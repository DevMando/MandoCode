using System.Text.RegularExpressions;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.JsonRpc;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

public enum AcpRuntimeStatus
{
    NotStarted,
    Starting,
    Ready,

    /// <summary>Startup failed, or the agent went away.</summary>
    Failed,

    /// <summary>Shut down on purpose (pane closed, restart, unresponsive agent).</summary>
    Closed,
}

/// <summary>Startup failed; <see cref="Exception.Message"/> is written for the user and ready to print.</summary>
public sealed class AcpRuntimeException : Exception
{
    public AcpRuntimeException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>One external agent attached to one pane. The pane's service scope owns it; disposing it ends the process tree.</summary>
public interface IAcpAgentRuntime : IAsyncDisposable
{
    string AgentName { get; }
    AcpRuntimeStatus Status { get; }

    /// <summary>The agent's own name and version from <c>initialize</c> (for the adapter this is the adapter's version).</summary>
    string? AgentTitle { get; }

    /// <summary>Human-readable agent identity for the pane header, without adapter version details.</summary>
    string? AgentDisplayName => AgentTitle;

    string? CurrentMode { get; }

    /// <summary>Non-fatal things that happened while starting (for example a refused <c>session/set_mode</c>).</summary>
    IReadOnlyList<string> StartupWarnings { get; }

    /// <summary>What the agent accepts in a prompt; decide what to send from this.</summary>
    PromptCapabilities PromptCapabilities { get; }

    /// <summary>The current session's id, once connected.</summary>
    string? SessionId { get; }

    /// <summary>The settings (model, mode, ...) the agent exposes for the current session.</summary>
    IReadOnlyList<SessionConfigOption> ConfigOptions { get; }

    /// <summary>The slash commands the agent announced for the current session.</summary>
    IReadOnlyList<AvailableCommand> AvailableCommands { get; }

    /// <summary>Which session features the connected agent offers.</summary>
    AcpSessionSupport Support { get; }

    /// <summary>How the current session was obtained (new, resumed, loaded).</summary>
    AcpSessionOrigin SessionOrigin { get; }

    /// <summary>Raised with the id of every session this runtime starts, including after <see cref="NewConversationAsync"/>.</summary>
    event Action<string>? SessionStarted;

    /// <summary>Raised when the status, mode or title changes (on any thread).</summary>
    event Action? Changed;

    /// <summary>
    /// A new conversation in the same process and folder: closes the current session when the agent supports it, then
    /// <c>session/new</c> and the start mode. Throws <see cref="InvalidOperationException"/> while a turn is running.
    /// </summary>
    /// <returns>True when the old session was closed on the agent; false when the agent cannot close sessions.</returns>
    Task<bool> NewConversationAsync(CancellationToken cancellationToken);

    /// <summary>Sets a select option; throws <see cref="JsonRpcException"/> when the agent refuses it.</summary>
    Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken);

    /// <summary>The agent's sessions for this pane's folder (needs <see cref="AcpSessionSupport.CanList"/>).</summary>
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Trust check, launch, <c>initialize</c>, <c>session/new</c> and the start mode. Throws <see cref="AcpRuntimeException"/>.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    IAcpTurn Prompt(IReadOnlyList<ContentBlock> prompt);

    /// <summary>False when the agent ignored the cancel for too long; the caller then disposes the runtime.</summary>
    Task<bool> CancelAsync();
}

/// <summary>The real runtime: <see cref="AcpClient"/> over a process started from an <c>acpAgents</c> entry.</summary>
public sealed class AcpAgentRuntime : IAcpAgentRuntime
{
    private readonly AcpAgentConfig _config;
    private readonly string _workingDirectory;
    private readonly AcpAgentLauncher _launcher;
    private readonly Func<AcpLaunchPlan, Task<bool>> _confirm;
    private readonly string? _logDirectory;
    private readonly IAcpClientHandler _handler;
    private readonly Action<string, string>? _extensionDump;
    private readonly AcpResumeRequest? _resume;
    private readonly Action<SessionUpdate>? _history;
    private readonly List<string> _warnings = [];
    private readonly object _gate = new();

    private AcpClient? _client;
    private StderrLog? _stderrLog;
    private StderrLog? _traceLog;
    private AcpSession? _session;
    private AcpRuntimeStatus _status;
    private string? _title;

