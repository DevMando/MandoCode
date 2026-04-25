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

    // Live-updatable activity line shown above the spinner. Mutable so callers
    // (e.g. ExecuteCommand streaming subprocess output) can refresh it mid-spin.
    private volatile string? _liveActivity;

    // Tracks whether UpdateActivity fired during this spin. If it did, we preserve
    // the final activity line in scrollback on Stop instead of clearing it — the
    // streamed updates are signal worth keeping; the static initial activity isn't.
    private volatile bool _activityWasUpdated;

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
        _liveActivity = activity;
        _activityWasUpdated = false;
        var startTime = DateTime.UtcNow;
        var lastMessageRotation = startTime;

        // Reserve the activity line above the spinner. Live updates rewrite this
        // same line via cursor-up so the streamed output doesn't scroll the screen.
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
                var lastRenderedActivity = activity;
                var activityLineReserved = hasActivity;
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

                        // Pick up any live activity update. If the activity line
                        // wasn't reserved at Start, we can't add one mid-flight
                        // without scrolling — silently ignore in that case.
                        var currentActivity = _liveActivity;
                        if (activityLineReserved && currentActivity != lastRenderedActivity)
                        {
                            // Move up to the activity line, clear it, rewrite, drop back down.
                            // [A = up one, \r = col 0, [2K = clear line, [B = down one.
                            var redraw = currentActivity ?? string.Empty;
                            Console.Write($"\r[2K[A\r[2K  [2m{redraw}[0m[B\r");
                            lastRenderedActivity = currentActivity;
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
                    // Clear the spinner line - animation is ephemeral, never preserved.
                    Console.Write($"\r[2K");
                    if (activityLineReserved)
                    {
                        // Move up to the activity line and clear it.
                        Console.Write($"[A[2K");
                        // If UpdateActivity ever fired, the streamed updates are
                        // worth keeping in scrollback (e.g. "$ dotnet run -> ...").
                        // Re-print as a normal line - \n drops cursor back onto
                        // the (already-cleared) spinner line, ready for next output.
                        if (_activityWasUpdated && !string.IsNullOrEmpty(lastRenderedActivity))
                        {
                            Console.WriteLine($"  [2m{lastRenderedActivity}[0m");
                        }
                    }
                    Console.Write("\r");
                }
            });
        }
    }

    /// <summary>
    /// Updates the activity text shown above the spinner. Safe to call from any thread.
    /// No-op if the spinner isn't running or wasn't started with an activity (the line
    /// has to be reserved at Start time — we won't insert a new line mid-spin because
    /// that would scroll the terminal).
    /// </summary>
    public void UpdateActivity(string? activity)
    {
        _liveActivity = activity;
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
