namespace MandoCode.Services;

/// <summary>
/// Thin wrapper around Console.ReadKey that isolates console I/O.
/// Virtual methods allow test doubles (MockKeySource) or alternative
/// input sources (WebSocket, channel-based async) in the future.
/// </summary>
public class ConsoleInputReader
{
    /// <summary>
    /// Reads the next key from console input (blocking).
    /// </summary>
    public virtual ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    /// <summary>
    /// Whether additional keys are immediately available (paste detection).
    /// </summary>
    public virtual bool KeyAvailable => Console.KeyAvailable;
}