    public AcpAgentRuntime(
        string agentName,
        AcpAgentConfig config,
        string workingDirectory,
        AcpAgentLauncher launcher,
        Func<AcpLaunchPlan, Task<bool>> confirm,
        string? logDirectory = null,
        IAcpClientHandler? handler = null,
        Action<string, string>? extensionDump = null,
        AcpResumeRequest? resume = null,
        Action<SessionUpdate>? history = null)
    {
        _extensionDump = extensionDump;
        _resume = resume;
        _history = history;
        _handler = handler ?? new DenyAllClientHandler();
        AgentName = agentName;
        _config = config;
        _workingDirectory = workingDirectory;
        _launcher = launcher;
        _confirm = confirm;
        _logDirectory = logDirectory;
    }

    public string AgentName { get; }
    public string? AgentTitle => Volatile.Read(ref _title);
    public string? AgentDisplayName => _client?.Initialized?.AgentInfo is { } info
        ? (string.IsNullOrWhiteSpace(info.Title) ? info.Name : info.Title)
        : null;
    public string? CurrentMode => _session?.CurrentModeId;
    public IReadOnlyList<string> StartupWarnings { get { lock (_gate) return _warnings.ToArray(); } }
    public PromptCapabilities PromptCapabilities => _session?.AgentCapabilities.PromptCapabilities ?? new PromptCapabilities();
    public string? SessionId => _session?.SessionId;
    public IReadOnlyList<SessionConfigOption> ConfigOptions => _session?.ConfigOptions ?? [];
    public IReadOnlyList<AvailableCommand> AvailableCommands => _session?.AvailableCommands ?? [];
    public AcpSessionOrigin SessionOrigin { get; private set; }
    public event Action<string>? SessionStarted;
    public event Action? Changed;

    public AcpSessionSupport Support
    {
        get
        {
            var capabilities = _client?.Initialized?.AgentCapabilities ?? _session?.AgentCapabilities;
            return capabilities is null
                ? AcpSessionSupport.None
                : new AcpSessionSupport(
                    capabilities.SessionCapabilities.CanList(), capabilities.SessionCapabilities.CanResume(),
                    capabilities.LoadSession, capabilities.SessionCapabilities.CanClose());
        }
    }

    public AcpRuntimeStatus Status
    {
        get { lock (_gate) return _status; }
    }

