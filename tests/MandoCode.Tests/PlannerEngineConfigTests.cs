using System.Text.Json;
using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

/// <summary>
/// Tests the `planner` config key. Its nullability is load-bearing, not incidental: when the
/// default eventually flips to the workflow engine, null must still mean "follow the build" while
/// an explicit "legacy" still means "the user chose this". With a non-nullable default those two
/// states are indistinguishable and the flip becomes a guess — the same guess Migrate() already
/// has to make for ModelResponseTimeoutSeconds.
/// </summary>
public class PlannerEngineConfigTests
{
    [Fact]
    public void DefaultsToNull_MeaningBuildDefault()
    {
        Assert.Null(new MandoCodeConfig().PlannerEngine);
    }

    [Fact]
    public void IsNotWritten_WhenUnset()
    {
        // Older builds reserialize the whole config on save and have no UnmappedMemberHandling,
        // so emitting a null key would be noise they'd drop anyway.
        var json = JsonSerializer.Serialize(new MandoCodeConfig());
        Assert.DoesNotContain("\"planner\"", json);
    }

    [Fact]
    public void RoundTrips_WhenSet()
    {
        var json = JsonSerializer.Serialize(
            new MandoCodeConfig { PlannerEngine = MandoCodeConfig.PlannerEngineLegacy });

        Assert.Contains("\"planner\"", json);
        Assert.Equal(
            MandoCodeConfig.PlannerEngineLegacy,
            JsonSerializer.Deserialize<MandoCodeConfig>(json)!.PlannerEngine);
    }

    [Fact]
    public void AbsentKey_DeserializesToNull()
    {
        var config = JsonSerializer.Deserialize<MandoCodeConfig>("""{"modelName":"qwen3:8b"}""")!;
        Assert.Null(config.PlannerEngine);
    }

    [Fact]
    public void TrySet_AcceptsLegacy()
    {
        var config = new MandoCodeConfig();
        var result = ConfigKeySetter.TrySet(config, "planner", "legacy");

        Assert.True(result.Ok);
        Assert.Equal(MandoCodeConfig.PlannerEngineLegacy, config.PlannerEngine);
        Assert.Equal(ConfigKeySetter.ApplyScope.KernelRebuild, result.Scope);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("auto")]
    [InlineData("clear")]
    public void TrySet_ResetsToBuildDefault(string value)
    {
        var config = new MandoCodeConfig { PlannerEngine = MandoCodeConfig.PlannerEngineLegacy };
        var result = ConfigKeySetter.TrySet(config, "planner", value);

        Assert.True(result.Ok);
        Assert.Null(config.PlannerEngine);
    }

    [Fact]
    public void TrySet_RejectsWorkflow_UntilTheGraphExists()
    {
        // Accepting a name that does nothing would let someone select it and believe it took.
        var config = new MandoCodeConfig();
        var result = ConfigKeySetter.TrySet(config, "planner", "workflow");

        Assert.False(result.Ok);
        Assert.Contains("not available", result.Message);
        Assert.Null(config.PlannerEngine);
    }

    [Fact]
    public void TrySet_RejectsUnknownValues()
    {
        var config = new MandoCodeConfig();
        Assert.False(ConfigKeySetter.TrySet(config, "planner", "magentic").Ok);
        Assert.Null(config.PlannerEngine);
    }

    [Fact]
    public void PlannerKey_IsIndependentOfEnableTaskPlanning()
    {
        // Overloading enableTaskPlanning as the engine switch would make "planning off" and
        // "old engine" the same state, and render any A/B between engines uninterpretable.
        var config = new MandoCodeConfig();
        ConfigKeySetter.TrySet(config, "planner", "legacy");

        Assert.True(config.EnableTaskPlanning);
        Assert.Equal(MandoCodeConfig.PlannerEngineLegacy, config.PlannerEngine);

        ConfigKeySetter.TrySet(config, "taskPlanning", "false");

        Assert.False(config.EnableTaskPlanning);
        Assert.Equal(MandoCodeConfig.PlannerEngineLegacy, config.PlannerEngine);
    }
}
