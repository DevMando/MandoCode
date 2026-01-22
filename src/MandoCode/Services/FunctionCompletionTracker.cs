namespace MandoCode.Services;

/// <summary>
/// Tracks function execution completion using semaphore-based signaling.
/// Allows waiting for all pending function invocations to complete before proceeding.
/// </summary>
public class FunctionCompletionTracker : IDisposable
{
    private readonly SemaphoreSlim _completionSignal = new(0);
    private readonly object _countLock = new();
    private int _pendingCount;
    private bool _disposed;

    /// <summary>
    /// Gets the number of currently pending function executions.
    /// </summary>
    public int PendingCount
    {
        get { lock (_countLock) return _pendingCount; }
    }

    /// <summary>
    /// Registers the start of a function execution.
    /// Call this when a function invocation begins.
    /// </summary>
    public void RegisterStart()
    {
        lock (_countLock)
        {
            _pendingCount++;
        }
    }

    /// <summary>
    /// Registers the completion of a function execution.
    /// Call this when a function invocation completes (success or failure).
    /// </summary>
    public void RegisterCompletion()
    {
        bool shouldSignal = false;

        lock (_countLock)
        {
            if (_pendingCount > 0)
            {
                _pendingCount--;
                // Signal when count reaches zero
                if (_pendingCount == 0)
                {
                    shouldSignal = true;
                }
            }
        }

        if (shouldSignal)
        {
            // Release all waiting threads
            try
            {
                _completionSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Ignore - semaphore was already signaled
            }
        }
    }

    /// <summary>
    /// Waits for all pending function executions to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <returns>True if all functions completed within timeout, false otherwise.</returns>
    public async Task<bool> WaitForAllCompletionsAsync(TimeSpan timeout)
    {
        // If nothing pending, return immediately
        lock (_countLock)
        {
            if (_pendingCount == 0)
                return true;
        }

        // Wait for signal or timeout
        try
        {
            return await _completionSignal.WaitAsync(timeout);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for all pending function executions to complete with default 30s timeout.
    /// </summary>
    /// <returns>True if all functions completed within timeout, false otherwise.</returns>
    public Task<bool> WaitForAllCompletionsAsync()
    {
        return WaitForAllCompletionsAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Resets the tracker state for a new operation.
    /// </summary>
    public void Reset()
    {
        lock (_countLock)
        {
            _pendingCount = 0;
        }

        // Drain any pending signals
        while (_completionSignal.CurrentCount > 0)
        {
            _completionSignal.Wait(0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completionSignal.Dispose();
    }
}
