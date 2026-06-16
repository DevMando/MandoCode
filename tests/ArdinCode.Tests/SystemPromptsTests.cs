using Xunit;
using ArdinCode.Models;

namespace ArdinCode.Tests;

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
        var prompt = SystemPrompts.BuildArdinCodeAssistant(webSearchEnabled: true);

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
        var prompt = SystemPrompts.BuildArdinCodeAssistant(webSearchEnabled: false);

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
            var prompt = SystemPrompts.BuildArdinCodeAssistant(enabled);
            Assert.Contains("You are ArdinCode", prompt);
            Assert.Contains("MULTI-STEP PLANNING", prompt);
            Assert.Contains("LARGE FILES", prompt);
        }
    }
}
