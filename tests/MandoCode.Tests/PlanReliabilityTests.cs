using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PlanReliabilityTests
{
    private static TaskPlan Plan() => new() { OriginalRequest = "goal", Steps =
        [new TaskStep { StepNumber = 1, Description = "first", Instruction = "first" },
         new TaskStep { StepNumber = 2, Description = "second", Instruction = "second" }] };

    [Theory]
    [InlineData(TaskProgressType.PlanCreated)]
    [InlineData(TaskProgressType.StepStarted)]
    [InlineData(TaskProgressType.StepCompleted)]
    [InlineData(TaskProgressType.StepFailed)]
    public async Task ConsumerBreak_ReleasesProducerWithoutExternalCancellation(TaskProgressType stopAt)
    {
        var executor = new ScriptedPlanStepExecutor((_, _) =>
            stopAt == TaskProgressType.StepFailed ? throw new IOException("failure") : "ok");
        var plan = Plan();
        var runner = new WorkflowPlanRunner(executor);
        async Task Consume()
        {
            await foreach (var e in runner.ExecutePlanAsync(plan))
                if (e.ProgressType == stopAt) break;
        }
        await Consume().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("second", executor.Executed);
        if (stopAt is TaskProgressType.PlanCreated or TaskProgressType.StepStarted)
            Assert.Empty(executor.Executed);
    }

    [Fact]
    public async Task ConsumerException_IsPreservedAndDoesNotHang()
    {
        var runner = new WorkflowPlanRunner(new ScriptedPlanStepExecutor());
        async Task Consume()
        {
            await foreach (var e in runner.ExecutePlanAsync(Plan()))
                throw new InvalidOperationException("UI failed");
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Consume().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("UI failed", error.Message);
    }

    [Fact]
    public async Task PersistenceFailure_IsVisibleAndDoesNotStopExecution()
    {
        var runner = new WorkflowPlanRunner(new ScriptedPlanStepExecutor(),
            onStateSaved: _ => throw new IOException("Disk full; progress could not be saved"));
        var events = new List<TaskProgressEvent>();
        var plan = Plan();
        await foreach (var e in runner.ExecutePlanAsync(plan)) events.Add(e);
        Assert.Contains(events, e => e.ProgressType == TaskProgressType.PersistenceWarning);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public void ToolEvidence_DoesNotTrustAssistantSuccess()
    {
        Assert.Empty(PlanToolEvidence.Capture([new ChatMessage(ChatRole.Assistant, "[PLAN_STEP_RESULT:SUCCESS]")]));
        var messages = new[] {
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "run_tests", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "FAIL: expected 2, got 1")]) };
        Assert.Contains("FAIL: expected 2, got 1", PlanToolEvidence.Capture(messages));
    }

    [Fact]
    public void Grounding_ReadsSourceButNotCredentialsOrBuildOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "Existing architecture");
            File.WriteAllText(Path.Combine(dir, ".env"), "DO NOT READ");
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            File.WriteAllText(Path.Combine(dir, "bin", "Generated.cs"), "IGNORE BUILD");
            var context = PlanRepositoryContext.Capture(dir, "architecture");
            Assert.Contains("Existing architecture", context);
            Assert.DoesNotContain("DO NOT READ", context);
            Assert.DoesNotContain("IGNORE BUILD", context);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MutationEvidence_IsCheckpointedBeforeTheStepFinishes()
    {
        var handoff = new PlanHandoff();
        using var execution = handoff.BeginResumedExecution([]);
        PlanRunState? latest = null;
        var executor = new ScriptedPlanStepExecutor((_, _) =>
        {
            handoff.RecordFileOperation("write_file", "partial.cs");
            Assert.NotNull(latest);
            Assert.Contains(latest.FileOperations, f => f.Path == "partial.cs");
            Assert.Equal(TaskStepStatus.InProgress, latest.Steps[0].Status);
            return "ok";
        });
        var runner = new WorkflowPlanRunner(executor, handoff, state => latest = state);
        var plan = Plan();
        await foreach (var e in runner.ExecutePlanAsync(plan))
            if (e.ProgressType == TaskProgressType.StepCompleted) break;
        Assert.NotNull(latest);
        Assert.Contains(latest.FileOperations, f => f.Path == "partial.cs");
    }

    [Fact]
    public void LongPlanContext_PreservesEarlyAndRecentDecisionsWithinBudget()
    {
        var results = Enumerable.Range(1, 20).Select(i =>
            $"Step {i}: decision-{i} " + new string('x', 2000) + $" artifact-{i}").ToList();
        var context = AIService.BuildStepContext("system", "goal", results);
        Assert.Contains("decision-1 ", context);
        Assert.Contains("artifact-1", context);
        Assert.Contains("decision-20 ", context);
        Assert.True(context.Length < 8000);
    }
}
