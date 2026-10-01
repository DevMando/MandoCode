using Spectre.Console;
using Spectre.Console.Rendering;
using PhysicalConsole = Spectre.Console.AnsiConsole;

namespace MandoCode.Services;

/// <summary>Routes legacy display calls into component state; RazorConsole alone writes the terminal.</summary>
public static class TuiConsole
{
    private static TextWriter? _terminalWriter;
    public static TuiSession? Current { get; private set; }
    public static IDisposable Begin(TuiSession session)
    {
        if (Current is not null) throw new InvalidOperationException("A TUI session is already active.");
        // Force Spectre to retain the physical writer before redirecting ordinary stdout.
        _ = PhysicalConsole.Console;
        var previous = System.Console.Out;
        var writer = new TuiTranscriptWriter(session);
        _terminalWriter = previous;
        Current = session;
        System.Console.SetOut(writer);
        return new OutputScope(previous, writer);
    }
    // Palette OSC sequences change terminal colors without drawing into the canvas.
    // Send them to the retained terminal writer rather than the transcript sanitizer.
    internal static void WriteTerminalControl(string sequence)
    {
        if (_terminalWriter is { } writer) writer.Write(sequence);
        else System.Console.Write(sequence);
    }
    public static IAnsiConsole Console { get => PhysicalConsole.Console; set => PhysicalConsole.Console = value; }
    public static IAnsiConsole Create(AnsiConsoleSettings settings) => PhysicalConsole.Create(settings);
    public static void Write(IRenderable content)
    {
        if (Current is { } session) session.Append(content);
        else PhysicalConsole.Write(content);
    }
    public static void Write(string text) { if (Current is { } session) session.Append(new Text(text)); else PhysicalConsole.Write(text); }
    public static void WriteLine() { if (Current is null) PhysicalConsole.WriteLine(); }
    public static void WriteLine(string text) { if (Current is { } session) session.Append(new Text(text)); else PhysicalConsole.WriteLine(text); }
    public static void Markup(string text) { if (Current is { } session) session.Append(new Markup(text)); else PhysicalConsole.Markup(text); }
    public static void MarkupLine(string text) { if (Current is { } session) session.Append(new Markup(text)); else PhysicalConsole.MarkupLine(text); }
    public static void Clear() { if (Current is { } session) session.Clear(); else PhysicalConsole.Clear(); }
    // Standalone configuration commands still use Spectre. Component wizards already
    // use App's coordinators; remaining blocking pickers will be migrated separately.
    public static T Prompt<T>(IPrompt<T> prompt) => PhysicalConsole.Prompt(prompt);
    public static T Ask<T>(string prompt) => PhysicalConsole.Ask<T>(prompt);
    public static T Ask<T>(string prompt, T defaultValue) => PhysicalConsole.Ask(prompt, defaultValue);
    public static bool Confirm(string prompt, bool defaultValue = true) => PhysicalConsole.Confirm(prompt, defaultValue);
    public static Status Status() => PhysicalConsole.Status();
    private sealed class OutputScope(TextWriter previous, TuiTranscriptWriter writer) : IDisposable
    {
        public void Dispose()
        {
            System.Console.SetOut(previous);
            writer.Flush();
            Current = null;
            _terminalWriter = null;
        }
    }
}
