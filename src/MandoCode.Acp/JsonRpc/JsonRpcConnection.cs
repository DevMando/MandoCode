using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace MandoCode.Acp.JsonRpc;

public sealed class JsonRpcConnectionOptions
{
    /// <summary>Longest accepted line. A longer one means the peer is not speaking NDJSON; the connection is broken.</summary>
    public int MaxLineBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Number of consecutive unparseable lines after which the peer is declared to be writing garbage to stdout.</summary>
    public int MaxConsecutiveParseErrors { get; init; } = 5;

    /// <summary>
    /// Builds the exception pending requests fail with when the stream ends. The process transport
    /// uses it to attach the exit code and stderr tail (the exit code can lag EOF slightly, hence
    /// async); the default is a plain message.
    /// </summary>
    public Func<Task<AcpConnectionClosedException>>? DescribeClose { get; init; }

    /// <summary>Diagnostics sink (ignored frames, handler failures). Never write this to the console.</summary>
    public Action<string>? Diagnostics { get; init; }
}

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON on a pair of streams (the agent's stdout and stdin).
/// <list type="bullet">
/// <item>Outgoing requests get numeric ids and are correlated with their responses.</item>
/// <item>The reader never waits on a handler: every incoming request runs on its own task and
/// answers itself, so a permission prompt can be pending while a prompt request is in flight.</item>
/// <item>Incoming notifications are handled by a single consumer, preserving their order.</item>
/// <item>Writes are serialized through one writer; each message is exactly one line.</item>
/// </list>
/// The connection does not own the streams: the transport that created them closes them.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private delegate ValueTask<Action<Utf8JsonWriter>> RequestHandler(JsonElement? parameters, CancellationToken cancellationToken);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly JsonRpcConnectionOptions _options;

    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<NotificationWork> _notifications = Channel.CreateUnbounded<NotificationWork>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, RequestHandler> _requestHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement?, ValueTask>> _notificationHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _runningHandlers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _readLoop;
    private readonly Task _writeLoop;
    private readonly Task _notificationLoop;

    private long _nextRequestId;
    private long _nextHandlerId;
    private int _terminated;
    private Exception? _closeReason;
    private int _consecutiveParseErrors;

    private readonly record struct NotificationWork(Func<JsonElement?, ValueTask>? Handler, JsonElement? Parameters, TaskCompletionSource? Marker);

    public JsonRpcConnection(Stream input, Stream output, JsonRpcConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _options = options ?? new JsonRpcConnectionOptions();
        _input = input;
        _output = output;

        _readLoop = Task.Run(ReadLoopAsync);
        _writeLoop = Task.Run(WriteLoopAsync);
        _notificationLoop = Task.Run(NotificationLoopAsync);
    }

    /// <summary>
    /// Completes when the connection ends: normally for EOF or dispose, faulted when the peer broke
    /// the protocol or the transport failed.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Why the connection ended, or null while it is open.</summary>
    public Exception? CloseReason => Volatile.Read(ref _closeReason);

    // ───────────────────────── handlers ─────────────────────────

    /// <summary>Registers the handler for an incoming request (a call the peer expects an answer to).</summary>
    public void RegisterRequestHandler<TRequest, TResponse>(
        string method,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        _requestHandlers[method] = async (parameters, cancellationToken) =>
        {
            if (parameters is null)
                throw new JsonRpcException(Protocol.AcpErrorCodes.InvalidParams, $"Missing params for '{method}'.");

            TRequest? request;
            try
            {
                request = parameters.Value.Deserialize(requestInfo);
            }
            catch (JsonException ex)
            {
                throw new JsonRpcException(Protocol.AcpErrorCodes.InvalidParams, $"Invalid params for '{method}': {ex.Message}");
            }

            if (request is null)
                throw new JsonRpcException(Protocol.AcpErrorCodes.InvalidParams, $"Invalid params for '{method}'.");

            var response = await handler(request, cancellationToken).ConfigureAwait(false);
            return writer => System.Text.Json.JsonSerializer.Serialize(writer, response, responseInfo);
        };
    }

    /// <summary>Registers the handler for an incoming notification. Handlers run one at a time, in arrival order.</summary>
    public void RegisterNotificationHandler<TNotification>(
        string method,
        JsonTypeInfo<TNotification> notificationInfo,
        Func<TNotification, ValueTask> handler)
    {
        _notificationHandlers[method] = async parameters =>
        {
            TNotification? notification;
            try
            {
                notification = parameters is null ? default : parameters.Value.Deserialize(notificationInfo);
            }
            catch (JsonException ex)
            {
                Diagnose($"Ignoring notification '{method}' with invalid params: {ex.Message}");
                return;
            }

            if (notification is null)
            {
                Diagnose($"Ignoring notification '{method}' without usable params.");
                return;
            }

            await handler(notification).ConfigureAwait(false);
        };
    }

    // ───────────────────────── sending ─────────────────────────

    public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();

        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
                removed.TrySetCanceled(cancellationToken);
        });

        try
        {
            Enqueue(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                System.Text.Json.JsonSerializer.Serialize(writer, request, requestInfo);
                writer.WriteEndObject();
            });

            // The race between a close and our registration is closed by FailPending, which
            // also runs after _terminated is set, so this task cannot be left dangling.
            if (Volatile.Read(ref _terminated) != 0)
                FailPending();

            var result = await tcs.Task.ConfigureAwait(false);

            try
            {
                var response = result.Deserialize(responseInfo);
                return response ?? throw new JsonRpcException(Protocol.AcpErrorCodes.InternalError, $"'{method}' returned an empty result.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new JsonRpcException(Protocol.AcpErrorCodes.InternalError, $"'{method}' returned a malformed result: {ex.Message}");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync<TNotification>(
        string method,
        TNotification notification,
        JsonTypeInfo<TNotification> notificationInfo)
    {
        ThrowIfClosed();

        Enqueue(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            System.Text.Json.JsonSerializer.Serialize(writer, notification, notificationInfo);
            writer.WriteEndObject();
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes once every notification received before this call has been handled. The reader
    /// dispatches a response as soon as it arrives, so a caller that must see all <c>session/update</c>
    /// notifications that preceded a response awaits this first.
    /// </summary>
    public Task DrainNotificationsAsync()
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_notifications.Writer.TryWrite(new NotificationWork(null, null, marker)))
            marker.TrySetResult();
        return marker.Task;
    }

    // ───────────────────────── write path ─────────────────────────

    private void Enqueue(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);

        var line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';

        if (!_outbound.Writer.TryWrite(line))
            throw ClosedException();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _output.WriteAsync(line).ConfigureAwait(false);
                await _output.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            Terminate(await CreateClosedExceptionAsync(ex).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ───────────────────────── read path ─────────────────────────

    private async Task ReadLoopAsync()
    {
        try
        {
            var token = _lifetime.Token;
            var buffer = new byte[16 * 1024];
            int start = 0, end = 0, scanFrom = 0;

            while (true)
            {
                var newline = buffer.AsSpan(scanFrom, end - scanFrom).IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var lineEnd = scanFrom + newline;
                    HandleLine(buffer.AsMemory(start, lineEnd - start));
                    start = scanFrom = lineEnd + 1;
                    continue;
                }

                scanFrom = end;

                if (end - start > _options.MaxLineBytes)
                {
                    throw new AcpProtocolViolationException(
                        $"The agent sent a line longer than {_options.MaxLineBytes} bytes without a newline; it is not speaking newline-delimited JSON.");
                }

                if (start == end)
                {
                    start = end = scanFrom = 0;
                }
                else if (end == buffer.Length)
                {
                    if (start > 0)
                    {
                        // Slide the unfinished line to the front to make room.
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        scanFrom -= start;
                        start = 0;
                    }
                    else
                    {
                        // The line is longer than the buffer but still within the limit checked above.
                        Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, (long)_options.MaxLineBytes + 1));
                    }
                }

                var read = await _input.ReadAsync(buffer.AsMemory(end), token).ConfigureAwait(false);
                if (read == 0)
                {
                    // EOF: a final line without a trailing newline still counts.
                    if (end > start)
                        HandleLine(buffer.AsMemory(start, end - start));
                    break;
                }

                end += read;
            }

            Terminate(await CreateClosedExceptionAsync(null).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Terminate(await CreateClosedExceptionAsync(null).ConfigureAwait(false));
        }
        catch (AcpProtocolViolationException ex)
        {
            Terminate(ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Terminate(await CreateClosedExceptionAsync(ex).ConfigureAwait(false));
        }
    }

    private void HandleLine(ReadOnlyMemory<byte> line)
    {
        // Tolerate CRLF-terminated peers.
        if (line.Length > 0 && line.Span[^1] == 0x0D)
            line = line[..^1];

        if (IsBlank(line.Span))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            OnUnparseable($"Skipped a line that is not valid JSON ({line.Length} bytes).");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                OnUnparseable("Skipped JSON that is not a JSON-RPC message object.");
                return;
            }

            _consecutiveParseErrors = 0;
            Dispatch(root);
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b is not (0x20 or 0x09 or 0x0D or 0x0A))
                return false;
        }

        return true;
    }

    private void OnUnparseable(string message)
    {
        Diagnose(message);
        if (++_consecutiveParseErrors >= _options.MaxConsecutiveParseErrors)
        {
            throw new AcpProtocolViolationException(
                "The agent wrote non-protocol output to stdout (it must write only newline-delimited JSON-RPC there; logs belong on stderr).");
        }
    }

    private void Dispatch(JsonElement root)
    {
        var hasId = root.TryGetProperty("id", out var idElement) && JsonRpcId.TryRead(idElement, out _);
        var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;

        if (hasMethod)
        {
            var method = methodElement.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : null;

            if (hasId)
            {
                JsonRpcId.TryRead(idElement, out var id);
                StartRequestHandler(id, method, parameters);
            }
            else
            {
                QueueNotification(method, parameters);
            }

            return;
        }

        if (hasId && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
        {
            CompleteRequest(idElement, root);
            return;
        }

        Diagnose("Ignored a message that is neither a request, a notification nor a response.");
    }

    private void CompleteRequest(JsonElement idElement, JsonElement root)
    {
        JsonRpcId.TryRead(idElement, out var id);
        if (!id.TryGetInt64(out var numericId) || !_pending.TryRemove(numericId, out var tcs))
        {
            Diagnose($"Ignored a response with unknown id {id}.");
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsedCode)
                ? parsedCode
                : Protocol.AcpErrorCodes.InternalError;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "The agent returned an error without a message.";
            JsonElement? data = error.TryGetProperty("data", out var d) ? d.Clone() : null;

            tcs.TrySetException(new JsonRpcException(code, message, data));
            return;
        }

        tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
    }

    // ───────────────────────── incoming requests / notifications ─────────────────────────

    private void StartRequestHandler(JsonRpcId id, string method, JsonElement? parameters)
    {
        if (!_requestHandlers.TryGetValue(method, out var handler))
        {
            Enqueue(writer => WriteErrorResponse(writer, id, Protocol.AcpErrorCodes.MethodNotFound, $"Method not found: {method}", null));
            return;
        }

        var handlerId = Interlocked.Increment(ref _nextHandlerId);
        var task = Task.Run(async () =>
        {
            try
            {
                await RunRequestAsync(id, method, handler, parameters).ConfigureAwait(false);
            }
            finally
            {
                _runningHandlers.TryRemove(handlerId, out _);
            }
        });
        _runningHandlers[handlerId] = task;
    }

    private async Task RunRequestAsync(JsonRpcId id, string method, RequestHandler handler, JsonElement? parameters)
    {
        Action<Utf8JsonWriter> write;
        try
        {
            var writeResult = await handler(parameters, _lifetime.Token).ConfigureAwait(false);
            write = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                id.WriteTo(writer);
                writer.WritePropertyName("result");
                writeResult(writer);
                writer.WriteEndObject();
            };
        }
        catch (JsonRpcException ex)
        {
            write = writer => WriteErrorResponse(writer, id, ex.Code, ex.Message, ex.ErrorData);
        }
        catch (OperationCanceledException)
        {
            write = writer => WriteErrorResponse(writer, id, Protocol.AcpErrorCodes.RequestCancelled, "Request cancelled.", null);
        }
        catch (Exception ex)
        {
            Diagnose($"Handler for '{method}' failed: {ex.GetType().Name}: {ex.Message}");
            write = writer => WriteErrorResponse(writer, id, Protocol.AcpErrorCodes.InternalError, $"Internal error handling '{method}'.", null);
        }

        try
        {
            Enqueue(write);
        }
        catch (AcpConnectionClosedException)
        {
            // The peer is gone; there is nobody to answer.
        }
    }

    private static void WriteErrorResponse(Utf8JsonWriter writer, JsonRpcId id, int code, string message, JsonElement? data)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        if (data is { } value)
        {
            writer.WritePropertyName("data");
            value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void QueueNotification(string method, JsonElement? parameters)
    {
        if (!_notificationHandlers.TryGetValue(method, out var handler))
        {
            Diagnose($"Ignored notification '{method}' (no handler).");
            return;
        }

        _notifications.Writer.TryWrite(new NotificationWork(handler, parameters, null));
    }

    private async Task NotificationLoopAsync()
    {
        await foreach (var work in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Marker is not null)
            {
                work.Marker.TrySetResult();
                continue;
            }

            try
            {
                await work.Handler!(work.Parameters).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One misbehaving notification must not stop the ones behind it.
                Diagnose($"A notification handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ───────────────────────── shutdown ─────────────────────────

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _terminated) != 0)
            throw ClosedException();
    }

    private AcpConnectionClosedException ClosedException() =>
        Volatile.Read(ref _closeReason) as AcpConnectionClosedException
        ?? new AcpConnectionClosedException(
            Volatile.Read(ref _closeReason)?.Message ?? "The connection to the agent is closed.",
            inner: Volatile.Read(ref _closeReason));

    private async Task<AcpConnectionClosedException> CreateClosedExceptionAsync(Exception? inner)
    {
        if (_options.DescribeClose is { } describe)
        {
            try
            {
                return await describe().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Fall through to the generic description.
            }
        }

        return new AcpConnectionClosedException(
            inner is null ? "The agent closed the connection." : $"The connection to the agent failed: {inner.Message}",
            inner: inner);
    }

    private void Terminate(Exception reason)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
            return;

        Volatile.Write(ref _closeReason, reason);
        _outbound.Writer.TryComplete();
        _notifications.Writer.TryComplete();
        FailPending();

        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }

        if (reason is AcpProtocolViolationException)
            _completion.TrySetException(reason);
        else
            _completion.TrySetResult();
    }

    private void FailPending()
    {
        var reason = ClosedException();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(reason);
        }
    }

    private void Diagnose(string message) => _options.Diagnostics?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        Terminate(new AcpConnectionClosedException("The connection was disposed."));

        var work = Task.WhenAll(_readLoop, _writeLoop, _notificationLoop, Task.WhenAll(_runningHandlers.Values));
        await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        _lifetime.Dispose();
    }
}
