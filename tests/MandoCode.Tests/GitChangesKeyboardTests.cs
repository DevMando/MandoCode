using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public sealed class GitChangesKeyboardTests
{
    [Fact]
    public async Task TabAndArrowsReachFilesAndActionsAndEscapeCloses()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        AgentGitChanges panel = null!;
        var closed = 0;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<AgentGitChanges>)(value => panel = value),
                [nameof(Host.Close)] = (Action)(() => closed++)
            }));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(AgentGitChanges);
            type.GetField("_files", flags)!.SetValue(panel, new GitChangedFile[]
            {
                new("first.txt", " M", null, 1, 1, false), new("second.txt", " M", null, 2, 0, false)
            });
            async Task Key(string key, bool shift = false) => await (Task)type.GetMethod("Key", flags)!.Invoke(panel, [new KeyboardEventArgs { Key = key, ShiftKey = shift }])!;
            int Index(string field) => (int)type.GetField(field, flags)!.GetValue(panel)!;
            var scope = new ExplorerFocusScope { Active = true };
            scope.FocusPrompt = () => { scope.SetPromptFocused(true); return Task.CompletedTask; };
            type.GetProperty("ExplorerFocus", flags | BindingFlags.Public)!.SetValue(panel, scope);
            await (Task)type.GetMethod("OnParametersSetAsync", flags)!.Invoke(panel, null)!;
            scope.SetPromptFocused(false);
            Assert.Equal(0, Index("_toolbarIndex")); // Initial list starts on Refresh.
            await Key("Tab"); await Key("Tab", shift: true); await scope.ToggleAsync();
            Assert.Equal(0, Index("_toolbarIndex")); Assert.False(scope.PromptFocused);
            await Key("ArrowDown"); Assert.Equal(-1, Index("_toolbarIndex")); Assert.Equal(0, Index("_selected"));
            var background = type.GetMethod("RowBackground", flags)!;
            Assert.Equal(Spectre.Console.Color.DeepSkyBlue1, background.Invoke(panel, [0]));
            Assert.Equal(Spectre.Console.Color.Default, background.Invoke(panel, [1]));
            var captionMethod = type.GetMethod("RowCaption", flags)!;
            foreach (var status in new[] { " M", "??", " D", "A ", "R " })
            {
                var item = new GitChangedFile("src/very/long/directory/important-file.cs", status, null, 2, 1, false);
                var caption = (string)captionMethod.Invoke(panel, [item, 0])!;
                // Parse the actual styled row, rather than passing markup through an escaping text component.
                var rendered = new Spectre.Console.Markup(caption);
                Assert.Contains("important-file.cs", Spectre.Console.Markup.Remove(caption));
                Assert.DoesNotContain("[green]", Spectre.Console.Markup.Remove(caption));
                Assert.Contains("+2", Spectre.Console.Markup.Remove(caption));
            }
            await Key("ArrowDown"); Assert.Equal(1, Index("_selected"));
            Assert.Equal(Spectre.Console.Color.DeepSkyBlue1, background.Invoke(panel, [1]));
            Assert.Equal(Spectre.Console.Color.Default, background.Invoke(panel, [0]));
            await Key("ArrowUp"); Assert.Equal(0, Index("_selected"));
            await Key("ArrowUp"); Assert.Equal(0, Index("_toolbarIndex")); // Refresh
            await Key("ArrowRight"); Assert.Equal(0, Index("_toolbarIndex")); // One list action.
            await Key("ArrowDown"); Assert.Equal(-1, Index("_toolbarIndex"));
            await Key("Escape");
            Assert.Equal(1, closed);
            type.GetField("_file", flags)!.SetValue(panel, new GitChangedFile("first.txt", " M", null, 1, 1, false));
            type.GetField("_toolbarIndex", flags)!.SetValue(panel, -1);
            await Key("ArrowRight"); await Key("ArrowRight"); Assert.Equal(2, Index("_toolbarIndex")); // Discard
            var toolbar = (System.Collections.IEnumerable)type.GetProperty("Toolbar", flags)!.GetValue(panel)!;
            var labels = toolbar.Cast<object>().Select(action => (string)action.GetType().GetField("Item1")!.GetValue(action)!).ToArray();
            Assert.Equal(new[] { " ‹ Back ", " Refresh ", " Discard Changes " }, labels);
            await Key("Tab"); Assert.Equal(0, Index("_toolbarIndex"));
            await Key("Tab", shift: true); Assert.Equal(2, Index("_toolbarIndex"));
            await scope.ToggleAsync(); Assert.Equal(0, Index("_toolbarIndex"));
            await scope.ToggleAsync(reverse: true); Assert.Equal(2, Index("_toolbarIndex"));
            await Key("ArrowLeft"); Assert.Equal(1, Index("_toolbarIndex"));
            await Key("ArrowLeft"); Assert.Equal(0, Index("_toolbarIndex"));
            for (var index = 1; index <= 2; index++) { await Key("ArrowRight"); Assert.Equal(index, Index("_toolbarIndex")); }
            await Key("Enter");
            Assert.True((bool)type.GetField("_confirm", flags)!.GetValue(panel)!);
            await Key("Escape");
            await Key("ArrowLeft"); await Key("ArrowLeft"); Assert.Equal(0, Index("_toolbarIndex")); // Back
            await Key("Enter");
            Assert.Null(type.GetField("_file", flags)!.GetValue(panel));
            Assert.Equal(1, closed); // Back keeps Changes open.
        });
    }
    [Fact]
    public async Task OpeningDiffSelectsBackAndRefreshShowsTemporarySuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mandocode-git-keyboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("init"); start.ArgumentList.Add("--quiet");
            using (var process = System.Diagnostics.Process.Start(start)!) { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
            await File.WriteAllTextAsync(Path.Combine(directory, "new.txt"), "hello\n");
            var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
            Program.RegisterAgentServices(registrations, new MandoCodeConfig { AllowPersistence = false }, directory);
            await using var services = registrations.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                AgentGitChanges panel = null!;
                await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host.Root)] = directory, [nameof(Host.Capture)] = (Action<AgentGitChanges>)(value => panel = value) }));
                var type = typeof(AgentGitChanges); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                async Task Key(string key) => await (Task)type.GetMethod("Key", flags)!.Invoke(panel, [new KeyboardEventArgs { Key = key }])!;
                var feedbackElapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.FeedbackDelay = (duration, token) =>
                {
                    Assert.Equal(TimeSpan.FromSeconds(2), duration);
                    return feedbackElapsed.Task.WaitAsync(token);
                };
                await Key("ArrowDown"); await Key("Enter");
                Assert.NotNull(type.GetField("_file", flags)!.GetValue(panel));
                Assert.Equal(0, type.GetField("_toolbarIndex", flags)!.GetValue(panel));
                await Key("ArrowRight");
                var refreshing = Key("Enter");
                Assert.True((bool)type.GetField("_refreshing", flags)!.GetValue(panel)!);
                await refreshing;
                Assert.False((bool)type.GetField("_refreshing", flags)!.GetValue(panel)!);
                Assert.Equal("File refreshed", type.GetField("_refreshMessage", flags)!.GetValue(panel));
                feedbackElapsed.SetResult();
                await panel.FeedbackCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("", type.GetField("_refreshMessage", flags)!.GetValue(panel));
            });
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
    public sealed class Host : ComponentBase
    {
        [Parameter] public Action<AgentGitChanges>? Capture { get; set; }
        [Parameter] public Action? Close { get; set; }
        [Parameter] public string Root { get; set; } = "";
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<AgentGitChanges>(0);
            builder.AddAttribute(1, "Width", 60);
            builder.AddAttribute(2, "Height", 20);
            builder.AddAttribute(3, "OnClose", EventCallback.Factory.Create(this, () => Close?.Invoke()));
            builder.AddAttribute(4, "Root", Root);
            builder.AddComponentReferenceCapture(5, component => Capture?.Invoke((AgentGitChanges)component));
            builder.CloseComponent();
        }
    }
}
