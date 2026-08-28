using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Regression tests for plan-step context seeding. Steps execute in isolated chat
/// histories and used to see only the model's distilled `goal` — a lossy summary.
/// Observed live: "@STarfox/ create a starfox64 inspired game…" became goal
/// "create a starfox64 inspired game in three.js", and every step wrote to the
/// project root (editing a leftover index.html) instead of STarfox/. The verbatim
/// user request is now included in every step's context as the authority on
/// target paths.
/// </summary>
public class PlanStepContextTests
{
    private const string SystemPrompt = "You are a helpful coding assistant.";

    [Fact]
    public void IncludesVerbatimUserRequest()
    {
        var context = AIService.BuildStepContext(
            SystemPrompt,
            "in @STarfox/ folder create a starfox64 inspired game in three.js\n[Directory] STarfox/",
            new List<string>());

        Assert.Contains("STarfox/", context);
        Assert.Contains("The User's Original Request", context);
        Assert.Contains("authoritative for WHERE work happens", context);
    }

    [Fact]
    public void NoUserRequest_OmitsTheSection()
    {
        var context = AIService.BuildStepContext(SystemPrompt, null, new List<string>());

        Assert.DoesNotContain("Original Request", context);
        Assert.StartsWith(SystemPrompt, context);
    }

    [Fact]
    public void CapsHugeAttachedContent_ButKeepsTheHead()
    {
        // A pasted @file expansion can be enormous; the head (where the user's actual
        // ask and folder references live) must survive truncation.
        var request = "in @STarfox/ build the game\n" + new string('x', 20_000);

        var context = AIService.BuildStepContext(SystemPrompt, request, new List<string>());

        Assert.Contains("@STarfox/", context);
        Assert.Contains("[truncated]", context);
        Assert.True(context.Length < SystemPrompt.Length + 6000,
            $"Step context too large: {context.Length} chars");
    }

    [Fact]
    public void IncludesOnlyLastTwoPreviousStepResults()
    {
        var results = new List<string> { "result one", "result two", "result three" };

        var context = AIService.BuildStepContext(SystemPrompt, "do the thing", results);

        Assert.DoesNotContain("result one", context);
        Assert.Contains("result two", context);
        Assert.Contains("result three", context);
    }

    // ---- File manifest ----
    //
    // Steps only carry the last two prose summaries forward, which describe work rather than
    // naming files. That left a step guessing: observed live, step 3 wrote
    // getElementById('gameCanvas') against step 1's id="game-canvas" and the game never started;
    // another run re-read a 750-line file five times in one step, contributing to 1.1M tokens.

    private static readonly (string Operation, string Path)[] Ops =
    [
        ("write_file", "index.html"),
        ("write_file", "style.css"),
        ("edit_file",  "index.html"),
        ("write_file", "game.js"),
    ];

    [Fact]
    public void ListsFilesEarlierStepsTouched()
    {
        var context = AIService.BuildStepContext(SystemPrompt, "build a game", [], Ops);

        Assert.Contains("index.html", context);
        Assert.Contains("style.css", context);
        Assert.Contains("game.js", context);
    }

    [Fact]
    public void ListsEachFileOnce_EvenWhenTouchedRepeatedly()
    {
        var context = AIService.BuildStepContext(SystemPrompt, "build a game", [], Ops);

        var section = context[context.IndexOf("--- Files This Plan", StringComparison.Ordinal)..];
        var occurrences = section.Split("index.html").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void TellsTheModelNotToGuessNamesOrReReadFiles()
    {
        // The two failure modes this section exists to prevent.
        var context = AIService.BuildStepContext(SystemPrompt, "build a game", [], Ops);

        Assert.Contains("do not read the same file twice", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never guess at element ids", context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OmitsTheSectionEntirely_WhenNothingHasBeenWritten()
    {
        // The first step of a plan has no manifest, and an empty header would be noise.
        var context = AIService.BuildStepContext(SystemPrompt, "build a game", [], []);
        Assert.DoesNotContain("--- Files This Plan", context);

        Assert.DoesNotContain("--- Files This Plan",
            AIService.BuildStepContext(SystemPrompt, "build a game", []));
    }

    [Fact]
    public void CapsTheListSoOneStepCannotFloodTheContext()
    {
        var many = Enumerable.Range(1, 60).Select(i => ("write_file", $"file{i}.cs")).ToArray();
        var context = AIService.BuildStepContext(SystemPrompt, "big refactor", [], many);

        Assert.Contains("file1.cs", context);
        Assert.DoesNotContain("file60.cs", context);
        Assert.Contains("and 20 more", context);
    }

    // ---- Step boundary ----

    [Fact]
    public void StepMessage_TellsTheModelToDoOnlyThisStep()
    {
        // Observed live: a step scoped to "create the game HTML shell" wrote the HTML, the CSS and
        // all 612 lines of the engine, leaving the plan's other three steps with nothing to do.
        var message = AIService.BuildStepUserMessage("Create the game HTML shell");

        Assert.Contains("Create the game HTML shell", message);
        Assert.Contains("ONLY this step", message);
        Assert.Contains("later steps", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StepMessage_StillInsistsOnRealToolCalls()
    {
        // Long-standing local-model failure: describing a call instead of making one.
        var message = AIService.BuildStepUserMessage("do the thing");
        Assert.Contains("actually invoke it", message);
    }
}