    private void SetStatus(AcpRuntimeStatus status)
    {
        lock (_gate)
        {
            // Closed is final: a late "connection ended" must not turn it back into Failed.
            if (_status == AcpRuntimeStatus.Closed)
                return;
            _status = status;
        }

        Changed?.Invoke();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_status is AcpRuntimeStatus.Starting or AcpRuntimeStatus.Ready)
                throw new InvalidOperationException("The agent is already running.");
            _status = AcpRuntimeStatus.Starting;
            _warnings.Clear();
        }

        Changed?.Invoke();

        if (_config.Disabled)
            throw Fail(new AcpRuntimeException($"ACP agent '{AgentName}' is disabled. Enable it with /agent-acp enable {AgentName}."));

        AcpLaunchPlan plan;
        try
        {
            plan = _launcher.Prepare(AgentName, _config, _workingDirectory);
        }
        catch (AcpLaunchException ex)
        {
            throw Fail(new AcpRuntimeException(AcpErrorMessages.Startup(ex, AgentName, _config, "", null), ex));
        }

        if (!await _launcher.EnsureTrustedAsync(plan, _confirm).ConfigureAwait(false))
        {
            throw Fail(new AcpRuntimeException(
                $"ACP agent '{AgentName}' was not started: the launch was not approved. Use /agent-acp restart to be asked again."));
        }

        var stderr = new StderrLog(filePath: AcpLogPaths.For(AgentName, _logDirectory));
        // The file is only created by the first traced message, so an agent that sends nothing leaves no empty log.
        var trace = AcpLogPaths.TraceEnabled(_config) ? new StderrLog(filePath: AcpLogPaths.TraceFor(AgentName, _logDirectory)) : null;
        _stderrLog = stderr;
        _traceLog = trace;
        AcpClient? client = null;
        try
        {
            var transport = AcpProcessTransport.Start(plan.Spec, plan.Resolved, stderr);
            client = new AcpClient(transport, _handler, new AcpClientOptions
            {
                ClientName = "MandoCode",
                ClientVersion = typeof(AcpAgentRuntime).Assembly.GetName().Version?.ToString(3) ?? "0",
                StartupTimeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds),
                Trace = trace is null ? null : entry => trace.Append(entry.ToString()),
                ExtensionNotification = _extensionDump is not null && AcpLogPaths.ExtensionDumpEnabled()
                    ? (method, parameters) =>
                    {
                        var text = parameters?.ToString() ?? "";
                        _extensionDump(method, text.Length > 4000 ? text[..4000] + "…" : text);
                    }
                    : null,
            });
            _ = client.Completion.ContinueWith(_ =>
            {
                if (Status == AcpRuntimeStatus.Ready)
                    SetStatus(AcpRuntimeStatus.Failed);
            }, TaskScheduler.Default);

            var initialized = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _title, DescribeAgent(initialized));

            var (session, origin) = await OpenInitialSessionAsync(client, cancellationToken).ConfigureAwait(false);
            session.StateChanged += () => Changed?.Invoke();
            _session = session;
            SessionOrigin = origin;
            // A continued session keeps the mode it was left in.
            if (origin == AcpSessionOrigin.New)
                await ApplyStartModeAsync(session, cancellationToken).ConfigureAwait(false);

            lock (_gate)
                _client = client;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            lock (_gate)
                _status = AcpRuntimeStatus.Closed;
            Changed?.Invoke();
            throw;
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            _session = null;
            throw Fail(new AcpRuntimeException(
                AcpErrorMessages.Startup(ex, AgentName, _config, stderr.Tail(20), stderr.FilePath), ex));
        }

        SetStatus(AcpRuntimeStatus.Ready);
        SessionStarted?.Invoke(_session!.SessionId);
    }

    /// <summary>The earlier session when one was asked for and the agent can continue it; otherwise a new one (with a warning).</summary>
    private async Task<(AcpSession Session, AcpSessionOrigin Origin)> OpenInitialSessionAsync(AcpClient client, CancellationToken cancellationToken)
    {
        if (_resume is { } resume)
        {
            var capabilities = client.Initialized!.AgentCapabilities;
            try
            {
                if (capabilities.SessionCapabilities.CanResume())
                    return (await client.ResumeSessionAsync(resume.SessionId, _workingDirectory, cancellationToken).ConfigureAwait(false), AcpSessionOrigin.Resumed);

                if (capabilities.LoadSession)
                    return (await client.LoadSessionAsync(resume.SessionId, _workingDirectory, _history ?? (_ => { }), cancellationToken).ConfigureAwait(false), AcpSessionOrigin.Loaded);

                AddWarning("The agent can neither resume nor load sessions, so a new session was started.");
            }
            catch (Exception ex) when (ex is JsonRpcException or AcpTimeoutException)
            {
                AddWarning($"Could not continue the previous session ({ex.Message}); a new session was started.");
            }
        }

        return (await client.NewSessionAsync(_workingDirectory, cancellationToken).ConfigureAwait(false), AcpSessionOrigin.New);
    }

    public async Task<bool> NewConversationAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        var old = _session;
        if (Status != AcpRuntimeStatus.Ready || client is null || old is null)
            throw new InvalidOperationException("The agent is not running.");
        if (old.IsBusy)
            throw new InvalidOperationException("A request is still running.");

        var closed = false;
        if (Support.CanClose)
        {
            try
            {
                await client.CloseSessionAsync(old, cancellationToken).ConfigureAwait(false);
                closed = true;
            }
            catch (Exception ex) when (ex is JsonRpcException or AcpTimeoutException)
            {
                AddWarning($"The agent did not close the previous session ({ex.Message}).");
            }
        }

        var session = await client.NewSessionAsync(_workingDirectory, cancellationToken).ConfigureAwait(false);
        session.StateChanged += () => Changed?.Invoke();
        _session = session;
        SessionOrigin = AcpSessionOrigin.New;
        await ApplyStartModeAsync(session, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
        SessionStarted?.Invoke(session.SessionId);
        return closed;
    }

    public async Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken)
    {
        var session = _session;
        if (Status != AcpRuntimeStatus.Ready || session is null)
            throw new InvalidOperationException("The agent is not running.");
        await session.SetConfigOptionAsync(configId, value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (Status != AcpRuntimeStatus.Ready || client is null)
            throw new InvalidOperationException("The agent is not running.");

        var sessions = await client.ListSessionsAsync(_workingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        // The folder filter is the agent's to apply; do not trust it for what this pane may show or resume.
        return sessions.Where(info => AcpPaths.SameFolder(info.Cwd, _workingDirectory)).ToArray();
    }

    private static string? DescribeAgent(InitializeResponse initialized) =>
        initialized.AgentInfo is { } info
            ? string.Join(' ', new[] { info.Name, info.Version }.Where(part => !string.IsNullOrWhiteSpace(part)))
            : null;

    private async Task ApplyStartModeAsync(AcpSession session, CancellationToken cancellationToken)
    {
        var explicitMode = _config.StartMode;
        var target = explicitMode ?? "default";

        if (session.Modes is not { AvailableModes.Count: > 0 } modes)
        {
            if (explicitMode is not null)
                AddWarning($"The agent offers no session modes, so startMode '{explicitMode}' was ignored.");
            return;
        }

        if (string.Equals(session.CurrentModeId, target, StringComparison.Ordinal))
            return;

        if (!modes.AvailableModes.Any(mode => mode.Id == target))
        {
            // Without an explicit startMode this is just an agent that has no "default" mode: leave it alone.
            if (explicitMode is not null)
                AddWarning($"The agent has no mode '{target}' (offered: {string.Join(", ", modes.AvailableModes.Select(m => m.Id))}); it stays in '{session.CurrentModeId}'.");
            return;
        }

        var before = session.CurrentModeId;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.StartupTimeoutSeconds));
            await session.SetModeAsync(target, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not fatal: the session works, it just is not in the mode MandoCode wanted.
            AddWarning($"Could not switch the agent to mode '{target}' ({ex.Message}); it stays in '{before}'.");
        }
    }

    private void AddWarning(string warning)
    {
        lock (_gate)
            _warnings.Add(warning);
    }

    private AcpRuntimeException Fail(AcpRuntimeException exception)
    {
        SetStatus(AcpRuntimeStatus.Failed);
        return exception;
    }

    private static async Task DisposeQuietlyAsync(AcpClient? client)
    {
        if (client is null)
            return;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process tree is being killed anyway; there is nothing useful to report.
        }
    }

    public IAcpTurn Prompt(IReadOnlyList<ContentBlock> prompt)
    {
        var session = _session;
        if (Status != AcpRuntimeStatus.Ready || session is null)
            throw new InvalidOperationException("The agent is not running.");
        return session.Prompt(prompt);
    }

    public async Task<bool> CancelAsync()
    {
        var session = _session;
        return session is null || await session.CancelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        AcpClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _status = AcpRuntimeStatus.Closed;
        }

        _session = null;
        await DisposeQuietlyAsync(client).ConfigureAwait(false);
        // The agent is gone, so nothing writes to its log files any more; release them.
        _stderrLog?.Close();
        _traceLog?.Close();
        Changed?.Invoke();
    }
}

