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

    /// <summary>Startup of an ACP pane: no Ollama, no model check, no MCP; just the agent.</summary>
    private async Task InitializeAcpAsync()
    {
        _isConnected = true;   // nothing in an ACP pane depends on Ollama
        try { await StartAcpRuntimeAsync(); }
        finally
        {
            _connectionChecked = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task StartAcpRuntimeAsync()
    {
        var name = Pane!.AcpAgentName!;
        if (AcpEntry is not { } entry)
        {
            Print(new Text($"No ACP agent named '{name}' is configured any more. Add it with /agent-acp add, then use /agent-acp restart.", new Style(Color.Yellow)));
            UpdateAcpHeader();
            return;
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
        Spinner.Start($"Starting {name}...");
        try
        {
            await runtime.ConnectAsync(_acpStartCts.Token);
            TuiConsole.WriteLine($"Connected to {runtime.AgentTitle ?? name}" + (runtime.CurrentMode is { } mode ? $" (mode: {mode})." : "."));
            foreach (var warning in runtime.StartupWarnings)
                Print(new Text("Warning: " + warning, new Style(Color.Yellow)));
        }
        catch (AcpRuntimeException ex)
        {
            Print(new Text(ex.Message, new Style(Color.Red)));
        }
        catch (OperationCanceledException)
        {
            // The pane was closed while the agent was starting.
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
        parts.Add(_acp?.AgentTitle ?? "(not connected)");
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
        var old = _acp;
        _acp = null;
        if (Pane is not null) Pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = null;
        if (old is not null)
        {
            old.Changed -= OnAcpChanged;
            await old.DisposeAsync();
        }

        await StartAcpRuntimeAsync();
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
            if (_acp is null)
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
