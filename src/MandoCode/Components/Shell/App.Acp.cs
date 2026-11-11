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
            TuiConsole.WriteLine($"Connecting to {name}... (you can type already; the prompt is sent once the agent is ready)");
        }

        public void Connected(string description, IReadOnlyList<string> warnings)
        {
            app.Spinner.Stop();
            TuiConsole.WriteLine($"Connected to {description}.");
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

        var runtime = new AcpAgentRuntime(
            name, entry, ProjectRoot.ProjectRoot,
            new AcpAgentLauncher(new FileTrustStore()),
            async plan =>
            {
                // The trust question owns the screen; the spinner would only fight it.
                Spinner.Stop();
                try { return await ConfirmAcpLaunchAsync(plan); }
                finally { Spinner.Start($"Starting {name}..."); }
            });
        _acp = runtime;
        Pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;
        runtime.Changed += OnAcpChanged;

        _acpStartCts?.Dispose();
        _acpStartCts = new CancellationTokenSource();
        UpdateAcpHeader();
        try
        {
            return await AcpPaneStartup.RunAsync(runtime, new StartupSink(this, name), _acpStartCts.Token);
        }
        finally
        {
            Spinner.Stop();
            TuiConsole.WriteLine();
            UpdateAcpHeader();
        }
    }

    private void OnAcpChanged() => UpdateAcpHeader();

    private void UpdateAcpHeader()
    {
        if (Pane is null || !IsAcpPane) return;

        var parts = new List<string> { "ACP" };
        if (Pane.Workspace.Panes.Count == 1) parts.Add(Pane.Name);
        parts.Add(_acp?.AgentTitle ?? (_acp?.Status == AcpRuntimeStatus.Starting ? "connecting…" : "(not connected)"));
        if (_acp?.CurrentMode is { } mode) parts.Add("mode: " + mode);
        var folder = Path.GetFileName(ProjectRoot.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        parts.Add(string.IsNullOrEmpty(folder) ? ProjectRoot.ProjectRoot : folder);
        if (_acp is { Status: AcpRuntimeStatus.Failed or AcpRuntimeStatus.Closed } || _acp is null) parts.Add("stopped");

        Pane.AcpHeader = string.Join(" · ", parts);
        Pane.Workspace.Refresh();
    }

    private static void Print(IRenderable renderable) => TuiConsole.Write(renderable);

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
        _messages.Add(new ChatMsg { Role = "user", Text = input });
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
            var driver = new AcpTurnDriver(new PaneSink(this), AcpEntry);
            var outcome = await driver.RunAsync(_acp, AcpPromptBuilder.Build(input, ProjectRoot.ProjectRoot), token);

            _lastRequestError = outcome.Error;
            if (outcome.ReplyText.Length > 0)
            {
                _lastCompletedReply = outcome.ReplyText;
                _messages.Add(new ChatMsg { Role = "assistant", Text = outcome.ReplyText });
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
        public void Preview(string? tail) => app.Spinner.UpdatePreview(tail);

        public void Activity(string label) => (app.Pane?.Session ?? TuiConsole.Current)?.SetActivity(label);

        public void Reply(string markdown)
        {
            app.PrintReplyHeading();
            app.RenderMarkdownGuarded(markdown);
        }

        public void Notice(string text) => Print(new Text(text, new Style(Color.Yellow)));

        public void Error(string text) => Print(new Text(text, new Style(Color.Red)));
    }

    /// <summary>What <c>/agent-acp &lt;name&gt;</c> and <c>/agent-acp restart</c> do in this pane.</summary>
    private sealed class PaneHost(App app) : IAcpPaneHost
    {
        public async Task<string?> OpenPaneAsync(string agentName)
        {
            if (app.Pane is null) return "Agent panes are not available here.";
            string? result = null;
            await app.InvokeAsync(() =>
            {
                var workspace = app.Pane.Workspace;
                var before = workspace.Panes.Count;
                var pane = workspace.Add(acpAgent: agentName);
                if (workspace.Panes.Count == before || pane.AcpAgentName != agentName)
                    result = "Four agent panes are already open. Close one first.";
            });
            return result;
        }

        public async Task<string?> RestartAsync()
        {
            if (!app.IsAcpPane) return "Restart works inside an ACP agent pane. Open one with /agent-acp <name>.";
            await app.RestartAcpAsync();
            return null;
        }
    }
}
