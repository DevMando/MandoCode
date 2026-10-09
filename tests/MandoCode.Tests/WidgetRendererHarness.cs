using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RazorConsole.Core;
using RazorConsole.Core.Vdom;

namespace MandoCode.Tests;

/// <summary>One boundary for RazorConsole's internal renderer API. Product assertions
/// should inspect the rendered tree rather than repeat renderer reflection.</summary>
internal sealed class WidgetRendererHarness : IAsyncDisposable
{
    private readonly object _renderer;
    private readonly Type _type;
    private WidgetRendererHarness(IServiceProvider services)
    {
        _type = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer", throwOnError: true)!;
        _renderer = ActivatorUtilities.CreateInstance(services, _type,
            new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        Dispatcher = (Dispatcher)_type.GetProperty("Dispatcher")!.GetValue(_renderer)!;
    }

    public Dispatcher Dispatcher { get; }

    public static async Task<WidgetRendererHarness> MountAsync<T>(IServiceProvider services, Dictionary<string, object?> parameters) where T : IComponent
    {
        var harness = new WidgetRendererHarness(services);
        try
        {
            var mount = harness._type.GetMethods().Single(method => method.Name == "MountComponentAsync" && method.IsGenericMethodDefinition);
            await (Task)mount.MakeGenericMethod(typeof(T)).Invoke(harness._renderer, [ParameterView.FromDictionary(parameters), CancellationToken.None])!;
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public object Snapshot() => _type.GetMethod("RefreshSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_renderer, null)!;
    public static VNode Root(object snapshot) => (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
    public static IEnumerable<VNode> Flatten(VNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child)) yield return descendant;
    }

    public async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await Dispatcher.InvokeAsync(condition))
            await Task.Delay(10, timeout.Token);
    }

    public ValueTask DisposeAsync() => ((IAsyncDisposable)_renderer).DisposeAsync();
}
