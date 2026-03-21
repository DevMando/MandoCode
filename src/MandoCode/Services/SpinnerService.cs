using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Owns spinner animation lifecycle and taskbar progress.
/// Thread-safe start/stop for the animated spinner with optional activity display.
/// </summary>
public class SpinnerService
{
    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;
    private readonly object _spinnerLock = new();

    public void Start(string? activity = null)
    {
        Stop();
        SetTaskbarIndeterminate();
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var message = LoadingMessages.GetRandom();
        var spinner = LoadingMessages.GetRandomSpinner();
        var frames = spinner.Frames.ToArray();
        var interval = (int)spinner.Interval.TotalMilliseconds;
        var hasActivity = !string.IsNullOrEmpty(activity);

        // Write activity line immediately on the calling thread so it's visible
        // before the background spinner task gets scheduled
        if (hasActivity)
        {
            Console.Write($"\u001b[2m  {activity}\u001b[0m\n");
        }

        lock (_spinnerLock)
        {
            _spinnerCts = cts;
            _spinnerTask = Task.Run(async () =>
            {
                var i = 0;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var frame = frames[i++ % frames.Length];
                        Console.Write($"\r  \u001b[38;2;200;100;255m{frame}\u001b[0m \u001b[38;2;180;140;255m{message}\u001b[0m  ");
                        await Task.Delay(interval, token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    // Clear the spinner line
                    Console.Write($"\r\u001b[2K");
                    // If we had an activity line, move cursor up and clear it too
                    if (hasActivity)
                    {
                        Console.Write($"\u001b[A\u001b[2K");
                    }
                    Console.Write("\r");
                }
            });
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
            try
            {
                cts.Cancel();
                // Use non-blocking wait with timeout to avoid thread pool starvation
                if (task != null)
                {
                    try { task.Wait(1000); } catch (AggregateException) { }
                }
            }
            finally
            {
                cts.Dispose();
            }
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

    public static void SetTaskbarWarning(int percent = 100)
    {
        Console.Write($"\u001b]9;4;4;{percent}\u0007");
    }

    public static void ClearTaskbarProgress()
    {
        Console.Write("\u001b]9;4;0\u0007");
    }
}
