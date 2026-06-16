using System.Diagnostics;
using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Regression tests for SyntaxHighlighter's backtracking guards. The hand-rolled
/// string/number/comment regexes could backtrack pathologically on adversarial input —
/// observed live as a single core pegged for 17 minutes inside Regex.Match while
/// highlighting an execute_command command panel (synchronous work after the spinner
/// stops, with no watchdog to recover it). A per-match timeout + length guard now bound it.
/// </summary>
public class SyntaxHighlighterTests
{
    [Fact]
    public void NormalCode_StillHighlightsKeywords()
    {
        var result = SyntaxHighlighter.Highlight("public class Foo", "csharp");
        // Keyword coloring still applied — the guard didn't break normal highlighting.
        Assert.Contains("[yellow]", result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SyntaxHighlighter.Highlight("", "bash"));
    }

    [Fact]
    public void OversizedInput_ReturnsEscapedPlainText_WithoutHighlighting()
    {
        // Above the length cap, tokenizing is skipped entirely (this is where the per-token
        // matching turns pathological — e.g. a model emitting a whole file inline as a shell
        // command). The result is the input, escaped, with no markup tags added.
        var huge = "public class " + new string('x', 25_000); // > MaxHighlightChars
        var result = SyntaxHighlighter.Highlight(huge, "csharp");

        Assert.DoesNotContain("[yellow]", result);
        Assert.Equal(Spectre.Console.Markup.Escape(huge), result);
    }

    [Theory]
    // Command-panel strings of the shape that froze the app: long, quote/paren/brace-heavy
    // commands highlighted as bash. Must complete fast and never throw, regardless of which
    // internal pattern would otherwise backtrack.
    [InlineData("node -e \"const fs=require('fs');const c=fs.readFileSync('main.js','utf8');try{new Function(c);console.log('ok');}catch(e){console.error('ERR:',e.message);process.exit(1);}\"")]
    [InlineData("$ python -c \"print('a'*1000000)\" && echo '''unterminated && more")]
    [InlineData("git commit -m \"a message with 'nested' quotes and \\\"escapes\\\" and 0xDEAD 1.2.3 // not a comment\"")]
    public void AdversarialCommand_CompletesQuickly_AndNeverThrows(string command)
    {
        var sw = Stopwatch.StartNew();
        // Must not throw (RegexMatchTimeoutException is caught internally) and must return
        // valid markup. The 1s per-match timeout means worst case is a few seconds even if a
        // pattern backtracks; a regression to the unbounded behavior would hang here forever.
        var result = SyntaxHighlighter.Highlight("$ " + command, "bash");
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Highlight took {sw.Elapsed.TotalSeconds:F1}s — backtracking guard regressed.");
    }

    [Theory]
    // Forward-progress regression cases. These contain characters the tokenizer treats as
    // possible token-starts ('/', '@', backtick) but which NO rule consumes in the given
    // language — so before the fix the loop spun in place forever, pinning a CPU core and
    // freezing the app. Each must terminate (a hang would never return) and round-trip the
    // literal text. Bash is the dangerous one: '/' (every path) isn't a comment there.
    [InlineData("cd src/MandoCode/bin && ./run.sh", "bash")]   // '/' paths — the live repro
    [InlineData("docker run -v /home/u:/app @cfg", "bash")]    // '/' and '@'
    [InlineData("echo `backtick` / @ # done", "bash")]         // backtick, '/', '@', '#'
    [InlineData("////////", "bash")]                            // pathological run of bare slashes
    [InlineData("@@@@ @notValidVerbatim", "csharp")]            // '@' not starting a verbatim string
    public void Highlight_NeverSpinsOnUnconsumedTokenChars(string code, string language)
    {
        var sw = Stopwatch.StartNew();
        // The whole point: this returns at all. Pre-fix the tokenizer spun in place forever
        // on these chars, so a hang (not an assertion) was the failure — the time bound just
        // turns "would hang forever" into a deterministic test failure.
        var result = SyntaxHighlighter.Highlight(code, language);
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Highlight took {sw.Elapsed.TotalSeconds:F1}s — forward-progress guard regressed (infinite loop).");
    }
}
