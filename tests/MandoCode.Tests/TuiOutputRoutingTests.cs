using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[CollectionDefinition("TUI console routing", DisableParallelization = true)]
public class TuiConsoleRoutingCollection { }

[Collection("TUI console routing")]
[Trait("Category", "Component")]
public class TuiOutputRoutingTests
{
    [Fact]
    public void PaletteChanges_ReachTerminal_WithoutBecomingConversationText()
    {
        var previous = Console.Out;
        var terminal = new StringWriter();
        Console.SetOut(terminal);
        try
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Begin(session);
            var theme = new TerminalThemeService(new MandoCode.Models.MandoCodeConfig(), new TokenTrackingService(), new ProjectRootAccessor("."));
            theme.ApplyPalette();
            theme.ResetPalette();
            Console.Out.Flush();
            Assert.Contains("\u001b]4;2;rgb:98/c3/79\u0007", terminal.ToString());
            Assert.Contains("\u001b]104\u0007", terminal.ToString());
            Assert.Empty(session.Snapshot().Entries);
        }
        finally { Console.SetOut(previous); }
    }

    [Theory]
    [InlineData(100, 32)]
    [InlineData(32, 12)]
    public async Task CompiledRazorResponse_JoinsTranscript_WithoutWritingToPhysicalTerminal(int width, int height)
    {
        var previous = AnsiConsole.Console;
        var physicalOutput = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(physicalOutput), Ansi = AnsiSupport.No });
        try
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Begin(session);
            var app = new App();
            typeof(App).GetProperty("ProjectRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(app, new ProjectRootAccessor("."));
            var renderResponse = typeof(App).GetMethod("RenderMarkdownGuarded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var turn = 1; turn <= 2; turn++)
            {
                session.AppendUserPrompt($"request {turn}");
                session.SetRunning(true, "Thinking");
                session.SetPreview("temporary response preview");
                renderResponse.Invoke(app, new object[] { $"final answer {turn}" });
                session.SetRunning(false);
            }
            Assert.Equal(4, session.Snapshot().Entries.Count);
            var frame = await TuiLayoutTests.Frame(session, width, height);
            var parts = new[] { "> request 1", "final answer 1", "> request 2", "final answer 2", "type / for commands" };
            var position = 0;
            foreach (var part in parts)
            {
                var next = frame.IndexOf(part, position, StringComparison.Ordinal);
                Assert.True(next >= 0, $"Missing or out of order: {part}\n{frame}");
                position = next + part.Length;
            }
            Assert.DoesNotContain("temporary response preview", frame);
            Assert.Contains("╚", frame.TrimEnd().Split('\n')[^1]);
            Assert.Equal("", physicalOutput.ToString());
        }
        finally { AnsiConsole.Console = previous; }
    }
}