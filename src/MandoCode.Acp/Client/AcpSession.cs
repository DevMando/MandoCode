using MandoCode.Acp.JsonRpc;
using MandoCode.Acp.Protocol;

namespace MandoCode.Acp.Client;

/// <summary>
/// One agent session (<c>session/new</c>). A session runs one turn at a time.
/// </summary>
public sealed class AcpSession
{
    private readonly JsonRpcConnection _connection;
    private readonly IReadOnlyList<AuthMethod> _authMethods;
    private readonly TimeSpan _cancelGrace;
    private readonly object _gate = new();

    private AcpTurn? _activeTurn;
    private Task? _activePrompt;
    private CancellationTokenSource _permissionCancellation = new();

    internal AcpSession(
        JsonRpcConnection connection,
        string sessionId,
        SessionModeState? modes,
        IReadOnlyList<SessionConfigOption>? configOptions,
        AgentCapabilities agentCapabilities,
        IReadOnlyList<AuthMethod> authMethods,
        TimeSpan cancelGrace)
    {
        ConfigOptions = configOptions ?? [];
        _connection = connection;
        _authMethods = authMethods;
        _cancelGrace = cancelGrace;
        SessionId = sessionId;
        Modes = modes;
        CurrentModeId = modes?.CurrentModeId;
        AgentCapabilities = agentCapabilities;
    }

    public string SessionId { get; }

    /// <summary>What the agent can accept (images, embedded resources, ...): decide features from this.</summary>
    public AgentCapabilities AgentCapabilities { get; }

    /// <summary>The modes the agent offered in <c>session/new</c>, if any (shown, never switched, in this client).</summary>
    public SessionModeState? Modes { get; private set; }

    /// <summary>The mode id last announced by the agent.</summary>
    public string? CurrentModeId { get; private set; }

