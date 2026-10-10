using Spectre.Console;
using Spectre.Console.Rendering;
using PhysicalConsole = Spectre.Console.AnsiConsole;

namespace MandoCode.Services;

/// <summary>Routes legacy display calls into component state; RazorConsole alone writes the terminal.</summary>
public static class TuiConsole
{
    private static TextWriter? _terminalWriter;
    private static readonly AsyncLocal<TuiSession?> Ambient = new();
    private static readonly AsyncLocal<TextWriter?> ControlOutput = new();
    private static TuiSession? _fallback;
    public static TuiSession? Current => Ambient.Value ?? _fallback;
    internal static void SetActive(TuiSession session) { if (_fallback is not null) _fallback = session; }
    public static IDisposable Enter(TuiSession session)
    {
        var previous = Ambient.Value;
        Ambient.Value = session;
        return new SessionScope(previous, ControlOutput.Value);
    }
    // Scoped terminal sink lets isolated hosts/tests route OSC controls without replacing process stdout.
    internal static IDisposable Enter(TuiSession session, TextWriter terminalOutput)
    {
        var previous = Ambient.Value;
        var previousOutput = ControlOutput.Value;
        Ambient.Value = session;
        ControlOutput.Value = terminalOutput;
        return new SessionScope(previous, previousOutput);
    }
    public static IDisposable Begin(TuiSession session)
    {
        if (Current is not null) throw new InvalidOperationException("A TUI session is already active.");
        // Force Spectre to retain the physical writer before redirecting ordinary stdout.
        _ = PhysicalConsole.Console;
        var previous = System.Console.Out;
        var writer = new RoutedTranscriptWriter(session);
        _terminalWriter = previous;
        _fallback = session;
        System.Console.SetOut(writer);
        return new OutputScope(previous, writer);
    }
    // Palette and clipboard OSC sequences act on the terminal without drawing into the canvas.
    // Send them to the retained terminal writer rather than the transcript sanitizer.
    internal static void WriteTerminalControl(string sequence)
    {
        if ((ControlOutput.Value ?? _terminalWriter) is { } writer) writer.Write(sequence);
        else System.Console.Write(sequence);
    }
    public static IAnsiConsole Console { get => PhysicalConsole.Console; set => PhysicalConsole.Console = value; }
    public static IAnsiConsole Create(AnsiConsoleSettings settings) => PhysicalConsole.Create(settings);
    public static void Write(IRenderable content)
    {
        if (Current is { } session) session.Append(content);
        else PhysicalConsole.Write(content);
    }
    public static void WriteSpaced(IRenderable content)
    {
        if (Current is { } session) session.AppendSpaced(content);
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
    private sealed class SessionScope(TuiSession? previous, TextWriter? previousOutput) : IDisposable
    {
        public void Dispose() { Ambient.Value = previous; ControlOutput.Value = previousOutput; }
    }
    private sealed class RoutedTranscriptWriter(TuiSession fallback) : TextWriter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TuiSession, TuiTranscriptWriter> _writers = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(string? value) => _writers.GetOrAdd(Current ?? fallback, s => new TuiTranscriptWriter(s)).Write(value);
        public override void Write(char value) => Write(value.ToString());
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
        public override void Flush() { foreach (var writer in _writers.Values) writer.Flush(); }
    }
    private sealed class OutputScope(TextWriter previous, RoutedTranscriptWriter writer) : IDisposable
    {
        public void Dispose()
        {
            System.Console.SetOut(previous);
            writer.Flush();
            _fallback = null;
            _terminalWriter = null;
        }
    }
}
