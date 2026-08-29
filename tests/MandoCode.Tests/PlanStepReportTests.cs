using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public sealed class PlanStepReportTests
{
    [Fact]
    public void SuccessMarker_IsRemovedFromDisplayedResult()
    {
        var result = PlanStepReport.Parse("Created and verified the file.\n[PLAN_STEP_RESULT:SUCCESS]");

        Assert.True(result.Succeeded);
        Assert.Equal("Created and verified the file.", result.DisplayText);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void FailedMarker_ReturnsExplicitReason()
    {
        var result = PlanStepReport.Parse(
            "The requested file does not exist.\n[PLAN_STEP_RESULT:FAILED] Expected file was missing.");

        Assert.False(result.Succeeded);
        Assert.Equal("Expected file was missing.", result.FailureReason);
        Assert.DoesNotContain("PLAN_STEP_RESULT", result.DisplayText);
    }

    [Fact]
    public void MissingMarker_RemainsBackwardCompatible()
    {
        var result = PlanStepReport.Parse("Ordinary response from an older or noncompliant model.");

        Assert.Null(result.Succeeded);
        Assert.Equal("Ordinary response from an older or noncompliant model.", result.DisplayText);
    }

    [Fact]
    public void StepContext_RequiresTruthfulTerminalOutcome()
    {
        var context = AIService.BuildStepContext("system", "goal", []);

        Assert.Contains("PLAN_STEP_RESULT:SUCCESS", context);
        Assert.Contains("Never call a failed verification success", context);
    }
}
