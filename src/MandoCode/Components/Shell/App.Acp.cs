using Microsoft.Extensions.DependencyInjection;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Components;

// ACP panes: the component is the glue between the pane and the agent runtime. The logic that is worth testing
// (turn handling, errors, cancellation) lives in Services/Acp (AcpTurnDriver, AcpAgentRuntime).
public partial class App
{
    private IAcpAgentRuntime? _acp;

    // The ACP conversation is kept apart from the native one (_messages): it is never saved to the agent history,
    // and the native conversation underneath is untouched while this pane runs an external agent.
    private readonly List<ChatMsg> _acpMessages = [];
    private bool _nativeInitialized;
    private AcpTranscriptAdapter? _acpTranscript;
    private AcpToolActivityStore? _acpTools;
    private CancellationTokenSource? _acpStartCts;

    private bool IsAcpPane => Pane?.Runtime == AgentRuntime.Acp;

    private AcpAgentConfig? AcpEntry =>
        Pane?.AcpAgentName is { } name && Config.AcpAgents.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>The agent is connecting in the background; the pane takes input meanwhile (a prompt waits for it).</summary>
    private Task<bool>? _acpStart;

    /// <summary>
    /// Startup of an ACP pane: no Ollama, no model check, no MCP; just the agent. The connection runs in the
    /// background so the pane never waits on the agent to take focus, input and Alt+arrows.
    /// </summary>
    private async Task InitializeAcpAsync()
    {
        _isConnected = true;   // nothing in an ACP pane depends on Ollama
        _connectionChecked = true;
        var session = Pane!.Session;
        _acpStart = Task.Run(async () =>
        {
            using var output = TuiConsole.Enter(session);
            return await StartAcpRuntimeAsync();
        });
        await InvokeAsync(StateHasChanged);
    }

    private sealed class StartupSink(App app, string name) : IAcpStartupSink
    {
        public void Connecting(string agentName)
        {
            app.Spinner.Start($"Connecting to {name}...");
            Print(new Text($"Connecting to {name}… You can type while the agent starts.", new Style(Color.Grey)));
        }

        public void Connected(string description, IReadOnlyList<string> warnings)
        {
            app.Spinner.Stop();
            if (app._acp is { } runtime) Print(AcpConnectionCard.Build(runtime));
            else TuiConsole.WriteLine($"Connected to {description}.");
            foreach (var warning in warnings)
                Print(new Text("Warning: " + warning, new Style(Color.Yellow)));
        }

        public void Failed(string message)
        {
            app.Spinner.Stop();
            Print(new Text(message, new Style(Color.Red)));
        }
    }

    private async Task<bool> StartAcpRuntimeAsync()
    {
        var name = Pane!.AcpAgentName!;
        if (AcpEntry is not { } entry)
        {
            Print(new Text($"No ACP agent named '{name}' is configured any more. Add it with /agent-acp add, then use /agent-acp restart.", new Style(Color.Yellow)));
            UpdateAcpHeader();
            return false;
        }

        var resume = TakeResumeRequest();
        _acpHistory = resume is null ? null : new AcpHistoryCollector();
        var runtime = new AcpAgentRuntime(
            name, entry, ProjectRoot.ProjectRoot,
            new AcpAgentLauncher(new FileTrustStore()),
            async plan =>
            {
                // The trust question owns the screen; the spinner would only fight it.
                Spinner.Stop();
                try { return await ConfirmAcpLaunchAsync(plan); }
                finally { Spinner.Start($"Starting {name}..."); }
            },
            handler: CreateAcpClientHandler(),
            extensionDump: (method, content) =>
            {
                // Debug only (MANDOCODE_ACP_DEBUG_EXT): on screen, never in a file.
                using var output = TuiConsole.Enter(Pane!.Session);
                Print(new Text($"[acp extension] {method}: {content}", new Style(Color.Grey)));
            },
            resume: resume,
            history: _acpHistory is { } history ? history.Add : null);
        _acp = runtime;
        Pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;
        runtime.Changed += OnAcpChanged;
        runtime.SessionStarted += RememberAcpSession;

        _acpStartCts?.Dispose();
        _acpStartCts = new CancellationTokenSource();
        UpdateAcpHeader();
        try
        {
            var started = await AcpPaneStartup.RunAsync(runtime, new StartupSink(this, name), _acpStartCts.Token);
            if (started)
                AnnounceSession(runtime);
            return started;
        }
        finally
        {
            Spinner.Stop();
            TuiConsole.WriteLine();
            UpdateAcpHeader();
        }
    }

