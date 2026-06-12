using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

/// <summary>
/// Tests the shared config setter behind both `mandocode --config set` (CLI) and the
/// in-app `/config set` command. One implementation, one set of key names — the split
/// version had already drifted: the stall-watchdog error recommended a
/// `modelResponseTimeout` key the CLI didn't have.
/// </summary>
public class ConfigKeySetterTests
{
    [Fact]
    public void ModelResponseTimeout_TheKeyTheErrorMessageAlwaysPromised_Works()
    {
        var config = new MandoCodeConfig();

        var result = ConfigKeySetter.TrySet(config, "modelResponseTimeout", "300");

        Assert.True(result.Ok);
        Assert.Equal(300, config.ModelResponseTimeoutSeconds);
        Assert.Equal(ConfigKeySetter.ApplyScope.Immediate, result.Scope);
    }

    [Theory]
    [InlineData("watchdog", "240", 240)]                      // alias
    [InlineData("modelResponseTimeoutSeconds", "600", 600)]   // full name
    public void ModelResponseTimeout_Aliases_AllResolve(string key, string value, int expected)
    {
        var config = new MandoCodeConfig();
        Assert.True(ConfigKeySetter.TrySet(config, key, value).Ok);
        Assert.Equal(expected, config.ModelResponseTimeoutSeconds);
    }

    [Theory]
    [InlineData("29")]    // below MinModelResponseTimeoutSeconds
    [InlineData("1801")]  // above max
    [InlineData("fast")]  // not a number
    public void ModelResponseTimeout_RejectsInvalid(string value)
    {
        var config = new MandoCodeConfig();
        var before = config.ModelResponseTimeoutSeconds;

        var result = ConfigKeySetter.TrySet(config, "modelResponseTimeout", value);

        Assert.False(result.Ok);
        Assert.Contains("between", result.Message);
        Assert.Equal(before, config.ModelResponseTimeoutSeconds);
    }

    [Fact]
    public void UnknownKey_FailsWithoutMutating()
    {
        var config = new MandoCodeConfig();
        var result = ConfigKeySetter.TrySet(config, "warpDrive", "engaged");

        Assert.False(result.Ok);
        Assert.Contains("Unknown configuration key", result.Message);
    }

    [Theory]
    // Kernel-baked settings need a rebuild to apply; live ones don't.
    [InlineData("temperature", "0.5", ConfigKeySetter.ApplyScope.KernelRebuild)]
    [InlineData("maxTokens", "16384", ConfigKeySetter.ApplyScope.KernelRebuild)]
    [InlineData("toolBudget", "200000", ConfigKeySetter.ApplyScope.KernelRebuild)]
    [InlineData("webSearch", "false", ConfigKeySetter.ApplyScope.KernelRebuild)]
    [InlineData("timeout", "30", ConfigKeySetter.ApplyScope.Immediate)]
    [InlineData("maxContinuations", "5", ConfigKeySetter.ApplyScope.Immediate)]
    [InlineData("renderTimeout", "60", ConfigKeySetter.ApplyScope.Immediate)]
    [InlineData("diffApprovals", "false", ConfigKeySetter.ApplyScope.AppRestart)]
    [InlineData("contextLength", "16384", ConfigKeySetter.ApplyScope.DaemonRestart)]
    public void ApplyScope_ClassifiesWhereEachSettingTakesEffect(string key, string value, ConfigKeySetter.ApplyScope expected)
    {
        var config = new MandoCodeConfig();
        var result = ConfigKeySetter.TrySet(config, key, value);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(expected, result.Scope);
    }

    [Fact]
    public void ContextLength_Zero_MeansOllamaDefault()
    {
        var config = new MandoCodeConfig { ContextLength = 16384 };
        var result = ConfigKeySetter.TrySet(config, "contextLength", "0");

        Assert.True(result.Ok);
        Assert.Equal(0, config.ContextLength);
        Assert.Contains("Ollama's own default", result.Message);
    }

    [Fact]
    public void TavilyKey_Set_StoresKey_MasksItInTheMessage_AndCarriesValidation()
    {
        var config = new MandoCodeConfig();

        var result = ConfigKeySetter.TrySet(config, "tavilyKey", "tvly-abcdef1234567890");

        Assert.True(result.Ok);
        Assert.Equal("tvly-abcdef1234567890", config.TavilyApiKey);
        Assert.Equal(ConfigKeySetter.ApplyScope.KernelRebuild, result.Scope);
        // The confirmation must never echo the full secret back.
        Assert.DoesNotContain("tvly-abcdef1234567890", result.Message);
        Assert.Contains("tvly-", result.Message);
        // The live verification probe rides on the result for the caller to run post-save.
        Assert.NotNull(result.PostSetValidation);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("none")]
    public void TavilyKey_Clear_RemovesKey(string clearWord)
    {
        var config = new MandoCodeConfig { TavilyApiKey = "tvly-abcdef1234567890" };

        var result = ConfigKeySetter.TrySet(config, "tavilyKey", clearWord);

        Assert.True(result.Ok);
        Assert.Null(config.TavilyApiKey);
        Assert.Contains("DuckDuckGo", result.Message);
        Assert.Null(result.PostSetValidation);
    }

    [Fact]
    public void TavilyKey_UnexpectedPrefix_StillSavesButWarns()
    {
        var config = new MandoCodeConfig();

        var result = ConfigKeySetter.TrySet(config, "tavilyKey", "sk-wrong-provider-key-12345");

        Assert.True(result.Ok);
        Assert.Equal("sk-wrong-provider-key-12345", config.TavilyApiKey);
        Assert.Contains("tvly-", result.Message); // the "keys normally start with" warning
    }

    [Fact]
    public void DescribeKeys_MasksTheTavilyKey()
    {
        var config = new MandoCodeConfig { TavilyApiKey = "tvly-abcdef1234567890" };
        var listing = ConfigKeySetter.DescribeKeys(config);

        Assert.Contains("tavilyKey", listing);
        Assert.DoesNotContain("tvly-abcdef1234567890", listing);

        Assert.Contains("not set", ConfigKeySetter.DescribeKeys(new MandoCodeConfig()));
    }

    [Fact]
    public void MaskApiKey_NeverRevealsTheMiddle_AndHandlesShortKeys()
    {
        Assert.Equal("tvly-…7890", MandoCodeConfig.MaskApiKey("tvly-abcdef1234567890"));
        Assert.Equal("****", MandoCodeConfig.MaskApiKey("short"));
    }

    [Fact]
    public void DescribeKeys_ListsEveryUserFacingKey_WithCurrentValues()
    {
        var config = new MandoCodeConfig { ModelName = "qwen3.5:4b", ModelResponseTimeoutSeconds = 300 };
        var listing = ConfigKeySetter.DescribeKeys(config);

        // Every key in the listing must round-trip through TrySet — this is the
        // anti-drift guard for the "menu" output.
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = line.TrimStart().Split(' ')[0];
            if (key is "model" or "endpoint") continue; // free-text keys, trivially settable
            var probe = ConfigKeySetter.TrySet(new MandoCodeConfig(), key, "true");
            Assert.DoesNotContain("Unknown configuration key", probe.Message);
        }

        Assert.Contains("qwen3.5:4b", listing);
        Assert.Contains("300s", listing);
        Assert.Contains("modelResponseTimeout", listing);
        Assert.Contains("contextLength", listing);
    }
}