/// <summary>Turns protocol and launch failures into the messages the user sees (see the error table in docs/acp.md).</summary>
public static class AcpErrorMessages
{
    public const string RestartHint = "Use /agent-acp restart to try again.";

    private static readonly Regex AuthLike = new(
        @"\b(auth(?:enticat\w*|oriz\w*)?|log[ -]?in|sign[ -]?in|unauthori[sz]ed|credentials?|api[ _-]?key|token (?:expired|invalid)|401)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public const string DefaultAuthHint = "Sign in with the agent's own tooling (for Claude Code: `claude auth login`), then /agent-acp restart.";

    public static string Startup(Exception exception, string agentName, AcpAgentConfig config, string stderrTail, string? logFile)
    {
        var lines = new List<string>();
        switch (exception)
        {
            case AcpLaunchException launch:
                lines.Add($"Cannot start ACP agent '{agentName}': {launch.Message}");
                if (launch.Failure == AcpLaunchFailure.NotFound)
                    lines.Add($"Check \"command\" under acpAgents.{agentName} in ~/.mandocode/config.json.");
                break;
            case AcpTimeoutException timeout:
                lines.Add(timeout.Message);
                lines.Add(AcpConnectionProbe.NotAcpHint);
                break;
            case AcpConnectionClosedException closed:
                lines.Add(closed.ExitCode is { } code
                    ? $"The agent exited during startup (exit code {code})."
                    : "The agent closed the connection during startup.");
                break;
            case AcpProtocolViolationException:
                lines.Add("The agent wrote non-protocol output to stdout (it must write only ACP messages there).");
                lines.Add(AcpConnectionProbe.NotAcpHint);
                break;
            case AcpProtocolVersionException version:
                lines.Add(version.Message);
                break;
            case AcpAuthRequiredException auth:
                lines.Add($"The agent is not signed in ({auth.Message}).");
                lines.Add(config.AuthHint ?? DefaultAuthHint);
                break;
            case JsonRpcException rpc:
                lines.Add($"The agent rejected the request: {rpc.Message} (code {rpc.Code}).");
                if (LooksLikeAuthProblem(rpc))
                    lines.Add("This looks like a sign-in problem. " + (config.AuthHint ?? DefaultAuthHint));
                break;
            default:
                lines.Add($"Starting the agent failed: {exception.Message}");
                break;
        }

        AddStderr(lines, exception, stderrTail, logFile);
        if (exception is not AcpAuthRequiredException)
            lines.Add(RestartHint);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The text for a turn that failed. Null for nothing to say.</summary>
    public static string ForTurn(Exception exception, AcpAgentConfig? config)
    {
        var lines = new List<string>();
        switch (exception)
        {
            case AcpAuthRequiredException auth:
                lines.Add($"The agent is not signed in ({auth.Message}).");
                lines.Add(config?.AuthHint ?? DefaultAuthHint);
                return string.Join(Environment.NewLine, lines);
            case AcpConnectionClosedException closed:
                lines.Add(closed.ExitCode is { } code
                    ? $"The agent process ended in the middle of the request (exit code {code})."
                    : "The agent closed the connection in the middle of the request.");
                break;
            case AcpProtocolViolationException:
                lines.Add("The agent wrote non-protocol output to stdout (it must write only ACP messages there).");
                break;
            case JsonRpcException rpc:
                lines.Add($"The agent returned an error: {rpc.Message} (code {rpc.Code}).");
                if (LooksLikeAuthProblem(rpc))
                    lines.Add("This looks like a sign-in problem. " + (config?.AuthHint ?? DefaultAuthHint));
                break;
            default:
                lines.Add($"The request failed: {exception.Message}");
                break;
        }

        AddStderr(lines, exception, "", null);
        lines.Add(RestartHint);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A cautious guess: the real error Claude Code gives when it is not signed in is not known yet (see docs/acp.md),
    /// so anything that merely reads like an authentication failure gets the sign-in hint, worded as a possibility.
    /// </summary>
    public static bool LooksLikeAuthProblem(JsonRpcException exception)
    {
        if (exception.Code == AcpErrorCodes.AuthRequired)
            return true;

        try
        {
            return AuthLike.IsMatch(exception.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The user-facing sentence for a stop reason other than a normal end of turn; null when nothing needs saying.</summary>
    public static string? ForStopReason(string stopReason) => stopReason switch
    {
        StopReasons.EndTurn => null,
        StopReasons.Cancelled => "Request cancelled.",
        StopReasons.Refusal => "The agent refused to continue with this request (stop reason: refusal).",
        StopReasons.MaxTokens => "The agent stopped because it reached its output limit (stop reason: max_tokens).",
        StopReasons.MaxTurnRequests => "The agent stopped after reaching its limit of model requests for one turn (stop reason: max_turn_requests).",
        _ => $"The agent stopped with an unrecognised stop reason: {stopReason}.",
    };

    private static void AddStderr(List<string> lines, Exception exception, string stderrTail, string? logFile)
    {
        if (string.IsNullOrWhiteSpace(stderrTail) && exception is AcpConnectionClosedException closed)
            stderrTail = closed.StderrTail ?? "";

        if (!string.IsNullOrWhiteSpace(stderrTail))
        {
            lines.Add("Agent stderr (last lines):");
            lines.Add(stderrTail);
        }

        if (logFile is not null && File.Exists(logFile))
            lines.Add("Full agent stderr: " + logFile);
    }
}

/// <summary>Per-pane home of the pane's runtime: it lives in the pane's service scope, so closing the pane ends the agent's process tree.</summary>
public sealed class AcpRuntimeHolder : IAsyncDisposable
{
    /// <summary>True for an ACP pane: hides the commands that only make sense for MandoCode's own model loop.</summary>
    public bool IsAcpPane { get; set; }

    public IAcpAgentRuntime? Runtime { get; set; }

    public async ValueTask DisposeAsync()
    {
        var runtime = Runtime;
        Runtime = null;
        if (runtime is not null)
            await runtime.DisposeAsync().ConfigureAwait(false);
    }
}