    /// <summary>
    /// What the agent can call back into. These run on connection threads, so each one enters this pane's transcript
    /// first; the capabilities (fs, permissions) are advertised because the handlers exist.
    /// </summary>
    private AcpClientHandler CreateAcpClientHandler()
    {
        var session = Pane!.Session;
        var root = ProjectRoot.ProjectRoot;
        var renderer = (Pane.Services).GetRequiredService<OperationDisplayRenderer>();
        var transcript = _acpTranscript = new AcpTranscriptAdapter(renderer.BuildRenderable, root);
        var tools = _acpTools = new AcpToolActivityStore(session.UpdateAcpTool);

        var files = new AcpFileSystemHandler(root, change =>
        {
            using var output = TuiConsole.Enter(session);
            if (transcript.FileChanged(change) is { } diff)
                Print(diff);
        });

        var prompter = new AcpPermissionPrompter(
            PromptGate, ApprovalMenu, () => KeyCoordinator.Suppress(),
            line => { using var output = TuiConsole.Enter(session); TuiConsole.WriteLine(line); },
            () => _acp?.CurrentMode,
            pauseActivity: () =>
            {
                Spinner.Stop();
                return new ResumeSpinner(this);
            },
            decided: (toolCallId, allowed) =>
            {
                using var output = TuiConsole.Enter(session);
                transcript.NotePermission(toolCallId, allowed);
                tools.Permission(toolCallId, allowed);
            },
            requested: call => { tools.Apply(call); tools.Permission(call.ToolCallId, null); });

        return new AcpClientHandler(prompter, files);
    }

    /// <summary>Brings the spinner back after a question, unless the request ended meanwhile.</summary>
    private sealed class ResumeSpinner(App app) : IDisposable
    {
        public void Dispose()
        {
            if (app._requestCts is { IsCancellationRequested: false })
                app.Spinner.Start("Working...");
        }
    }

    private void OnAcpChanged() => UpdateAcpHeader();

    private void UpdateAcpHeader()
    {
        if (Pane is null || !IsAcpPane) return;

        var parts = new List<string>();
        parts.Add(_acp?.AgentDisplayName ?? (_acp?.Status == AcpRuntimeStatus.Starting ? "Connecting…" : "Not connected"));
        if (_acp is { } runtime && AcpPaneCommands.ModelOption(runtime) is { CurrentSelection: { } modelValue } modelOption)
            parts.Add(modelOption.Choices().FirstOrDefault(choice => choice.Value == modelValue)?.Name ?? modelValue);
        parts.Add("ACP");
        if (_acp is { Status: AcpRuntimeStatus.Failed or AcpRuntimeStatus.Closed } || _acp is null) parts.Add("stopped");

        Pane.AcpHeader = string.Join(" · ", parts);
        Pane.Workspace.Refresh();
    }

    private static void Print(IRenderable renderable) => TuiConsole.Write(renderable);

    /// <summary>Something of this pane is running or waiting for the user; a mode switch must not cut it off.</summary>
    private bool HasActiveWork =>
        _requestCts is not null || _runningPeerRequest is not null || _planSelectActive || ApprovalMenu.IsActive;

    private const string BusyMessage = "This agent is busy. Wait for it to finish (Escape cancels it), or use --new-panel to open the agent in another pane.";

