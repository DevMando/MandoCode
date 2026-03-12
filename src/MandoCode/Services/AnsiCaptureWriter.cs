using System.Text;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Captures Console.Write/WriteLine AND AnsiConsole.Write output from existing renderers
/// into a string. Used to bridge imperative ANSI rendering into the VDOM via
/// AnsiPassthroughTranslator.
///
/// Spectre.Console's AnsiConsole caches its own TextWriter reference at startup,
/// so Console.SetOut() alone is insufficient — we must also redirect AnsiConsole.Console.
///
/// IMPORTANT: This captures ALL console output while active. Only use during
/// synchronous render calls — never during interactive input or concurrent operations.
/// </summary>
public static class AnsiCaptureWriter
{
    /// <summary>
    /// Executes an action while capturing all Console and AnsiConsole output,
    /// returns the captured string.
    /// </summary>
    public static string Capture(Action renderAction)
    {
        var buffer = new StringWriter(new StringBuilder(4096));
        var originalOut = Console.Out;
        var originalAnsiConsole = AnsiConsole.Console;

        try
        {
            Console.SetOut(buffer);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(buffer),
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor
            });
            renderAction();
        }
        finally
        {
            Console.SetOut(originalOut);
            AnsiConsole.Console = originalAnsiConsole;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Async version for renderers that use AnsiConsole or await internally.
    /// </summary>
    public static async Task<string> CaptureAsync(Func<Task> renderAction)
    {
        var buffer = new StringWriter(new StringBuilder(4096));
        var originalOut = Console.Out;
        var originalAnsiConsole = AnsiConsole.Console;

        try
        {
            Console.SetOut(buffer);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(buffer),
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.TrueColor
            });
            await renderAction();
        }
        finally
        {
            Console.SetOut(originalOut);
            AnsiConsole.Console = originalAnsiConsole;
        }

        return buffer.ToString();
    }
}
