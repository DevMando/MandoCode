using MandoCode;
using MandoCode.Components;
using MandoCode.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public class ExplorerPromptLayoutTests
{
    [Theory]
    [InlineData(70, false, 70)]
    [InlineData(120, true, 70)]
    public async Task ExplorerKeepsOnePromptAndSuppliesAvailableWidth(int width, bool conversationVisible, int composerWidth)
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-layout-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var registrations = new ServiceCollection().AddLogging();
            registrations.AddRazorConsoleServices();
            registrations.AddSingleton<ITerminalViewport>(new Viewport(width));
            Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, AllowPersistence = false }, root);
            await using var services = registrations.BuildServiceProvider();
            await using var scope = services.CreateAsyncScope();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, services.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ChatShell.ProjectDirectory)] = root,
                    [nameof(ChatShell.FileExplorerOpen)] = true,
                    [nameof(ChatShell.Header)] = (RenderFragment)(builder => builder.AddContent(0, "CONVERSATION_MARKER")),
                    [nameof(ChatShell.Footer)] = (RenderFragment)(builder => { builder.OpenComponent<WidthProbe>(0); builder.CloseComponent(); })
                }));
                return rendered.ToHtmlString();
            });
            Assert.Equal(conversationVisible, html.Contains("CONVERSATION_MARKER"));
            Assert.Equal(1, html.Split("PROMPT_MARKER", StringSplitOptions.None).Length - 1);
            Assert.Contains($"PROMPT_MARKER_{composerWidth}", html);
        }
        finally { Directory.Delete(root, true); }
    }

    public sealed class WidthProbe : ComponentBase
    {
        [CascadingParameter(Name = "AgentContentWidth")] public int Width { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, $"PROMPT_MARKER_{Width}");
    }
    private sealed class Viewport(int width) : ITerminalViewport
    {
        public int Width => width;
        public int Height => 30;
        public event Action? OnResized { add { } remove { } }
    }
}