    /// <summary>Kills the current ACP agent (its whole process tree) and forgets it; the pane stays in ACP mode.</summary>
    private async Task TearDownAcpAsync()
    {
        try { _acpStartCts?.Cancel(); } catch (ObjectDisposedException) { }
        var old = _acp;
        _acp = null;
        _acpStart = null;
        if (old is not null)
            old.Changed -= OnAcpChanged;
        if (Pane is not null)
            await AcpPaneMode.CloseSessionAsync(Pane);   // the holder owns the runtime and disposes it
        else if (old is not null)
            await old.DisposeAsync();
    }

    /// <summary>
    /// <c>/agent-acp &lt;name&gt;</c>: this pane runs the external agent from now on. The native session stays alive
    /// underneath, and nothing is carried over in either direction (a fresh start).
    /// </summary>
    private async Task<string?> SwitchToAcpAsync(string agentName, bool resume = false)
    {
        if (Pane is null) return "Agent panes are not available here.";
        if (HasActiveWork) return BusyMessage;
        if (IsAcpPane)
        {
            if (string.Equals(Pane.AcpAgentName, agentName, StringComparison.OrdinalIgnoreCase)
                && _acp is { Status: AcpRuntimeStatus.Ready or AcpRuntimeStatus.Starting })
                return $"This pane already runs '{agentName}'. Use /agent-acp restart to start it again.";
            await TearDownAcpAsync();
        }

        AcpPaneMode.Enter(Pane, agentName, resume);
        _acpMessages.Clear();
        Print(new Text(AcpPaneMode.EnterMessage(agentName, resume), new Style(Color.Grey)));

        var session = Pane.Session;
        _acpStart = Task.Run(async () =>
        {
            using var output = TuiConsole.Enter(session);
            return await StartAcpRuntimeAsync();
        });
        await InvokeAsync(StateHasChanged);
        return null;
    }

    /// <summary><c>/agent-acp stop</c>: closes the ACP session and gives the pane back to the native agent.</summary>
    private async Task<string?> StopAcpAsync()
    {
        if (Pane is null) return "Agent panes are not available here.";
        if (!IsAcpPane) return "This pane already runs the native agent. /agent-acp stop works in a pane that runs an ACP agent.";
        if (HasActiveWork) return BusyMessage;

        await TearDownAcpAsync();
        await AcpPaneMode.LeaveAsync(Pane);
        _acpMessages.Clear();
        Print(new Text("── Back to the native agent (the ACP conversation is not shared with it). ──", new Style(Color.Grey)));

        // A pane that was opened as an ACP pane never started its native side; do it now.
        if (!_nativeInitialized)
        {
            _nativeInitialized = true;
            await InitializeConnectionAsync();
        }

        await InvokeAsync(StateHasChanged);
        return null;
    }

    private async Task RestartAcpAsync()
    {
        try { _acpStartCts?.Cancel(); } catch (ObjectDisposedException) { }
        var old = _acp;
        _acp = null;
        if (Pane is not null) Pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = null;
        if (old is not null)
        {
            old.Changed -= OnAcpChanged;
            await old.DisposeAsync();
        }

        var start = StartAcpRuntimeAsync();
        _acpStart = start;
        await start;
    }

