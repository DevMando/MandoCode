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

    // How often to rotate the random "fun" message so long waits don't feel frozen.
    private static readonly TimeSpan MessageRotationInterval = TimeSpan.FromSeconds(15);

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
        var startTime = DateTime.UtcNow;
        var lastMessageRotation = startTime;

        // Write activity line immediately on the calling thread so it's visible
        // before the background spinner task gets scheduled
        if (hasActivity)
        {
            Console.Write($"[2m  {activity}[0m\n");
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
                        var now = DateTime.UtcNow;

                        // Rotate the random message periodically so long waits feel alive.
                        if (now - lastMessageRotation >= MessageRotationInterval)
                        {
                            message = LoadingMessages.GetRandom();
                            lastMessageRotation = now;
                        }

                        var frame = frames[i++ % frames.Length];
                        var elapsed = FormatElapsed(now - startTime);

                        // [2K clears the whole line so variable-length elapsed text
                        // (9s -> 10s, 59s -> 1m 0s) doesn't leave stale chars behind.
                        Console.Write(
                            $"\r[2K  " +
                            $"[38;2;200;100;255m{frame}[0m " +
                            $"[38;2;180;140;255m{message}[0m " +
                            $"[2m· {elapsed}[0m");

                        await Task.Delay(interval, token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    // Clear the spinner line
                    Console.Write($"\r[2K");
                    // If we had an activity line, move cursor up and clear it too
                    if (hasActivity)
                    {
                        Console.Write($"[A[2K");
                    }
                    Console.Write("\r");
                }
            });
        }
    }

    /// <summary>
    /// Compact elapsed-time formatter: "3s", "45s", "1m 20s", "12m 5s".
    /// </summary>
    private static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
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
        Console.Write($"]9;4;1;{percent}");
    }

    public static void SetTaskbarIndeterminate()
    {
        Console.Write("]9;4;3");
    }

    public static void SetTaskbarError(int percent = 100)
    {
        Console.Write($"]9;4;2;{percent}");
    }

    public static void SetTaskbarWarning(int percent = 100)
    {
        Console.Write($"]9;4;4;{percent}");
    }

    public static void ClearTaskbarProgress()
    {
        Console.Write("]9;4;0");
    }
}
