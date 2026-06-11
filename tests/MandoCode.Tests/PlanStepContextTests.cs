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
}
