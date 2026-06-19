using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

public class MandoCodeConfigTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(61, false)]
    public void IsValidRequestTimeout_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsValidRequestTimeout(value));
    }

    [Fact]
    public void CreateDefault_UsesFifteenMinuteTimeout()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal(15, config.RequestTimeoutMinutes);
    }

    [Theory]
    [InlineData(50_000L, true)]
    [InlineData(400_000L, true)]
    [InlineData(4_000_000L, true)]
    [InlineData(49_999L, false)]
    [InlineData(4_000_001L, false)]
    [InlineData(0L, false)]
    public void IsValidToolResultCharBudget_EnforcesBounds(long value, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsValidToolResultCharBudget(value));
    }

    [Fact]
    public void CreateDefault_UsesSensibleToolResultBudget()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal(100_000L, config.ToolResultCharBudget);
    }

    [Theory]
    [InlineData(0, true)]      // 0 = "leave Ollama's default alone"
    [InlineData(2048, true)]
    [InlineData(8192, true)]
    [InlineData(262144, true)]
    [InlineData(1, false)]
    [InlineData(2047, false)]
    [InlineData(262145, false)]
    [InlineData(-1, false)]
    public void IsValidContextLength_EnforcesBounds_AndAllowsZero(int value, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsValidContextLength(value));
    }

    [Fact]
    public void ValidateAndClamp_PreservesZeroContextLength_ClampsOutOfRange()
    {
        var zero = new MandoCodeConfig { ContextLength = 0 };
        zero.ValidateAndClamp();
        Assert.Equal(0, zero.ContextLength);

        var tiny = new MandoCodeConfig { ContextLength = 100 };
        tiny.ValidateAndClamp();
        Assert.Equal(MandoCodeConfig.MinContextLength, tiny.ContextLength);

        var huge = new MandoCodeConfig { ContextLength = 999_999_999 };
        huge.ValidateAndClamp();
        Assert.Equal(MandoCodeConfig.MaxContextLength, huge.ContextLength);
    }

    [Fact]
    public void CreateDefault_SetsLocalContextWindow()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal(8192, config.ContextLength);
    }

    [Theory]
    [InlineData("minimax-m2.7:cloud", true)]
    [InlineData("kimi-k2.6:cloud", true)]
    [InlineData("qwen3-coder:480b-cloud", true)]
    [InlineData("QWEN3:480B-CLOUD", true)]   // case-insensitive
    [InlineData("qwen3.5:9b", false)]
    [InlineData("mistral", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCloudModel_DetectsCloudSuffix(string? tag, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsCloudModel(tag));
    }

    [Theory]
    // Cloud → 0: context is managed server-side, leave local config alone.
    [InlineData("minimax-m2.7:cloud", 0)]
    [InlineData("qwen3-coder:480b-cloud", 0)]
    // Local tiers: the tag's parameter count implies the user's hardware.
    [InlineData("qwen3.5:0.8b", 8192)]
    [InlineData("qwen3.5:2b", 8192)]
    [InlineData("qwen3.5:4b", 16384)]
    [InlineData("qwen3.5:9b", 32768)]
    [InlineData("qwen2.5-coder:14b", 32768)]
    [InlineData("qwen3:8b-q4_K_M", 32768)]   // size parses past the quant suffix
    // Unparseable local tags get the safe floor.
    [InlineData("mistral", 8192)]
    [InlineData("llama3.1:latest", 8192)]
    [InlineData(null, 8192)]
    public void RecommendedContextLength_MapsTierToWindow(string? tag, int expected)
    {
        Assert.Equal(expected, MandoCodeConfig.RecommendedContextLength(tag));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    public void IsValidMaxAutoContinuations_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsValidMaxAutoContinuations(value));
    }

    [Fact]
    public void CreateDefault_EnablesAutoContinuation()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.True(config.EnableAutoContinuation);
        Assert.Equal(3, config.MaxAutoContinuations);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(180, true)]
    [InlineData(1800, true)]
    [InlineData(29, false)]
    [InlineData(0, false)]
    [InlineData(1801, false)]
    public void IsValidModelResponseTimeout_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, MandoCodeConfig.IsValidModelResponseTimeout(value));
    }

    [Fact]
    public void CreateDefault_UsesGenerousStallWatchdog()
    {
        // 680s, not 180/420: calls are non-streaming, so the watchdog gets no signal during
        // generation — a long reply on a slow provider (~10 tok/s observed) legitimately
        // runs many minutes and was killed mid-generation at both older defaults. Stays
        // well below the 900s (15-min) request ceiling so the watchdog can still fire first.
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal(680, config.ModelResponseTimeoutSeconds);
    }

    [Fact]
    public void CreateDefault_StampsCurrentConfigVersion()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal(MandoCodeConfig.CurrentConfigVersion, config.ConfigVersion);
    }

    [Fact]
    public void CreateDefault_StreamsAllModels()
    {
        // Default is "all" — stream every model (cloud + local), verified via the streaming spikes.
        var config = MandoCodeConfig.CreateDefault();
        Assert.Equal("all", config.ResponseStreaming);
        Assert.Equal(ResponseStreamingMode.All, config.StreamingMode);
    }

    [Fact]
    public void StreamingMode_DefaultsToAll_WhenAbsentFromConfigJson()
    {
        // Configs that predate the field leave the initializer intact → streaming "all".
        var loaded = System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>("{}");
        Assert.NotNull(loaded);
        Assert.Equal(ResponseStreamingMode.All, loaded!.StreamingMode);
    }

    [Theory]
    [InlineData("off", ResponseStreamingMode.Off)]
    [InlineData("cloud", ResponseStreamingMode.Cloud)]
    [InlineData("all", ResponseStreamingMode.All)]
    [InlineData("CLOUD", ResponseStreamingMode.Cloud)]   // case-insensitive
    [InlineData("All", ResponseStreamingMode.All)]
    [InlineData("garbage", ResponseStreamingMode.All)]   // unknown heals to All
    [InlineData("", ResponseStreamingMode.All)]
    public void StreamingMode_ParsesLeniently(string raw, ResponseStreamingMode expected)
    {
        var config = new MandoCodeConfig { ResponseStreaming = raw };
        Assert.Equal(expected, config.StreamingMode);
    }

    [Theory]
    [InlineData("CLOUD", "cloud")]
    [InlineData("All", "all")]
    [InlineData("nonsense", "all")]   // unknown heals to the default token
    [InlineData("off", "off")]
    public void ValidateAndClamp_NormalizesStreamingToken(string raw, string expected)
    {
        var config = new MandoCodeConfig { ResponseStreaming = raw };
        config.ValidateAndClamp();
        Assert.Equal(expected, config.ResponseStreaming);
    }

    [Theory]
    [InlineData(180)]   // ≤ v0.11.0 default
    [InlineData(420)]   // v0.12.0 default
    public void Migrate_BumpsOldDefaultStallWatchdog_AndStampsVersion(int oldDefault)
    {
        // Simulates a config written by an older version: the field carries an old default
        // and ConfigVersion is absent (0).
        var config = new MandoCodeConfig { ModelResponseTimeoutSeconds = oldDefault, ConfigVersion = 0 };

        var changed = config.Migrate();

        Assert.True(changed);
        Assert.Equal(680, config.ModelResponseTimeoutSeconds);
        Assert.Equal(MandoCodeConfig.CurrentConfigVersion, config.ConfigVersion);
    }

    [Fact]
    public void Migrate_LeavesDeliberatelyCustomizedValue_ButStillStampsVersion()
    {
        // A value that was never a shipped default → the user chose it; don't touch it.
        var config = new MandoCodeConfig { ModelResponseTimeoutSeconds = 300, ConfigVersion = 0 };

        var changed = config.Migrate();

        Assert.True(changed);                                  // version stamp still advances
        Assert.Equal(300, config.ModelResponseTimeoutSeconds); // value preserved
        Assert.Equal(MandoCodeConfig.CurrentConfigVersion, config.ConfigVersion);
    }

    [Fact]
    public void Migrate_IsIdempotent_OnceStamped()
    {
        var config = new MandoCodeConfig { ModelResponseTimeoutSeconds = 420, ConfigVersion = 0 };
        Assert.True(config.Migrate());      // first run bumps 420 → 680
        Assert.Equal(680, config.ModelResponseTimeoutSeconds);

        // A subsequent run is a no-op: nothing changed, value untouched even if it now
        // happens to differ from any default.
        Assert.False(config.Migrate());
        Assert.Equal(680, config.ModelResponseTimeoutSeconds);
    }

    [Theory]
    [InlineData(5, MandoCodeConfig.MinModelResponseTimeoutSeconds)]
    [InlineData(99999, MandoCodeConfig.MaxModelResponseTimeoutSeconds)]
    [InlineData(240, 240)]
    public void ValidateAndClamp_BoundsModelResponseTimeout(int input, int expected)
    {
        var config = MandoCodeConfig.CreateDefault();
        config.ModelResponseTimeoutSeconds = input;
        config.ValidateAndClamp();
        Assert.Equal(expected, config.ModelResponseTimeoutSeconds);
    }
}
