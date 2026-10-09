namespace MandoCode.Services;

/// <summary>Compatible entry point for agent activity. Backend selection belongs here,
/// keeping cursor-driven terminal animation outside the component presentation path.</summary>
public class SpinnerService
{
    public const int PreviewLines = 5;
    private readonly TerminalActivityPresentation _terminal = new();

    private IActivityPresentation Presentation => TuiConsole.Current is { } session
        ? new WidgetActivityPresentation(session)
        : _terminal;

    public void Start(string? activity = null, string? message = null) => Presentation.Start(activity, message);
    public void UpdateActivity(string? activity) => Presentation.UpdateActivity(activity);
    public void UpdatePreview(string? text) => Presentation.UpdatePreview(text);
    public void Stop() => Presentation.Stop();

    public static void SetTaskbarProgress(int percent) => TerminalActivityPresentation.SetTaskbarProgress(percent);
    public static void SetTaskbarIndeterminate() => TerminalActivityPresentation.SetTaskbarIndeterminate();
    public static void SetTaskbarError(int percent = 100) => TerminalActivityPresentation.SetTaskbarError(percent);
    public static void SetTaskbarWarning(int percent = 100) => TerminalActivityPresentation.SetTaskbarWarning(percent);
    public static void ClearTaskbarProgress() => TerminalActivityPresentation.ClearTaskbarProgress();
}

internal interface IActivityPresentation
{
    void Start(string? activity, string? message);
    void UpdateActivity(string? activity);
    void UpdatePreview(string? text);
    void Stop();
}

internal sealed class WidgetActivityPresentation(TuiSession session) : IActivityPresentation
{
    public void Start(string? activity, string? message) => session.SetRunning(true, activity, message);
    public void UpdateActivity(string? activity) => session.SetActivity(activity);
    public void UpdatePreview(string? text) => session.SetPreview(text);
    public void Stop() => session.SetRunning(false);
}