    /// <summary>The settings the agent exposes for this session (model, mode, ...), as last announced.</summary>
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; private set; }

    /// <summary>Where history replayed by <c>session/load</c> goes; null outside a load, when such updates are dropped.</summary>
    internal Action<SessionUpdate>? HistorySink { get; set; }

    /// <summary>
    /// Sets a select option (<c>session/set_config_option</c>) and adopts the full option list the agent answers with.
    /// Throws <see cref="JsonRpcException"/> when the agent refuses.
    /// </summary>
    public async Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default)
    {
        var response = await _connection.SendRequestAsync(
            AcpMethods.SessionSetConfigOption,
            new SetSessionConfigOptionRequest { SessionId = SessionId, ConfigId = configId, Value = value },
            AcpJsonContext.Default.SetSessionConfigOptionRequest,
            AcpJsonContext.Default.SetSessionConfigOptionResponse,
            cancellationToken).ConfigureAwait(false);

        ConfigOptions = response.ConfigOptions;
        StateChanged?.Invoke();
    }

    /// <summary>The slash commands the agent last announced.</summary>
    public IReadOnlyList<AvailableCommand> AvailableCommands { get; private set; } = [];

    /// <summary>Raised when the agent announces new commands or a new mode (on the notification thread).</summary>
    public event Action? StateChanged;

    /// <summary>True while a turn is running.</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _activeTurn is not null; }
    }

    /// <summary>
    /// Switches the session to <paramref name="modeId"/> (<c>session/set_mode</c>). Throws
    /// <see cref="JsonRpcException"/> when the agent refuses; the caller decides whether that matters.
    /// </summary>
    public async Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
    {
        await _connection.SendRequestAsync(
            AcpMethods.SessionSetMode,
            new SetSessionModeRequest { SessionId = SessionId, ModeId = modeId },
            AcpJsonContext.Default.SetSessionModeRequest,
            AcpJsonContext.Default.SetSessionModeResponse,
            cancellationToken).ConfigureAwait(false);

        CurrentModeId = modeId;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Sends a prompt and returns the turn immediately. Cancelling <paramref name="cancellationToken"/>
    /// is the same as calling <see cref="CancelAsync"/>.
    /// </summary>
    public AcpTurn Prompt(IReadOnlyList<ContentBlock> prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var turn = new AcpTurn();
        lock (_gate)
        {
            if (_activeTurn is not null)
                throw new InvalidOperationException("A turn is already running in this session.");
            _activeTurn = turn;
            _permissionCancellation = new CancellationTokenSource();
            _activePrompt = RunPromptAsync(turn, prompt);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => _ = CancelAsync());
            _ = turn.Completion.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        return turn;
    }

    private async Task RunPromptAsync(AcpTurn turn, IReadOnlyList<ContentBlock> prompt)
    {
        try
        {
            var response = await _connection.SendRequestAsync(
                AcpMethods.SessionPrompt,
                new PromptRequest { SessionId = SessionId, Prompt = prompt },
                AcpJsonContext.Default.PromptRequest,
                AcpJsonContext.Default.PromptResponse).ConfigureAwait(false);

            // The response is dispatched as soon as it is read; the updates that preceded it may still
            // be queued. Deliver them first so the stream never ends before the agent's last words.
            await _connection.DrainNotificationsAsync().ConfigureAwait(false);
            End(turn, t => t.Succeed(response.StopReason));
        }
        catch (JsonRpcException ex) when (ex.Code == AcpErrorCodes.RequestCancelled && turn.CancelRequested)
        {
            // Some agents answer a cancelled prompt with an error instead of stopReason "cancelled".
            await DrainQuietlyAsync().ConfigureAwait(false);
            End(turn, t => t.Succeed(StopReasons.Cancelled));
        }
        catch (JsonRpcException ex) when (ex.Code == AcpErrorCodes.AuthRequired)
        {
            End(turn, t => t.Fail(new AcpAuthRequiredException(ex.Message, _authMethods, ex)));
        }
        catch (Exception ex)
        {
            await DrainQuietlyAsync().ConfigureAwait(false);
            End(turn, t => t.Fail(ex));
        }
    }

    private async Task DrainQuietlyAsync()
    {
        try
        {
            await _connection.DrainNotificationsAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void End(AcpTurn turn, Action<AcpTurn> finish)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeTurn, turn))
            {
                _activeTurn = null;
                _activePrompt = null;
            }
        }

        finish(turn);
    }

    /// <summary>
    /// Cancels the running turn: pending permission requests are answered "cancelled", then
    /// <c>session/cancel</c> is sent, and the agent is given a grace period to end the turn.
    /// Updates that still arrive are delivered; the turn completes with stop reason <c>cancelled</c>.
    /// </summary>
    /// <returns>
    /// True when the turn ended within the grace period (or none was running). False means the agent
    /// ignored the cancel: the caller should shut the agent down.
    /// </returns>
    public async Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        AcpTurn? turn;
        Task? prompt;
        CancellationTokenSource permissions;
        lock (_gate)
        {
            turn = _activeTurn;
            prompt = _activePrompt;
            permissions = _permissionCancellation;
        }

        if (turn is null || prompt is null)
            return true;

        turn.CancelRequested = true;
        permissions.Cancel();

        try
        {
            await _connection.SendNotificationAsync(
                AcpMethods.SessionCancel,
                new CancelNotification { SessionId = SessionId },
                AcpJsonContext.Default.CancelNotification).ConfigureAwait(false);
        }
        catch (AcpConnectionClosedException)
        {
            return true; // The agent is gone; the turn fails with the connection error.
        }

        var finished = await Task.WhenAny(prompt, Task.Delay(_cancelGrace, cancellationToken)).ConfigureAwait(false);
        return finished == prompt;
    }

    // ───────────── called by AcpClient ─────────────

    internal void Receive(SessionUpdate update)
    {
        switch (update)
        {
            case AvailableCommandsUpdate commands:
                AvailableCommands = commands.AvailableCommands;
                StateChanged?.Invoke();
                break;
            case CurrentModeUpdate mode:
                CurrentModeId = mode.CurrentModeId;
                StateChanged?.Invoke();
                break;
            case ConfigOptionUpdate options:
                ConfigOptions = options.ConfigOptions;
                StateChanged?.Invoke();
                break;
        }

        if (HistorySink is { } history)
        {
            history(update);
            return;
        }

        AcpTurn? turn;
        lock (_gate)
            turn = _activeTurn;

        turn?.Push(update);
    }

    /// <summary>Takes the modes and options a <c>session/load</c> answered with.</summary>
    internal void Adopt(SessionModeState? modes, IReadOnlyList<SessionConfigOption>? configOptions)
    {
        if (modes is not null)
        {
            Modes = modes;
            CurrentModeId = modes.CurrentModeId;
        }

        if (configOptions is not null)
            ConfigOptions = configOptions;
        StateChanged?.Invoke();
    }

    /// <summary>The token a permission prompt must observe, or null when a cancel is already under way.</summary>
    internal CancellationToken? BeginPermission()
    {
        lock (_gate)
        {
            if (_activeTurn is { CancelRequested: true })
                return null;

            return _permissionCancellation.Token;
        }
    }
}
