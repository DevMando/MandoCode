using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Owns spinner animation lifecycle and taskbar progress.
/// Thread-safe start/stop for the animated braille spinner.
/// </summary>
public class SpinnerService
{
    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;
    private readonly object _spinnerLock = new();

    public void Start()
    {
        Stop();
        SetTaskbarIndeterminate();
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var message = LoadingMessages.GetRandom();
        var frames = new[] { "\u28fb", "\u28fd", "\u28fe", "\u28f7", "\u28ef", "\u28df", "\u28bf", "\u287f" };

        var task = Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var frame = frames[i++ % frames.Length];
                    Console.Write($"\r  {frame} {message}  ");
                    await Task.Delay(80, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Clear the spinner line
                Console.Write($"\r{new string(' ', message.Length + 10)}\r");
            }
        });

        lock (_spinnerLock)
        {
            _spinnerCts = cts;
            _spinnerTask = task;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_spinnerLock)
        {
            cts = _spinnerCts;
            task = _spinnerTask;
            _spinnerCts = null;
            _spinnerTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            // Use non-blocking wait with timeout to avoid thread pool starvation
            if (task != null)
            {
                try { task.Wait(1000); } catch (AggregateException) { }
            }
            cts.Dispose();
        }

        ClearTaskbarProgress();
    }

    // Taskbar progress (Windows Terminal OSC 9;4)
    public static void SetTaskbarProgress(int percent)
    {
        Console.Write($"\u001b]9;4;1;{percent}\u0007");
    }

    public static void SetTaskbarIndeterminate()
    {
        Console.Write("\u001b]9;4;3\u0007");
    }

    public static void SetTaskbarError(int percent = 100)
    {
        Console.Write($"\u001b]9;4;2;{percent}\u0007");
    }

    public static void ClearTaskbarProgress()
    {
        Console.Write("\u001b]9;4;0\u0007");
    }
}
