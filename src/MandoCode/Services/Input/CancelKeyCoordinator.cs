namespace MandoCode.Services;

/// <summary>
/// Coordinates the App-level Escape-key listener with prompts that own the
/// console themselves (Spectre SelectionPrompt / TextPrompt). Both call
/// Console.ReadKey, so when both are active the listener wins about half
/// the time and silently drops non-Escape keys — arrow presses feel laggy
/// and the user has to mash keys to navigate.
///
/// Wrap any Spectre prompt in `using (coordinator.Suppress())` to pause the
/// listener for the duration. Reference-counted so nested prompts compose.
/// </summary>
public sealed class CancelKeyCoordinator
{
    private int _suppressionCount;

    public bool Suppressed => Volatile.Read(ref _suppressionCount) > 0;

    public IDisposable Suppress()
    {
        Interlocked.Increment(ref _suppressionCount);
        return new SuppressionScope(this);
    }

    private sealed class SuppressionScope : IDisposable
    {
        private readonly CancelKeyCoordinator _coordinator;
        private int _disposed;

        public SuppressionScope(CancelKeyCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _coordinator._suppressionCount);
            }
        }
    }
}
