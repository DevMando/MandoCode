namespace MandoCode.Acp.Transport;

/// <summary>
/// The byte channel to an agent. <see cref="Input"/> is what the agent writes (its stdout) and
/// <see cref="Output"/> is what it reads (its stdin). Disposing the transport shuts the agent down.
/// </summary>
public interface IAcpTransport : IAsyncDisposable
{
    /// <summary>The agent's stdout: protocol messages only.</summary>
    Stream Input { get; }

    /// <summary>The agent's stdin.</summary>
    Stream Output { get; }

    /// <summary>Completes with the exit code when the agent process ends (null when there is no process).</summary>
    Task<int?> Exited { get; }

    /// <summary>The last lines the agent wrote to stderr.</summary>
    string StderrTail { get; }

    /// <summary>
    /// Describes why the connection ended: exit code (if the process is gone) and stderr tail.
    /// Waits briefly for the exit code, which can lag the stdout EOF.
    /// </summary>
    Task<AcpConnectionClosedException> DescribeCloseAsync();
}

/// <summary>A transport over two existing streams, for tests and in-process agents. Owns neither.</summary>
public sealed class StreamTransport : IAcpTransport
{
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StreamTransport(Stream input, Stream output)
    {
        Input = input;
        Output = output;
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public Task<int?> Exited => _exited.Task;
    public string StderrTail => "";

    /// <summary>Simulates the end of the peer.</summary>
    public void SignalExit(int? exitCode = null) => _exited.TrySetResult(exitCode);

    public Task<AcpConnectionClosedException> DescribeCloseAsync() =>
        Task.FromResult(new AcpConnectionClosedException("The agent closed the connection."));

    public ValueTask DisposeAsync()
    {
        _exited.TrySetResult(null);
        return ValueTask.CompletedTask;
    }
}
