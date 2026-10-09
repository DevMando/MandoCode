namespace MandoCode.Services;

internal sealed class CallbackRegistrations : IDisposable
{
    private readonly List<Action> _release = [];
    private bool _disposed;

    public void Bind<T>(Func<T?> read, Action<T?> write, T callback) where T : Delegate
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CallbackRegistrations));
        write(callback);
        _release.Add(() => { if (Equals(read(), callback)) write(null); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var release in _release) release();
        _release.Clear();
    }
}
