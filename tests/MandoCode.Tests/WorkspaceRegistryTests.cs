using System.Reflection;
using HtmlAgilityPack;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
public class WorkspaceRegistryTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(40)]
    [InlineData(12)]
    public void AsciiArt_ClipsEachRowWithoutWrapping_AndPreservesLiteralText(int width)
    {
        var lines = new[] { " ███╗   ███╗ █████╗ ███╗   ██╗", "[literal] ░▒▓░░▒▓▒░░▒▓░░▒▓▒", " ▓░▒▓░░░▒▓░▒░░▓▒ v0.16.0" };
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = width;
        console.Write(new AsciiArtRenderable(lines));
        var output = writer.ToString().Replace("\r", "").TrimEnd('\n').Split('\n');
        Assert.Equal(lines.Length, output.Length);
        for (var row = 0; row < lines.Length; row++)
            Assert.Equal(lines[row][..Math.Min(lines[row].Length, width)], output[row]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ConfigFlow_WithMultipleAgents_EnterCompletesActualMenu(int count)
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        for (var i = 0; i < count; i++) registry.Active.Add();
        var selected = registry.Active.SelectedPane!;
        var rendererType = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")!;
        var instance = ActivatorUtilities.CreateInstance(services, rendererType, new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        await using var renderer = (IAsyncDisposable)instance;
        RenderFragment<AgentPane> body = pane => builder =>
        {
            builder.OpenComponent<ConfigFlowApp>(0);
            builder.AddComponentReferenceCapture(1, component => pane.PresentationState["config-app"] = component);
            builder.CloseComponent();
        };
        var mount = rendererType.GetMethods().Single(method => method.Name == "MountComponentAsync" && method.IsGenericMethodDefinition);
        await (Task)mount.MakeGenericMethod(typeof(TerminalWorkspaceView)).Invoke(instance, new object[]
        {
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }), CancellationToken.None
        })!;
        var dispatcher = (Dispatcher)rendererType.GetProperty("Dispatcher")!.GetValue(instance)!;
        var app = (ConfigFlowApp)selected.PresentationState["config-app"];
        Task? pending = null;
        await dispatcher.InvokeAsync(() => { pending = app.OpenConfig(); });
        await Task.Delay(350);
        var snapshot = rendererType.GetMethod("RefreshSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null)!;
        var root = (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        var menu = Flatten(root).Single(node => (node.Attributes.GetValueOrDefault("data-focus-key") ?? "").StartsWith("approval-"));
        Assert.Equal("true", menu.Attributes.GetValueOrDefault("data-focusable"));
        Assert.Equal(selected.FocusKey, menu.Attributes["data-focus-key"]);
        Assert.Equal(selected.FocusKey, menu.Key);
        var focus = services.GetRequiredService<RazorConsole.Core.Focus.FocusManager>();
        typeof(RazorConsole.Core.Focus.FocusManager).GetMethod("UpdateFocusTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(focus, new[] { snapshot });
        await focus.FocusAsync(selected.FocusKey!);
        Assert.True(focus.IsFocused(menu.Key!));
        var handler = menu.Events.Single(evt => evt.Name == "onkeydown").HandlerId;
        // Choose "View current configuration" in the actual /config menu.
        var dispatch = rendererType.GetMethod("DispatchEventAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(ulong), typeof(EventArgs) })!;
        await (Task)dispatch.Invoke(instance, new object[] { handler, new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" } })!;
        await (Task)dispatch.Invoke(instance, new object[] { handler, new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" } })!;
        await pending!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(selected, registry.Active.SelectedPane);
        Assert.True(selected.Active);
    }

    public sealed class ConfigFlowApp : App
    {
        protected override void OnInitialized()
        {
            base.OnInitialized();
            typeof(App).GetField("_showPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
        }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task OpenConfig() => (Task)typeof(App).GetMethod("HandleConfigCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectorOwnsEnter_AndOnlyActivePaneIsFocusable(bool active)
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var first = workspace.Add();
        workspace.Add();
        if (active) workspace.Focus(first);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = builder =>
            {
                builder.OpenComponent<ApprovalSelect>(0);
                builder.AddAttribute(1, "Options", new[] { new ApprovalSelect.Option("Cancel", Color.Grey) });
                builder.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<CascadingValue<AgentPane>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = first, ["ChildContent"] = child
            }));
            var document = new HtmlDocument();
            document.LoadHtml(rendered.ToHtmlString());
            var menu = document.DocumentNode.SelectSingleNode("//*[@data-input-managed='true']");
            Assert.NotNull(menu);
            Assert.Equal(active ? "true" : "false", menu.GetAttributeValue("data-focusable", ""));
        });
    }

    [Fact]
    public async Task MenuEnterAndStaleInput_PreserveSelectedAgent()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var inactive = workspace.Add();
        var selected = workspace.Add();
        var enter = new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" };
        var submitted = 0;
        var menu = new ApprovalSelect { Pane = inactive };
        typeof(ApprovalSelect).GetProperty(nameof(ApprovalSelect.Options))!.SetValue(menu, new[] { new ApprovalSelect.Option("Cancel", Color.Grey) });
        typeof(ApprovalSelect).GetProperty(nameof(ApprovalSelect.OnSubmit))!.SetValue(menu, EventCallback.Factory.Create<string>(this, _ => submitted++));
        var menuKey = typeof(ApprovalSelect).GetMethod("HandleKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)menuKey.Invoke(menu, new object[] { enter })!;
        Assert.Equal(0, submitted);
        Assert.Same(selected, workspace.SelectedPane);
        var prompt = new PromptInput { Pane = inactive };
        var preview = typeof(PromptInput).GetMethod("PreviewKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(await (Task<bool>)preview.Invoke(prompt, new object[] { enter })!);
        var submit = typeof(PromptInput).GetMethod("HandleSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)submit.Invoke(prompt, new object[] { "/config" })!;
        Assert.Same(selected, workspace.SelectedPane);
        menu.Pane = selected;
        await (Task)menuKey.Invoke(menu, new object[] { enter })!;
        Assert.Equal(1, submitted);
        Assert.True(selected.Active);
        Assert.False(inactive.Active);
    }

    [Theory]
    [InlineData("# branch.head main\0# branch.oid abcdef123456\0", "main", false, false, 0, 0)]
    [InlineData("# branch.head main\0# branch.ab +2 -1\0? new file.txt\0", "main", true, false, 2, 1)]
    [InlineData("# branch.head feature/test\0u UU conflict\0", "feature/test", true, true, 0, 0)]
    [InlineData("# branch.head (detached)\0# branch.oid abcdef123456\0", "abcdef1", false, false, 0, 0)]
    [InlineData("# branch.head main\02 R. renamed\0u original file name\0", "main", true, false, 0, 0)]
    public void GitStatus_ParsesDesktopStates(string output, string branch, bool dirty, bool conflict, int ahead, int behind)
    {
        var status = GitPaneStatus.Parse(output);
        Assert.NotNull(status);
        Assert.Equal(branch, status.Branch);
        Assert.Equal(dirty, status.Dirty);
        Assert.Equal(conflict, status.Conflicted);
        Assert.Equal(ahead, status.Ahead);
        Assert.Equal(behind, status.Behind);
    }

    [Fact]
    public void GitStatus_HidesUnavailableRepository() => Assert.Null(GitPaneStatus.Parse(""));

    [Fact]
    public async Task TipsAutocomplete_FollowsSavedPreference()
    {
        await using var services = Services();
        var pane = services.GetRequiredService<WorkspaceRegistry>().Active.Add();
        var config = pane.Services.GetRequiredService<MandoCodeConfig>();
        var input = pane.Services.GetRequiredService<InputStateMachine>();
        Assert.True(config.ShowTips);
        Assert.Contains("/tips-off", input.GetAllCommands());
        Assert.DoesNotContain("/tips-on", input.GetAllCommands());
        Assert.True(ConfigKeySetter.TrySet(config, "showTips", "off").Ok);
        Assert.Contains("/tips-on", input.GetAllCommands());
        Assert.DoesNotContain("/tips-off", input.GetAllCommands());
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>(System.Text.Json.JsonSerializer.Serialize(config))!.ShowTips);
        Assert.False(ConfigKeySetter.TrySet(config, "showTips", "invalid").Ok);
        Assert.True(ConfigKeySetter.TrySet(config, "showTips", "on").Ok);
        Assert.Contains("/tips-off", input.GetAllCommands());
    }

    [Fact]
    public async Task DimmingAutocomplete_FollowsCurrentSetting_AndPreferenceIsPersisted()
    {
        await using var services = Services();
        var pane = services.GetRequiredService<WorkspaceRegistry>().Active.Add();
        var config = pane.Services.GetRequiredService<MandoCodeConfig>();
        var input = pane.Services.GetRequiredService<InputStateMachine>();
        Assert.True(config.DimUnfocusedAgents);
        Assert.Contains("/agent-dim-off", input.GetAllCommands());
        Assert.DoesNotContain("/agent-dim-on", input.GetAllCommands());
        Assert.DoesNotContain("/agent-dim", input.GetAllCommands());
        Assert.True(ConfigKeySetter.TrySet(config, "dimUnfocusedAgents", "off").Ok);
        Assert.Contains("/agent-dim-on", input.GetAllCommands());
        Assert.DoesNotContain("/agent-dim-off", input.GetAllCommands());
        var restored = System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        Assert.False(restored.DimUnfocusedAgents);
    }

    [Fact]
    public async Task NarrowPane_WrapsTranscriptToItsWidth_AndDimmingCanBeDisabled()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var pane = workspace.Add();
        workspace.Add();
        pane.Width = 32;
        var config = pane.Services.GetRequiredService<MandoCodeConfig>();
        Assert.True(ConfigKeySetter.TrySet(config, "dimUnfocusedAgents", "off").Ok);
        Assert.False(PaneColors.Muted(pane));
        Assert.Equal(Color.Blue, PaneColors.For(pane, Color.Blue));
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>(System.Text.Json.JsonSerializer.Serialize(config))!.DimUnfocusedAgents);
        Assert.True(ConfigKeySetter.TrySet(config, "dimUnfocusedAgents", "on").Ok);
        Assert.True(PaneColors.Muted(pane));
        Assert.False(ConfigKeySetter.TrySet(config, "dimUnfocusedAgents", "invalid").Ok);
        var words = Enumerable.Range(1, 20).Select(i => $"word{i:00}").ToArray();
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = 120;
        console.Write(PaneColors.Render(MarkdownHtmlRenderer.BuildRenderable(string.Join(" ", words)), pane));
        var output = writer.ToString();
        Assert.All(words, word => Assert.Contains(word, output));
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 30, line));
    }

    [Fact]
    public async Task UnfocusedColors_RestoreOriginalOutputAndKeepErrorsVisible()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var pane = workspace.Add();
        var rich = PaneColors.Render(new Markup("[blue]reply[/] [red]error[/]"), pane);
        string PaintColor(Spectre.Console.Rendering.IRenderable renderable)
        {
            var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor });
            console.Profile.Width = 120;
            console.Write(renderable);
            return writer.ToString();
        }
        var original = PaintColor(rich);
        workspace.Add();
        Assert.Equal(Color.Grey62, PaneColors.For(pane, Color.Blue));
        Assert.Equal(Color.Red, PaneColors.For(pane, Color.Red));
        var muted = PaintColor(rich);
        Assert.NotEqual(original, muted);
        Assert.Contains("reply", muted);
        Assert.Contains("error", muted);
        workspace.Focus(pane);
        Assert.Equal(Color.Blue, PaneColors.For(pane, Color.Blue));
        Assert.Equal(original, PaintColor(rich));
    }

    [Fact]
    public async Task PaneModelButton_FocusesItsAgentAndDoesNotInterruptBusyInput()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var first = workspace.Add();
        workspace.Add();
        string? command = null;
        first.SubmitCommand = value => { command = value; return Task.CompletedTask; };
        var component = new AgentPaneContent();
        typeof(AgentPaneContent).GetProperty("Pane")!.SetValue(component, first);
        var click = typeof(AgentPaneContent).GetMethod("PickModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        first.IsBusy = () => true;
        await (Task)click.Invoke(component, null)!;
        Assert.Null(command);
        first.IsBusy = () => false;
        first.IsAwaitingInput = () => true;
        await (Task)click.Invoke(component, null)!;
        Assert.Null(command);
        first.IsAwaitingInput = () => false;
        await (Task)click.Invoke(component, null)!;
        Assert.Equal("/model", command);
        Assert.Same(first, workspace.SelectedPane);
    }

    [Fact]
    public async Task RenameAgent_PreservesHistoryAndRefreshesPromptIdentity()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var pane = registry.Active.Add();
        var ai = pane.Services.GetRequiredService<AIService>();
        ai.AppendAssistantNote("Existing conversation");
        Assert.True(pane.Workspace.Rename(pane, " Jetik [dev] "));
        var config = pane.Services.GetRequiredService<MandoCodeConfig>();
        await ai.RefreshSettingsAsync(config);
        Assert.Equal("Jetik [dev]", pane.Name);
        Assert.Equal(pane.Name, config.AgentName);
        var prompt = (string)typeof(AIService).GetField("_systemPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
        Assert.Contains("You are Jetik [dev],", prompt);
        var history = (IEnumerable<Microsoft.Extensions.AI.ChatMessage>)typeof(AIService).GetField("_chatHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
        Assert.Contains(history, message => message.Text == "Existing conversation");
        Assert.Contains(history, message => message.Role == Microsoft.Extensions.AI.ChatRole.System && message.Text!.Contains("You are Jetik [dev],"));
        var other = registry.Add("Other").SelectedPane!;
        Assert.False(other.Workspace.Rename(other, "jetik [dev]"));
        Assert.False(other.Workspace.Rename(other, " "));
        Assert.False(other.Workspace.Rename(other, "line\nbreak"));
        Assert.False(other.Workspace.Rename(other, new string('x', 61)));
        Assert.Equal(pane.Name, config.AgentName);
    }

    [Fact]
    public async Task CallsignsAndNumbering_AreUniqueAndPersistThePreference()
    {
        Assert.True(new MandoCodeConfig().UseAgentNames);
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        var config = first.Services.GetRequiredService<MandoCodeConfig>();
        Assert.True(ConfigKeySetter.TrySet(config, "agentNaming", "names").Ok);
        var named = registry.Active.Add();
        Assert.Contains(named.Name, AgentCallsigns.Pool);
        var other = registry.Add("Other").SelectedPane!;
        Assert.Contains(other.Name, AgentCallsigns.Pool);
        Assert.NotEqual(named.Name, other.Name);
        var otherConfig = other.Services.GetRequiredService<MandoCodeConfig>();
        Assert.Equal(other.Name, otherConfig.AgentName);
        var ai = other.Services.GetRequiredService<AIService>();
        var prompt = (string)typeof(AIService).GetField("_systemPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
        Assert.Contains($"You are {other.Name},", prompt);
        Assert.True(ConfigKeySetter.TrySet(otherConfig, "agentNaming", "numbers").Ok);
        Assert.Equal("Agent 2", registry.Active.Add().Name);
        Assert.Contains(other.Name, AgentCallsigns.Pool);
        Assert.False(ConfigKeySetter.TrySet(otherConfig, "agentNaming", "invalid").Ok);
        var saved = System.Text.Json.JsonSerializer.Serialize(otherConfig);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>(saved)!.UseAgentNames);
        Assert.DoesNotContain("\"AgentName\"", saved);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddRazorConsoleServices();
        services.AddSingleton<ITerminalViewport>(new Viewport());
        services.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(services, new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task EightAgents_HaveIndependentScopesAndFourPerWorkspace()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active;
        for (var i = 0; i < 4; i++) main.Add();
        main.SelectedPane!.Services.GetRequiredService<MandoCodeConfig>().ModelName = "inherited-model";
        var tests = registry.Add("Tests");
        for (var i = 1; i < 4; i++) tests.Add();
        Assert.Equal("inherited-model", tests.Panes[0].Model);
        var extra = tests.Add();
        Assert.Equal(4, tests.Panes.Count);
        Assert.Same(tests.SelectedPane, extra);
        var panes = registry.Workspaces.SelectMany(w => w.Panes).ToArray();
        Assert.Equal(8, panes.Length);
        Assert.Equal(8, panes.Select(p => p.Id).Distinct().Count());
        Assert.Equal(8, panes.Select(p => p.Services.GetRequiredService<AIService>()).Distinct().Count());
        Assert.Null(panes[0].Services.GetRequiredService<AgentIdentity>().CheckpointId);
        Assert.All(panes.Skip(1), p => Assert.NotNull(p.Services.GetRequiredService<AgentIdentity>().CheckpointId));
        Assert.DoesNotContain(main.Panes, p => p.Active);
        Assert.Single(tests.Panes, p => p.Active);
        tests.Panes[0].Services.GetRequiredService<MandoCodeConfig>().ModelName = "other-model";
        Assert.Equal("inherited-model", main.SelectedPane.Model);
        registry.Switch(main);
        Assert.True(main.SelectedPane.Active);
        Assert.DoesNotContain(tests.Panes, p => p.Active);
    }

    [Fact]
    public async Task Switching_PreservesPendingApprovalAndRoutesBackgroundOutput()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        var approval = first.Services.GetRequiredService<ApprovalSelectCoordinator>();
        var pending = approval.RequestAsync([new("Approve", Color.Green)]);
        var second = registry.Add("Tests").SelectedPane!;
        using (TuiConsole.Enter(first.Session)) TuiConsole.Write("background main output");
        using (TuiConsole.Enter(second.Session)) TuiConsole.Write("tests output");
        Assert.Single(first.Session.Snapshot().Entries);
        Assert.Single(second.Session.Snapshot().Entries);
        Assert.False(pending.IsCompleted);
        Assert.True(approval.IsActive);
        registry.Switch(first.Workspace);
        Assert.False(pending.IsCompleted);
        approval.Submit("Approve");
        Assert.Equal("Approve", await pending);
    }

    [Fact]
    public async Task CommandsAndShortcuts_RenameSwitchAndProtectWorkspaceClosure()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        Assert.True(first.Workspace.Command(first, "/workspace-new Tests"));
        var second = registry.Active.SelectedPane!;
        Assert.True(second.Workspace.Command(second, "/workspace-rename Unit tests"));
        Assert.Equal("Unit tests", registry.Active.Name);
        second.IsBusy = () => true;
        registry.Close();
        Assert.Equal(2, registry.Workspaces.Count());
        second.IsBusy = () => false;
        Assert.True(second.Workspace.Key(second, new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true, ShiftKey = true }));
        Assert.Same(first.Workspace, registry.Active);
        first.Workspace.Command(first, "/workspace Unit tests");
        Assert.Same(second.Workspace, registry.Active);
        registry.Close();
        Assert.Single(registry.Workspaces);
        Assert.True(second.Workspace.IsClosed);
        Assert.Equal(2, registry.Entries.Count); // Stable positions, no sibling permutations.
        registry.Close();
        Assert.Single(registry.Workspaces);
        first.Workspace.Key(first, new KeyboardEventArgs { Key = "N", AltKey = true, ShiftKey = true });
        Assert.Equal(2, registry.Workspaces.Count());
        Assert.Equal(3, registry.Active.Id);
    }

    [Fact]
    public async Task LiveRenderer_SwitchingAndClosingWorkspace_PreservesMountedAgents()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active;
        for (var i = 0; i < 4; i++) main.Add();
        var tests = registry.Add("Tests");
        for (var i = 1; i < 4; i++) tests.Add();
        var rendererType = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")!;
        var instance = ActivatorUtilities.CreateInstance(services, rendererType,
            new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        await using var renderer = (IAsyncDisposable)instance;
        RenderFragment<AgentPane> body = pane => builder =>
        {
            builder.OpenComponent<AgentWorkspaceTests.MountedTestApp>(0);
            builder.CloseComponent();
        };
        var mount = rendererType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "MountComponentAsync" && m.IsGenericMethodDefinition);
        await (Task)mount.MakeGenericMethod(typeof(TerminalWorkspaceView)).Invoke(instance,
            new object[] { ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }), CancellationToken.None })!;
        var dispatcher = (Dispatcher)rendererType.GetProperty("Dispatcher")!.GetValue(instance)!;
        VNode Snapshot()
        {
            var snapshot = rendererType.GetMethod("RefreshSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null)!;
            return (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        }
        var root = Snapshot();
        Assert.Equal(8, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        using (TuiConsole.Enter(main.Panes[0].Session)) TuiConsole.Write("BACKGROUND_MAIN_MESSAGE");
        using (TuiConsole.Enter(tests.Panes[0].Session)) TuiConsole.Write("VISIBLE_TESTS_MESSAGE");
        await Task.Delay(150); // ChatShell refreshes its snapshot on an 80ms timer.
        root = Snapshot();
        Assert.Contains("VISIBLE_TESTS_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("BACKGROUND_MAIN_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("Agent 1", Paint(services, root));
        Assert.Contains("Agent 5", Paint(services, root));
        var hidden = Flatten(root).Single(n => n.Attributes.GetValueOrDefault("data-workspace-hidden") == "true");
        Assert.DoesNotContain(Flatten(hidden), n => n.Attributes.GetValueOrDefault("data-focusable") == "true" || n.Events.Count > 0);
        await dispatcher.InvokeAsync(() => registry.Switch(main));
        root = Snapshot();
        Assert.Equal(8, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains("Agent 1", Paint(services, root));
        Assert.DoesNotContain("Agent 5", Paint(services, root));
        Assert.Contains("BACKGROUND_MAIN_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("VISIBLE_TESTS_MESSAGE", Paint(services, root));
        Assert.All(tests.Panes, p => Assert.NotNull(p.Services.GetRequiredService<AIService>()));
        Assert.All(main.Panes, p => p.IsBusy = () => false);
        await dispatcher.InvokeAsync(() => registry.Close());
        root = Snapshot();
        Assert.Equal(4, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains("Agent 5", Paint(services, root));
        await dispatcher.InvokeAsync(() => registry.Add("Docs"));
        root = Snapshot();
        Assert.Equal(5, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains(registry.Active.SelectedPane!.Name, Paint(services, root));
        Assert.DoesNotContain("Agent 5", Paint(services, root));
    }

    [Fact]
    public async Task WorkspaceTabs_PreservePendingInputAndComposerDraftAcrossSwitch()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder =>
            {
                builder.OpenComponent<DraftHost>(0);
                builder.AddAttribute(1, "Pane", pane);
                builder.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            var prompt = (PromptInput)first.PresentationState["test-prompt"];
            prompt.SetValue("unfinished workspace draft");
            var approval = first.Services.GetRequiredService<ApprovalSelectCoordinator>();
            var pending = approval.RequestAsync([new("Approve", Color.Green)]);
            registry.Add("Tests");
            await Task.Yield();
            Assert.Contains("Main", root.ToHtmlString());
            registry.Switch(first.Workspace);
            await Task.Yield();
            Assert.Contains("unfinished workspace draft", root.ToHtmlString());
            Assert.False(pending.IsCompleted);
            approval.Submit("Approve");
        });
    }

    public sealed class DraftHost : ComponentBase
    {
        [Parameter] public AgentPane Pane { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (!Pane.Visible) return;
            builder.OpenComponent<PromptInput>(0);
            builder.AddComponentReferenceCapture(1, prompt => Pane.PresentationState["test-prompt"] = prompt);
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task PlanEditor_PreservesUnsubmittedTextAcrossWorkspaceSwitch()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder =>
            {
                builder.OpenComponent<EditorHost>(0);
                builder.AddAttribute(1, "Pane", pane);
                builder.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            var editor = (PlanStepEditor)first.PresentationState["test-editor"];
            var buffer = (RazorConsole.Core.Input.TextSelectionState)typeof(PlanStepEditor).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
            buffer.SetText("Unsubmitted revised plan instructions");
            registry.Add("Tests");
            await Task.Yield();
            registry.Switch(first.Workspace);
            await Task.Yield();
            Assert.Contains("Unsubmitted revised plan instructions", root.ToHtmlString());
            Assert.NotSame(editor, first.PresentationState["test-editor"]);
        });
    }

    public sealed class EditorHost : ComponentBase
    {
        [Parameter] public AgentPane Pane { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (!Pane.Visible) return;
            builder.OpenComponent<PlanStepEditor>(0);
            builder.AddAttribute(1, "InitialValue", "Original plan instructions");
            builder.AddAttribute(2, "PersistenceKey", "pending-plan-edit");
            builder.AddComponentReferenceCapture(3, editor => Pane.PresentationState["test-editor"] = editor);
            builder.CloseComponent();
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    [InlineData(45)]
    public async Task NineWorkspaceTabs_KeepActionsVisibleAndRevealSelection(int width)
    {
        await using var services = Services();
        ((Viewport)services.GetRequiredService<ITerminalViewport>()).Width = width;
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        for (var i = 2; i <= 9; i++) registry.Add($"Workspace {i} with an extremely long name");
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder => { builder.AddContent(0, $"Body {pane.Id}"); };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            string Header()
            {
                var document = new HtmlDocument();
                document.LoadHtml(root.ToHtmlString());
                var vnode = VNode.CreateRegion();
                foreach (var child in document.DocumentNode.ChildNodes)
                    if (ConvertNode(child) is { } converted) vnode.AddChild(converted);
                return Paint(services, vnode).Split('\n')[0].TrimEnd('\r');
            }
            var header = Header();
            Assert.Contains("Workspace 9", header);
            Assert.Contains("…", header);
            Assert.DoesNotContain("[white]", header);
            Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(header) <= width, header);
            if (width >= 70)
            {
                Assert.Contains(" All ", header);
                Assert.Contains(" + New ", header);
                Assert.Contains(" × Close ", header);
                Assert.True(header.IndexOf("× Close", StringComparison.Ordinal) >= width - 9, header);
            }
            else
            {
                Assert.Contains(" ≡ ", header);
                Assert.Contains(" + ", header);
                Assert.Contains(" × ", header);
            }
            registry.Switch(registry.Workspaces.First());
            await Task.Yield();
            Assert.Contains("Main", Header());
            Assert.Equal(9, registry.Workspaces.Count());
        });
    }

    [Fact]
    public async Task TabWindow_ScrollsWithoutSwitchingAgents_AndFollowsNewSelection()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        for (var i = 2; i <= 9; i++) registry.Add($"Workspace {i}");
        var items = registry.Workspaces.ToArray();
        var window = new WorkspaceTabWindow();
        window.Update(items, registry.Active, 66);
        Assert.True(window.Includes(8));
        window.Scroll(-1, items.Length);
        window.Update(items, registry.Active, 66);
        Assert.False(window.Includes(8));
        Assert.Same(items[8], registry.Active);
        registry.Switch(items[0]);
        window.Update(items, registry.Active, 66);
        Assert.True(window.Includes(0));
        window.Update(items, registry.Active, 10);
        Assert.True(window.Includes(0));
        Assert.Equal(1, window.Capacity);
        Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(WorkspaceTabWindow.Fit("模型 workspace 🎨", 7)) <= 7);
    }

    private static VNode? ConvertNode(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text) return VNode.CreateText(System.Net.WebUtility.HtmlDecode(node.InnerText));
        if (node.NodeType != HtmlNodeType.Element) return null;
        var converted = VNode.CreateElement(node.Name);
        foreach (var attribute in node.Attributes) converted.SetAttribute(attribute.Name, System.Net.WebUtility.HtmlDecode(attribute.Value));
        foreach (var child in node.ChildNodes) if (ConvertNode(child) is { } nested) converted.AddChild(nested);
        return converted;
    }

    [Fact]
    public async Task AllWorkspacePicker_FiltersSwitchesAndCancelsWithoutChangingWorkspace()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        var docs = registry.Add("Docs");
        var tests = registry.Add("Tests");
        registry.IsWorkspacePickerOpen = true;
        Assert.DoesNotContain(tests.Panes, p => p.Active);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            WorkspacePicker picker = default!;
            var closed = 0;
            RenderFragment content = builder =>
            {
                builder.OpenComponent<WorkspacePicker>(0);
                builder.AddAttribute(1, "OnClose", EventCallback.Factory.Create(this, () => { closed++; registry.IsWorkspacePickerOpen = false; registry.Active.Focus(registry.Active.SelectedPane!); }));
                builder.AddComponentReferenceCapture(2, instance => picker = (WorkspacePicker)instance);
                builder.CloseComponent();
            };
            await renderer.RenderComponentAsync<PickerHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = content }));
            var query = (RazorConsole.Core.Input.TextSelectionState)typeof(WorkspacePicker).GetField("_query", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)!;
            query.SetText("Docs");
            typeof(WorkspacePicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(picker, null);
            var key = typeof(WorkspacePicker).GetMethod("Key", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task<bool>)key.Invoke(picker, new object[] { new KeyboardEventArgs { Key = "Enter" } })!;
            Assert.Same(docs, registry.Active);
            Assert.True(docs.SelectedPane!.Active);
            Assert.False(registry.IsWorkspacePickerOpen);
            Assert.Equal(1, closed);
            registry.IsWorkspacePickerOpen = true;
            await (Task<bool>)key.Invoke(picker, new object[] { new KeyboardEventArgs { Key = "Escape" } })!;
            Assert.Same(docs, registry.Active);
            Assert.Equal(2, closed);
        });
    }
    public sealed class PickerHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }

    [Fact]
    public async Task WorkspaceAll_CommandAndToggle_RestoreAgentFocusAndRespectBusyClosure()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active.Add();
        Assert.True(main.Workspace.Command(main, "/workspace-all"));
        Assert.True(registry.IsWorkspacePickerOpen);
        Assert.False(main.Active);
        registry.TogglePicker();
        Assert.False(registry.IsWorkspacePickerOpen);
        Assert.True(main.Active);
        registry.TogglePicker();
        Assert.True(registry.IsWorkspacePickerOpen);
        Assert.True(main.Workspace.Key(main, new KeyboardEventArgs { Key = "A", AltKey = true, ShiftKey = true }));
        Assert.False(registry.IsWorkspacePickerOpen);
        var other = registry.Add("Tests").SelectedPane!;
        other.IsBusy = () => true;
        Assert.True(other.Workspace.Key(other, new KeyboardEventArgs { Key = "W", AltKey = true, ShiftKey = true }));
        Assert.Equal(2, registry.Workspaces.Count());
        other.IsBusy = () => false;
        other.Workspace.Key(other, new KeyboardEventArgs { Key = "W", AltKey = true, ShiftKey = true });
        Assert.Single(registry.Workspaces);
        Assert.Same(main.Workspace, registry.Active);
    }

    [Fact]
    public async Task CommandSuggestions_KeepKeyboardShortcutVisibleInNarrowPane()
    {
        await using var services = Services();
        ((Viewport)services.GetRequiredService<ITerminalViewport>()).Width = 45;
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<CommandSuggestionRow>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Command"] = "/workspace-new", ["Description"] = SlashCommands.All["/workspace-new"]
            }));
            Assert.Contains("Alt+Shift+N", System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
        });
    }
    private static IEnumerable<VNode> Flatten(VNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    private static string Paint(IServiceProvider services, VNode node)
    {
        var width = services.GetRequiredService<ITerminalViewport>().Width;
        var layout = new LayoutEngine().Layout(services.GetRequiredService<WidgetTranslationContext>().Translate(node), new BoxConstraints(width, width, 30, 30));
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = width;
        console.Write(layout.PaintToRenderable());
        return writer.ToString();
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width { get; set; } = 120;
        public int Height => 30;
        public event Action? OnResized { add { } remove { } }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
