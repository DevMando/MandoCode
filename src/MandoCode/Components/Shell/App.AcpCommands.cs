using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Spectre.Console;

namespace MandoCode.Components;

// Commands of an ACP pane that need the component: /clear, /model, /compact, folder changes, sessions.
// The decisions (what the agent offers, how a choice is matched) live in Services/Acp.
public partial class App
{
    private const string ChangeFolderLabel = "Change folder and restart the agent";
    private const string KeepFolderLabel = "Cancel";

    private AcpSessionStore? _acpSessionStore;
    private AcpHistoryCollector? _acpHistory;

    private AcpSessionStore AcpSessions => _acpSessionStore ??= new AcpSessionStore();

    private IAcpChoicePrompt AcpChoices =>
        new ApprovalMenuChoicePrompt(PromptGate, ApprovalMenu, () => KeyCoordinator.Suppress(), TuiConsole.WriteLine);

    private static void Say(string text, Color? color = null) => Print(new Text(text, new Style(color ?? Color.Yellow)));

    /// <summary>Commands whose meaning differs in an ACP pane. True when handled here.</summary>
    private async Task<bool> TryHandleAcpCommandAsync(string command, string arguments)
    {
        switch (command.Split(' ', 2)[0])
        {
            case "model": await RunAcpModelAsync(arguments); return true;
            case "compact": await RunAcpCompactAsync(); return true;
            case "clear": await ClearAcpConversationAsync(); return true;
            case "change-directory": await ChangeAcpFolderAsync(arguments); return true;
            default: return false;
        }
    }

    /// <summary>The running agent, or null with the reason already shown.</summary>
    private IAcpAgentRuntime? ReadyAcpRuntime()
    {
        if (_acpStart is { IsCompleted: false })
        {
            Say("The agent is still connecting. Try again when it is ready.");
            return null;
        }

        if (_acp is not { Status: AcpRuntimeStatus.Ready } runtime)
        {
            Say($"The agent is not running. {AcpErrorMessages.RestartHint}");
            return null;
        }

        return runtime;
    }

    private async Task RunAcpModelAsync(string arguments)
    {
        if (ReadyAcpRuntime() is not { } runtime)
            return;
        await AcpPaneCommands.ModelAsync(runtime, arguments, AcpChoices, text => Say(text, Color.Grey), CancellationToken.None);
        UpdateAcpHeader();
    }

    private async Task RunAcpModeAsync(string arguments)
    {
        if (HasActiveWork)
        {
            Say("Wait for the current request to finish before changing the agent mode.");
            return;
        }
        if (ReadyAcpRuntime() is not { } runtime) return;
        await AcpPaneCommands.ModeAsync(runtime, arguments, AcpChoices, text => Say(text, Color.Grey), CancellationToken.None);
        UpdateAcpHeader();
    }

    private async Task RunAcpCompactAsync()
    {
        if (ReadyAcpRuntime() is not { } runtime)
            return;
        if (!AcpPaneCommands.AgentOffersCompact(runtime))
        {
            Say(AcpPaneCommands.NoCompactMessage, Color.Grey);
            return;
        }

        // The agent's own command, sent as an ordinary prompt; no other agent command is forwarded.
        await ProcessAcpRequestAsync("/" + AcpPaneCommands.CompactCommand);
    }

    /// <summary>
    /// <c>/clear</c> in an ACP pane: a new ACP conversation in the same agent process and folder. The native
    /// conversation and its history are not touched.
    /// </summary>
    private async Task ClearAcpConversationAsync()
    {
        if (HasActiveWork)
        {
            Say("An ACP request is running. Cancel it with Escape, then run /clear again.");
            return;
        }

        if (ReadyAcpRuntime() is not { } runtime)
            return;

        try
        {
            await runtime.NewConversationAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"Could not start a new ACP conversation: {ex.Message} {AcpErrorMessages.RestartHint}", Color.Red);
            return;
        }

