using System.Threading.Channels;
using MandoCode.Acp.Protocol;

namespace MandoCode.Acp.Client;

/// <summary>
/// One prompt/response cycle. <see cref="Updates"/> streams the agent's <c>session/update</c>
/// notifications while it works; <see cref="Completion"/> resolves with the stop reason once the
/// agent answers <c>session/prompt</c> and every earlier update has been delivered.
/// </summary>
public sealed class AcpTurn
{
    private readonly Channel<SessionUpdate> _updates = Channel.CreateUnbounded<SessionUpdate>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal AcpTurn()
    {
    }

    /// <summary>
    /// The updates of this turn, in order. Enumerate it once. The sequence ends when the turn ends,
    /// whether it succeeded or failed: await <see cref="Completion"/> for the outcome.
    /// </summary>
    public IAsyncEnumerable<SessionUpdate> Updates => _updates.Reader.ReadAllAsync();

    /// <summary>
    /// The stop reason (one of <see cref="StopReasons"/>, or a value from a newer agent). Faults with
    /// <see cref="AcpConnectionClosedException"/> if the agent dies mid-turn, or with
    /// <see cref="AcpAuthRequiredException"/> / <see cref="JsonRpcException"/> when the agent refuses.
    /// </summary>
    public Task<string> Completion => _completion.Task;

    /// <summary>True once <see cref="AcpSession.CancelAsync"/> was called for this turn.</summary>
    public bool CancelRequested { get; internal set; }

    internal void Push(SessionUpdate update) => _updates.Writer.TryWrite(update);

    internal void Succeed(string stopReason)
    {
        _updates.Writer.TryComplete();
        _completion.TrySetResult(stopReason);
    }

    internal void Fail(Exception exception)
    {
        _updates.Writer.TryComplete();
        _completion.TrySetException(exception);
    }
}