    /// <summary>One user message to the external agent. Mirrors ProcessDirectRequestAsync's frame; the turn itself is AcpTurnDriver.</summary>
    private async Task ProcessAcpRequestAsync(string input)
    {
        _lastRequestError = null;
        _lastCompletedReply = null;
        _acpMessages.Add(new ChatMsg { Role = "user", Text = input });
        (Pane?.Session ?? TuiConsole.Current)?.BeginToolTurn();
        TuiConsole.WriteLine();

        _requestCts = new CancellationTokenSource();
        var token = _requestCts.Token;
        StartCancelKeyListener();

        OpenReply();
        try
        {
            if (_acpStart is { IsCompleted: false } connecting)
            {
                Spinner.Start("Waiting for the agent to connect...");
                try { await connecting.WaitAsync(token); }
                catch (OperationCanceledException)
                {
                    Print(new Text("Request cancelled before the agent was ready.", new Style(Color.Yellow)));
                    return;
                }
            }

            if (_acp is null || _acp.Status != AcpRuntimeStatus.Ready)
            {
                _lastRequestError = "The agent is not running.";
                Print(new Text($"The agent is not running. {AcpErrorMessages.RestartHint}", new Style(Color.Yellow)));
                return;
            }

            Spinner.Start("Thinking...");
            var driver = new AcpTurnDriver(new PaneSink(this), AcpEntry, _acpTranscript);
            var outcome = await driver.RunAsync(_acp, AcpPromptBuilder.Build(input, ProjectRoot.ProjectRoot), token);

            _lastRequestError = outcome.Error;
            if (outcome.ReplyText.Length > 0)
            {
                _lastCompletedReply = outcome.ReplyText;
                _acpMessages.Add(new ChatMsg { Role = "assistant", Text = outcome.ReplyText });
            }
        }
        catch (Exception ex)
        {
            _lastRequestError = ex.Message;
            Print(new Text($"Error: {ex.Message}", new Style(Color.Red)));
        }
        finally
        {
            Spinner.Stop();
            TuiConsole.WriteLine();
            CloseReply();
            (Pane?.Session ?? TuiConsole.Current)?.CompleteToolTurn();
            StopCancelKeyListener();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
            UpdateAcpHeader();
        }
    }

    private sealed class PaneSink(App app) : IAcpTurnSink
    {
        public void BeginToolActivities() => app._acpTools?.BeginTurn();
        public bool ToolCall(MandoCode.Acp.Protocol.ToolCallBase call)
        {
            if (app._acpTools is not { } tools) return false;
            tools.Apply(call);
            return true;
        }
        public void ToolDetail(string id, IRenderable detail) => app._acpTools?.Detail(id, detail);
        public void EndToolActivities(string status) => app._acpTools?.EndTurn(status);
        public void Preview(string? tail) => app.Spinner.UpdatePreview(tail);

        public void Activity(string label) => (app.Pane?.Session ?? TuiConsole.Current)?.SetActivity(label);

        public void Reply(string markdown)
        {
            app.PrintReplyHeading();
            app.RenderMarkdownGuarded(markdown);
        }

        public void Block(IRenderable block) => Print(block);

        public void Notice(string text) => Print(new Text(text, new Style(Color.Yellow)));

        public void Error(string text) => Print(new Text(text, new Style(Color.Red)));
    }

    /// <summary>What <c>/agent-acp &lt;name&gt;</c> and <c>/agent-acp restart</c> do in this pane.</summary>
    private sealed class PaneHost(App app) : IAcpPaneHost
    {
        public async Task<string?> OpenPaneAsync(string agentName, bool resume = false)
        {
            if (app.Pane is null) return "Agent panes are not available here.";
            string? result = null;
            await app.InvokeAsync(() =>
            {
                var workspace = app.Pane.Workspace;
                var before = workspace.Panes.Count;
                var pane = workspace.Add(acpAgent: agentName, acpResume: resume);
                if (workspace.Panes.Count == before || pane.AcpAgentName != agentName)
                    result = "Four agent panes are already open. Close one first.";
            });
            return result;
        }

        public Task<string?> SwitchAsync(string agentName, bool resume = false) => app.SwitchToAcpAsync(agentName, resume);

        public Task<string?> SessionsAsync() => app.ListAcpSessionsAsync();

        public Task<string?> StopAsync() => app.StopAcpAsync();

        public async Task<string?> RestartAsync()
        {
            if (!app.IsAcpPane) return "Restart works inside an ACP agent pane. Open one with /agent-acp <name>.";
            await app.RestartAcpAsync();
            return null;
        }
    }
}