        _acpMessages.Clear();
        _lastCompletedReply = null;
        _lastRequestError = null;
        _acpTranscript?.Reset();
        TuiConsole.Clear();
        Say($"ACP conversation cleared: a new session with {Pane?.AcpAgentName}. The native conversation of this pane was not touched.", Color.Green);
        UpdateAcpHeader();
    }

    /// <summary>
    /// <c>/change-directory</c> and <c>!cd</c> in an ACP pane. The folder belongs to this pane only, but the agent
    /// was started in the old one, so the change restarts it (a new process and session) after the user agrees.
    /// </summary>
    private async Task ChangeAcpFolderAsync(string arguments)
    {
        if (HasActiveWork)
        {
            Say(BusyMessage);
            return;
        }

        string? target;
        try { target = arguments.Length > 0 ? arguments : await PickProjectDirectoryAsync(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
        {
            TuiConsole.WriteLine($"Couldn't open the folder picker: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(target))
            return;

        string directory;
        try { directory = Shell.ResolveDirectory(target); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            Say($"Couldn't change directory: {ex.Message}");
            return;
        }

        if (AcpPaths.SameFolder(directory, ProjectRoot.ProjectRoot))
        {
            Say("This pane already works in that folder.", Color.Grey);
            return;
        }

        var answer = await AcpChoices.ChooseAsync(
            $"Change this pane's folder to {directory}? The ACP agent restarts there and the ACP conversation is lost; the native conversation is not affected.",
            [new AcpChoice(ChangeFolderLabel, Color.Green), new AcpChoice(KeepFolderLabel, new Color(255, 200, 80))]);
        if (answer != ChangeFolderLabel)
        {
            Say("Folder not changed.", Color.Grey);
            return;
        }

        Shell.ChangeDirectory(directory);
        _acpMessages.Clear();
        _lastCompletedReply = null;
        _lastRequestError = null;
        Say($"── Folder changed to {directory}. The ACP agent restarts there with a new conversation. ──", Color.Grey);
        await RestartAcpAsync();
            }

    /// <summary>A shell line typed in the prompt (<c>!cmd</c>, <c>/command cmd</c>): a folder change in an ACP pane goes through the folder flow.</summary>
    private async Task RunShellLineAsync(string command)
    {
        if (IsAcpPane && ShellCommandHandler.TryParseCd(command, out var target))
        {
            await ChangeAcpFolderAsync(target);
            return;
        }

        await Shell.HandleShellCommandAsync(command);
    }

    // ───────────── sessions ─────────────

    /// <summary>The session to continue for <c>--resume</c>, from what MandoCode remembers for this agent and folder.</summary>
    private AcpResumeRequest? TakeResumeRequest()
    {
        if (Pane is not { AcpResume: true } pane || pane.AcpAgentName is not { } name)
            return null;

        pane.AcpResume = false;
        if (!Config.AllowPersistence)
        {
            Say("Persistence is turned off in the settings, so no earlier session is remembered. Starting a new one.");
            return null;
        }

        var root = ProjectRoot.ProjectRoot;
        if (AcpSessions.Get(name, root) is { } stored && AcpPaths.SameFolder(stored.Cwd, root))
            return new AcpResumeRequest(stored.SessionId);

        Say("No earlier session of this agent is remembered for this folder. Starting a new one.");
        return null;
    }

    private void RememberAcpSession(string sessionId)
    {
        if (!Config.AllowPersistence || Pane?.AcpAgentName is not { } name)
            return;
        AcpSessions.Set(name, ProjectRoot.ProjectRoot, sessionId);
    }

    /// <summary>Says how the session started, and shows the replayed conversation when the agent loaded one.</summary>
    private void AnnounceSession(IAcpAgentRuntime runtime)
    {
        switch (runtime.SessionOrigin)
        {
            case AcpSessionOrigin.Resumed:
                Say("Continued the agent's earlier session. The agent keeps its context; earlier messages are not shown here.", Color.Grey);
                break;

            case AcpSessionOrigin.Loaded:
                var messages = _acpHistory?.Messages() ?? [];
                _acpMessages.AddRange(messages.Select(m => new ChatMsg { Role = m.Role, Text = m.Text }));
                Say($"── Continued the agent's earlier session: {messages.Count} earlier message(s) restored ──", Color.Grey);
                foreach (var message in messages)
                {
                    if (message.Role == "user")
                    {
                        Print(new Text("> " + message.Text, new Style(Color.Grey)));
                    }
                    else
                    {
                        PrintReplyHeading();
                        RenderMarkdownGuarded(message.Text);
                    }
                }

                if (messages.Count > 0)
                    _lastCompletedReply = messages.LastOrDefault(m => m.Role == "assistant")?.Text;
                Say("── end of restored conversation ──", Color.Grey);
                break;
        }

        _acpHistory = null;
    }

    /// <summary><c>/agent-acp sessions</c>: the agent's own list of sessions for this pane's folder.</summary>
    private async Task<string?> ListAcpSessionsAsync()
    {
        if (!IsAcpPane)
            return "Sessions are listed inside an ACP agent pane. Open one with /agent-acp <name>.";
        if (_acp is not { Status: AcpRuntimeStatus.Ready } runtime)
            return $"The agent is not running. {AcpErrorMessages.RestartHint}";
        if (!runtime.Support.CanList)
            return "This agent does not support listing sessions (session/list), so there is nothing to show.";

        IReadOnlyList<Acp.Protocol.SessionInfo> sessions;
        try
        {
            sessions = await runtime.ListSessionsAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is MandoCode.Acp.JsonRpcException or MandoCode.Acp.AcpTimeoutException or InvalidOperationException)
        {
            return $"Could not list the agent's sessions: {ex.Message}";
        }

        var remembered = Pane?.AcpAgentName is { } name ? AcpSessions.Get(name, ProjectRoot.ProjectRoot)?.SessionId : null;
        if (sessions.Count == 0)
        {
            TuiConsole.WriteLine("The agent has no sessions for this folder.");
            return null;
        }

        TuiConsole.WriteLine($"Sessions of {Pane?.AcpAgentName} in {ProjectRoot.ProjectRoot}:");
        foreach (var session in sessions.OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal))
        {
            var marks = string.Join(" ", new[]
            {
                session.SessionId == runtime.SessionId ? "(current)" : null,
                session.SessionId == remembered ? "(next --resume)" : null,
            }.Where(m => m is not null));
            TuiConsole.WriteLine($"  {session.SessionId}  {session.Title ?? "(untitled)"}  {session.UpdatedAt}  {marks}".TrimEnd());
        }

        return null;
    }
}
