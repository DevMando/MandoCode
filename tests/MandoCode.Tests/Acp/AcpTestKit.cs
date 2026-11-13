using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MandoCode.Acp.JsonRpc;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>One-directional in-memory byte pipe: what one side writes, the other reads. Completing it is EOF.</summary>
internal sealed class BytePipe : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void CompleteWriting() => _chunks.Writer.TryComplete();

    public override void Write(byte[] buffer, int offset, int count) =>
        _chunks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_chunks.Writer.TryWrite(buffer.ToArray()))
            throw new IOException("The pipe is closed.");
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null || _position >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;

            if (_chunks.Reader.TryRead(out var next))
            {
                _current = next;
                _position = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The agent's side of an in-memory connection. The client under test reads <see cref="ToClient"/>
/// and writes <see cref="FromClient"/>; tests drive the agent by hand.
/// </summary>
internal sealed class FakeAgentPeer : IDisposable
{
    public BytePipe ToClient { get; } = new();
    public BytePipe FromClient { get; } = new();

    private readonly StreamReader _reader;

    public FakeAgentPeer() => _reader = new StreamReader(FromClient, new UTF8Encoding(false));

    public JsonRpcConnection CreateConnection(JsonRpcConnectionOptions? options = null) =>
        new(ToClient, FromClient, options);

    /// <summary>Reads the next line the client wrote, parsed as JSON.</summary>
    public async Task<JsonElement> ReadMessageAsync()
    {
        var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    public async Task<string?> ReadRawLineAsync() =>
        await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

    public void SendLine(string json) => SendRaw(json + "\n");

    public void SendRaw(string text) => ToClient.Write(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text));

    public void SendRawInChunks(string text, int chunkSize)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += chunkSize)
            ToClient.Write(bytes, i, Math.Min(chunkSize, bytes.Length - i));
    }

    public void SendResult(JsonElement requestId, string resultJson) =>
        SendLine("{\"jsonrpc\":\"2.0\",\"id\":" + requestId.GetRawText() + ",\"result\":" + resultJson + "}");

    public void SendError(JsonElement requestId, int code, string message, string? dataJson = null) =>
        SendLine("{\"jsonrpc\":\"2.0\",\"id\":" + requestId.GetRawText()
                 + ",\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\""
                 + (dataJson is null ? "" : ",\"data\":" + dataJson) + "}}");

    /// <summary>A <c>session/update</c> notification carrying an agent text chunk.</summary>
    public static string ChunkNotification(string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s\",\"update\":"
        + "{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"" + text + "\"}}}}";

    /// <summary>Ends the agent's stdout: the client sees EOF.</summary>
    public void CloseOutput() => ToClient.CompleteWriting();

    public void Dispose()
    {
        ToClient.CompleteWriting();
        FromClient.CompleteWriting();
        _reader.Dispose();
    }
}

internal static class TaskExtensions
{
    public static async Task<T> Within<T>(this Task<T> task, int seconds = 5) =>
        await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public static async Task Within(this Task task, int seconds = 5) =>
        await task.WaitAsync(TimeSpan.FromSeconds(seconds));
}
