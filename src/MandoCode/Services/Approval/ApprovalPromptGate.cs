namespace MandoCode.Services;

/// <summary>
/// Serializes every interactive approval prompt (diff, delete, command, MCP, plan) behind
/// a single async mutex.
///
/// Why: the kernel runs with <c>AllowConcurrentInvocation = true</c>, so a model response
/// containing two approval-gated tool calls used to open two blocking Spectre prompts at
/// once. Both loop on Console.ReadKey, stealing each other's keys; the user answers the
/// visible one, and the invisible one blocks its function forever — the turn never ends
/// and the input prompt never returns. Acquiring this gate before rendering any approval
/// UI makes concurrent approvals queue and prompt one at a time.
///
/// Acquire with <see cref="AcquireAsync"/> and dispose the returned handle to release.
/// </summary>
public sealed class ApprovalPromptGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Acquires exclusive ownership of the console prompt surface.
    /// <paramref name="cancellationToken"/> aborts the wait (not an acquired hold), so a
    /// cancelled turn doesn't leave a queued approval stuck behind a wedged prompt forever.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
