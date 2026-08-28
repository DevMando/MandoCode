using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

/// <summary>
/// Guards the conditional web-access section of the main system prompt. Observed live
/// (minimax-m3): with only a passive "you can search" mention, the model recited its
/// knowledge-cutoff disclaimer and refused to call the search_web tool it had — and the
/// static prompt advertised search even in sessions where the plugin wasn't registered.
/// </summary>
public class SystemPromptsTests
{
    [Fact]
    public void WebSearchEnabled_PromptAssertsLiveAccess_AndForbidsCutoffDisclaimers()
    {
        var prompt = SystemPrompts.BuildMandoCodeAssistant(webSearchEnabled: true);

        Assert.Contains("LIVE WEB ACCESS", prompt);
        Assert.Contains("search_web", prompt);
        Assert.Contains("fetch_webpage", prompt);
        // The two anti-reflex rules that fix the observed failure: don't deny the
        // capability, don't punt the user to Google.
        Assert.Contains("NEVER tell the user you lack internet access", prompt);
        Assert.Contains("NEVER direct the user to search Google", prompt);
        Assert.DoesNotContain("disabled", prompt);
    }

    [Fact]
    public void WebSearchDisabled_PromptStopsAdvertisingSearchTools()
    {
        var prompt = SystemPrompts.BuildMandoCodeAssistant(webSearchEnabled: false);

        Assert.DoesNotContain("LIVE WEB ACCESS", prompt);
        // The capability list must not promise tools that aren't registered. The only
        // remaining mention is the disabled notice telling the user how to enable it.
        Assert.DoesNotContain("You can search the web", prompt);
        Assert.Contains("Web search is currently disabled", prompt);
        Assert.Contains("/config set websearch true", prompt);
    }

    [Fact]
    public void BothVariants_KeepTheCoreAssistantIdentity()
    {
        foreach (var enabled in new[] { true, false })
        {
            var prompt = SystemPrompts.BuildMandoCodeAssistant(enabled);
            Assert.Contains("You are MandoCode", prompt);
            Assert.Contains("MULTI-STEP PLANNING", prompt);
            Assert.Contains("LARGE FILES", prompt);
        }
    }

    [Fact]
    public void ProgressLines_AreNotNumbered()
    {
        // The model cannot know how many pieces of work there will be, so it invents a total.
        // Observed live: a 3-step plan rendered the harness's real "Step 2/3:" header directly above
        // the model's own "(Step 2/5)" line, which reads as a broken progress display.
        var prompt = SystemPrompts.BuildMandoCodeAssistant(webSearchEnabled: false);

        Assert.DoesNotContain("(Step 1/5)", prompt);
        Assert.DoesNotContain("Always number your steps", prompt);
        Assert.Contains("Do NOT number these lines", prompt);
    }

    [Fact]
    public void PlanningSection_DoesNotPromiseACompletionSummary()
    {
        // propose_plan returns a receipt as soon as the plan is queued; it no longer blocks until
        // the plan has run, so telling the model to expect the outcome would be a lie.
        var prompt = SystemPrompts.BuildMandoCodeAssistant(webSearchEnabled: false);

        Assert.Contains("returns as soon as the plan is queued", prompt);
        Assert.DoesNotContain("You will receive a summary string when planning completes", prompt);
    }
}
