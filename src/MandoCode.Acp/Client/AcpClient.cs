using System.Collections.Concurrent;
using MandoCode.Acp.JsonRpc;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;

namespace MandoCode.Acp.Client;

public sealed class AcpClientOptions
{
    public string ClientName { get; init; } = "MandoCode";
    public string ClientVersion { get; init; } = "0.0.0";

    /// <summary>How long <c>initialize</c> and <c>session/new</c> may take (agents can be slow to boot).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an agent may take to end a turn after <c>session/cancel</c> before it is considered unresponsive.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Diagnostics sink (ignored frames, unknown sessions). Never write this to the console.</summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>Optional message trace (direction, method, id, size; never content). Off when null.</summary>
    public Action<AcpTraceEntry>? Trace { get; init; }
}

/// <summary>
/// An ACP v1 client over one transport. Protocol only: no UI, no configuration, no trust decisions.
/// <para>
/// Features are decided from what the agent reports in <c>initialize</c>; unknown protocol additions
/// (new fields, new <c>session/update</c> kinds) are ignored.
/// </para>
/// Disposing the client disposes the transport, which shuts the agent process down.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    private readonly IAcpTransport _transport;
    private readonly IAcpClientHandler _handler;
    private readonly AcpClientOptions _options;
    private readonly JsonRpcConnection _connection;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public AcpClient(IAcpTransport transport, IAcpClientHandler handler, AcpClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _options = options ?? new AcpClientOptions();

        _connection = new JsonRpcConnection(transport.Input, transport.Output, new JsonRpcConnectionOptions
        {
            DescribeClose = transport.DescribeCloseAsync,
            Diagnostics = _options.Diagnostics,
            Trace = _options.Trace,
        });

        RegisterHandlers();
    }

    /// <summary>The agent's answer to <c>initialize</c>; null until <see cref="ConnectAsync"/> succeeds.</summary>
    public InitializeResponse? Initialized { get; private set; }

    /// <summary>Completes when the connection to the agent ends.</summary>
    public Task Completion => _connection.Completion;

    public IAcpTransport Transport => _transport;

    /// <summary>
    /// Performs the <c>initialize</c> handshake. Throws <see cref="AcpProtocolVersionException"/> when the
    /// agent does not speak version 1, <see cref="AcpTimeoutException"/> when it does not answer in time, and
    /// <see cref="AcpConnectionClosedException"/> (with exit code and stderr tail) when it dies first.
    /// </summary>
    public async Task<InitializeResponse> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var request = new InitializeRequest
        {
            ProtocolVersion = AcpProtocol.Version,
            ClientCapabilities = _handler.Capabilities,
            ClientInfo = new Implementation { Name = _options.ClientName, Version = _options.ClientVersion },
        };

        var response = await WithTimeoutAsync("initialize", cancellationToken, token =>
            _connection.SendRequestAsync(AcpMethods.Initialize, request,
                AcpJsonContext.Default.InitializeRequest, AcpJsonContext.Default.InitializeResponse, token))
            .ConfigureAwait(false);

        if (response.ProtocolVersion != AcpProtocol.Version)
            throw new AcpProtocolVersionException(response.ProtocolVersion);

        Initialized = response;
        return response;
    }

    /// <summary>
    /// Starts a session in <paramref name="workingDirectory"/>. This is also where a missing login surfaces:
    /// <see cref="AcpAuthRequiredException"/>.
    /// </summary>
    public async Task<AcpSession> NewSessionAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var initialized = Initialized ?? throw new InvalidOperationException("Call ConnectAsync first.");

        NewSessionResponse response;
        try
        {
            response = await WithTimeoutAsync("session/new", cancellationToken, token =>
                _connection.SendRequestAsync(AcpMethods.SessionNew,
                    new NewSessionRequest { Cwd = workingDirectory, McpServers = [] },
                    AcpJsonContext.Default.NewSessionRequest, AcpJsonContext.Default.NewSessionResponse, token))
                .ConfigureAwait(false);
        }
        catch (JsonRpcException ex) when (ex.Code == AcpErrorCodes.AuthRequired)
        {
            throw new AcpAuthRequiredException(ex.Message, initialized.AuthMethods, ex);
        }

        if (string.IsNullOrEmpty(response.SessionId))
            throw new JsonRpcException(AcpErrorCodes.InternalError, "The agent returned a session without an id.");

        var session = new AcpSession(_connection, response.SessionId, response.Modes,
            initialized.AgentCapabilities, initialized.AuthMethods, _options.CancelGrace);
        _sessions[session.SessionId] = session;
        return session;
    }

    private async Task<T> WithTimeoutAsync<T>(string operation, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
    {
        using var timeout = new CancellationTokenSource(_options.StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AcpTimeoutException(operation, _options.StartupTimeout);
        }
    }

    // ───────────── agent -> client ─────────────

    private void RegisterHandlers()
    {
        _connection.RegisterNotificationHandler(
            AcpMethods.SessionUpdate,
            AcpJsonContext.Default.SessionNotification,
            notification =>
            {
                if (_sessions.TryGetValue(notification.SessionId, out var session))
                    session.Receive(notification.Update);
                else
                    _options.Diagnostics?.Invoke($"Dropped an update for unknown session '{notification.SessionId}'.");

                return ValueTask.CompletedTask;
            });

        _connection.RegisterRequestHandler(
            AcpMethods.SessionRequestPermission,
            AcpJsonContext.Default.RequestPermissionRequest,
            AcpJsonContext.Default.RequestPermissionResponse,
            HandlePermissionAsync);

        var capabilities = _handler.Capabilities;
        if (capabilities.Fs.ReadTextFile)
        {
            _connection.RegisterRequestHandler(
                AcpMethods.FsReadTextFile,
                AcpJsonContext.Default.ReadTextFileRequest,
                AcpJsonContext.Default.ReadTextFileResponse,
                (request, token) => _handler.ReadTextFileAsync(request, token));
        }

        if (capabilities.Fs.WriteTextFile)
        {
            _connection.RegisterRequestHandler(
                AcpMethods.FsWriteTextFile,
                AcpJsonContext.Default.WriteTextFileRequest,
                AcpJsonContext.Default.WriteTextFileResponse,
                (request, token) => _handler.WriteTextFileAsync(request, token));
        }

        // terminal/* is deliberately not registered: the capability is not advertised, so the connection
        // answers "method not found".
    }

    private async ValueTask<RequestPermissionResponse> HandlePermissionAsync(RequestPermissionRequest request, CancellationToken connectionToken)
    {
        var cancelled = new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() };

        if (!_sessions.TryGetValue(request.SessionId, out var session))
            return cancelled;

        // A cancel is already under way: do not bother the user with a prompt for a turn being abandoned.
        if (session.BeginPermission() is not { } turnToken)
            return cancelled;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, turnToken);
        try
        {
            return await _handler.RequestPermissionAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested && !connectionToken.IsCancellationRequested)
        {
            // The turn was cancelled while the prompt was open: the protocol answer is "cancelled".
            return cancelled;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _connection.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
