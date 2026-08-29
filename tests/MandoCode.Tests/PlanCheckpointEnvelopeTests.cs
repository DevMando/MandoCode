using System.Text.Json;
using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests the versioned wrapper around the durable plan snapshot. Every field outside the payload is a
/// refusal criterion, because the failure mode of resuming a stale or foreign checkpoint is
/// re-running steps whose write_file already succeeded.
/// </summary>
public class PlanCheckpointEnvelopeTests
{
    private static PlanCheckpointEnvelope Make(
        int? schemaVersion = null,
        string? topologyVersion = null,
        string projectRootHash = "abc123abc123",
        string modelName = "qwen3:8b")
        => new()
        {
            SchemaVersion = schemaVersion ?? PlanCheckpointEnvelope.CurrentSchemaVersion,
            TopologyVersion = topologyVersion ?? PlanExecutorIds.TopologyVersion,
            PlanId = "plan-1",
            ProjectRootHash = projectRootHash,
            ModelName = modelName,
            MandoCodeVersion = "0.15.0",
            CreatedUtc = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
            Payload = JsonSerializer.SerializeToElement(new { opaque = true }),
        };

    [Fact]
    public void RoundTrips_ThroughJson()
    {
        var json = JsonSerializer.Serialize(Make());
        var back = JsonSerializer.Deserialize<PlanCheckpointEnvelope>(json)!;

        Assert.Equal(PlanCheckpointEnvelope.CurrentSchemaVersion, back.SchemaVersion);
        Assert.Equal(PlanExecutorIds.TopologyVersion, back.TopologyVersion);
        Assert.Equal("plan-1", back.PlanId);
        Assert.Equal("qwen3:8b", back.ModelName);
        Assert.True(back.Payload.GetProperty("opaque").GetBoolean());
    }

    [Fact]
    public void MatchingEnvelope_IsResumable()
    {
        Assert.Null(Make().FindIncompatibility("abc123abc123", "qwen3:8b"));
    }

    [Fact]
    public void SchemaVersionMismatch_IsRefused()
    {
        var reason = Make(schemaVersion: 99).FindIncompatibility("abc123abc123", "qwen3:8b");
        Assert.NotNull(reason);
        Assert.Contains("different version", reason);
    }

    [Fact]
    public void TopologyVersionMismatch_IsRefused()
    {
        // A graph-shape change means the checkpoint's step boundaries no longer mean what they did.
        var reason = Make(topologyVersion: "0").FindIncompatibility("abc123abc123", "qwen3:8b");
        Assert.NotNull(reason);
        Assert.Contains("Start it again", reason);
    }

    [Fact]
    public void DifferentProject_IsRefused()
    {
        var reason = Make().FindIncompatibility("ffffffffffff", "qwen3:8b");
        Assert.NotNull(reason);
        Assert.Contains("different project", reason);
    }

    [Fact]
    public void DifferentModel_IsRefused_AndNamesBothModels()
    {
        // Half a plan run by one model and half by another is not a state anyone can reason about.
        var reason = Make().FindIncompatibility("abc123abc123", "gemma3:12b");
        Assert.NotNull(reason);
        Assert.Contains("qwen3:8b", reason);
        Assert.Contains("gemma3:12b", reason);
    }

    [Fact]
    public void DifferentDesktopAgentSession_IsRefused()
    {
        var reason = Make().FindIncompatibility(
            "abc123abc123",
            "qwen3:8b",
            expectedPlanId: "another-agent");

        Assert.NotNull(reason);
        Assert.Contains("different agent session", reason);
    }

    [Fact]
    public void ProjectRootHash_IsStable_AndCaseInsensitive()
    {
        var a = PlanCheckpointEnvelope.HashProjectRoot(@"C:\work\Api");
        var b = PlanCheckpointEnvelope.HashProjectRoot(@"c:\work\api\");

        Assert.Equal(a, b);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void ProjectRootHash_DistinguishesSameLeafInDifferentPlaces()
    {
        // Two folders both named "api" must not collide — the reason the hash exists at all.
        Assert.NotEqual(
            PlanCheckpointEnvelope.HashProjectRoot(@"C:\one\api"),
            PlanCheckpointEnvelope.HashProjectRoot(@"C:\two\api"));
    }
}
