using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Completion is decided by the executor's own report plus mechanical signals read off tool
/// history. No second model participates, so these are the only ways a step can fail.
/// </summary>
public class PlanStepRecoveryTests
{
    private static TaskPlan Plan() => new() { OriginalRequest = "Build movement", Steps =
        [new TaskStep { StepNumber = 1, Instruction = "Implement intersection turns", Description = "Movement" }] };

    private static Task<string> Run(TaskStep step, Func<string, PlanStepEvidence> execute,
        Func<string, Task>? activity = null, CancellationToken ct = default) =>
        PlanStepRecovery.RunAsync(step, (instruction, _) => Task.FromResult(execute(instruction)),
            activity ?? (_ => Task.CompletedTask), ct);

    [Fact]
    public async Task ExecutorSuccessWithFreshChecks_CompletesWithoutASecondOpinion()
    {
        var step = Plan().Steps[0];
        var result = await Run(step, instruction => new PlanStepEvidence(instruction, "41 passed",
            "execute_command: node test_acceptance.mjs -> 41 passed, 0 failed"));
        Assert.Equal("41 passed", result);
        Assert.Null(step.ErrorMessage);
        Assert.False(step.VerificationPending);
    }

    [Fact]
    public void CodeEditAfterTest_InvalidatesPreviousChecks()
    {
        Assert.NotNull(PlanToolEvidence.AssessFreshness(Tools("execute_command", "edit_file")));
        Assert.NotNull(PlanToolEvidence.AssessFreshness(Tools("execute_command", "edit_file", "read_file")));
        Assert.Null(PlanToolEvidence.AssessFreshness(Tools("edit_file", "execute_command")));
    }

    [Fact]
    public async Task StaleChecks_FailTheStepEvenWhenTheExecutorClaimsSuccess()
    {
        var step = Plan().Steps[0];
        var failure = await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => Run(step,
            instruction => new PlanStepEvidence(instruction, "SUCCESS", "test then edit", "Rerun tests after entity.js edit")));
        Assert.Contains("Rerun tests", failure.Message);
        Assert.False(step.VerificationPending);
        Assert.Equal(failure.Message, step.ErrorMessage);
    }

    [Fact]
    public async Task ReportedFailureAndStaleChecks_BothReachTheRepairInstruction()
    {
        var step = Plan().Steps[0];
        var failure = await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => Run(step,
            instruction => new PlanStepEvidence(instruction, "done", "evidence",
                FreshnessFailure: "Rerun tests after entity.js edit", ReportedFailure: "DOWN turn is wrong")));
        Assert.Contains("DOWN turn is wrong", failure.Message);
        Assert.Contains("Also rerun the acceptance checks", failure.Message);
    }

    [Fact]
    public async Task StepThatTouchedNoTools_CannotPassOnItsClaimAlone()
    {
        var step = Plan().Steps[0];
        var failure = await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => Run(step,
            instruction => new PlanStepEvidence(instruction, "All done!", "")));
        Assert.Contains("No tool evidence", failure.Message);
    }

    [Fact]
    public async Task AcceptanceChecklistIsHandedToTheExecutorAndNeverGrows()
    {
        var step = Plan().Steps[0];
        step.AcceptanceCriteria = ["Turns register at intersections", "No wall clipping"];
        string? seen = null;
        await Run(step, instruction => { seen = instruction; return new PlanStepEvidence(instruction, "ok", "evidence"); });
        Assert.Contains("1. Turns register at intersections", seen);
        Assert.Contains("2. No wall clipping", seen);
        Assert.Contains("Do not add new acceptance requirements", seen);
        Assert.Equal(["Turns register at intersections", "No wall clipping"], step.Evidence!.AcceptanceCriteria!);
    }

    [Fact]
    public async Task EditedAcceptanceChecklist_ForcesAFreshAttemptRatherThanReusingEvidence()
    {
        var step = Plan().Steps[0];
        var executions = 0;
        await Run(step, instruction => { executions++; return new PlanStepEvidence(instruction, "ok", "evidence"); });
        Assert.Equal(1, executions);

        // Re-running an unchanged step reuses saved evidence; changing the terms must not.
        step.VerificationPending = true;
        await Run(step, instruction => { executions++; return new PlanStepEvidence(instruction, "ok", "evidence"); });
        Assert.Equal(1, executions);

        step.AcceptanceCriteria = ["A newly agreed check"];
        step.VerificationPending = true;
        await Run(step, instruction => { executions++; return new PlanStepEvidence(instruction, "ok", "evidence"); });
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task GenuineFailure_PassesDiagnosisAndTestCommandIntoTargetedRepair()
    {
        var executions = new List<string>();
        var plan = Plan();
        var executor = new Executor(instruction =>
        {
            executions.Add(instruction);
            return executions.Count == 1
                ? new PlanStepEvidence(instruction, "failed", "execute_command: node test_acceptance.mjs -> 39 passed, 2 failed",
                    ReportedFailure: "39 passed, 2 failed: DOWN turn at intersection. Rerun node test_acceptance.mjs.")
                : new PlanStepEvidence(instruction, "41 passed", "execute_command: node test_acceptance.mjs -> 41 passed, 0 failed");
        });
        await foreach (var e in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan))
            if (e.ProgressType == TaskProgressType.StepFailed) plan.Steps[0].Status = TaskStepStatus.Pending;

        Assert.Equal(2, executions.Count);
        Assert.Contains("39 passed, 2 failed", executions[1]);
        Assert.Contains("node test_acceptance.mjs", executions[1]);
        Assert.Contains("Targeted repair", executions[1]);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public async Task RepeatedFailure_PausesAtTheUnresolvedStepInsteadOfSkippingIt()
    {
        var plan = Plan();
        var executor = new Executor(instruction => new PlanStepEvidence(instruction, "failed", "evidence",
            ReportedFailure: "Still broken"));
        await foreach (var e in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan))
            if (e.ProgressType == TaskProgressType.StepFailed) plan.Steps[0].Status = TaskStepStatus.Pending;

        Assert.Equal(TaskPlanStatus.Paused, plan.Status);
        Assert.Equal(TaskStepStatus.Failed, plan.Steps[0].Status);
        Assert.Contains("Still broken", plan.ExecutionSummary);
    }

    [Fact]
    public async Task TargetedRepair_KeepsEarlierEvidenceForUnchangedFiles()
    {
        var step = Plan().Steps[0];
        var files = new Dictionary<string, string> { ["index.html"] = "html-hash", ["script.js"] = "script-hash" };
        var executions = 0;
        Task<string> Attempt() => Run(step, instruction => ++executions == 1
            ? new PlanStepEvidence(instruction, "partial", "read index.html: <canvas id=game><script src=script.js>",
                ReportedFailure: "Read the full WASD mapping", FileVersions: files)
            : new PlanStepEvidence(instruction, "done", "read script.js: s/S -> down, a/A -> left, d/D -> right",
                FileVersions: files));

        await Assert.ThrowsAsync<PlanStepReportedFailureException>(Attempt);
        await Attempt();

        Assert.Contains("<canvas id=game>", step.Evidence!.ToolEvidence);
        Assert.Contains("s/S -> down", step.Evidence.ToolEvidence);
    }

    [Fact]
    public void ChangedFiles_InvalidateHistoricalEvidence()
    {
        var previous = new PlanStepEvidence("step", "old", "old passing test", FileVersions:
            new Dictionary<string, string> { ["script.js"] = "before" });
        var current = new PlanStepEvidence("step", "new", "new failing test", FileVersions:
            new Dictionary<string, string> { ["script.js"] = "after" });
        Assert.DoesNotContain("old passing test", PlanStepRecovery.MergeEvidence(previous, current).ToolEvidence);
    }

    [Fact]
    public async Task SavedAttemptResumesFromCheckpointWithoutRepeatingImplementation()
    {
        var plan = new TaskPlan { Steps = [new TaskStep { Instruction = "task", VerificationPending = true,
            Evidence = new PlanStepEvidence("task", "Saved work", "check passed") }] };
        var state = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(PlanRunState.From(plan, 0, [], [])))!;
        var step = PlanCheckpointStore.ToPlan(state).Steps[0];

        var result = await Run(step, _ => throw new InvalidOperationException("Implementation must not repeat"));
        Assert.Equal("Saved work", result);
        Assert.False(step.VerificationPending);
    }

    [Fact]
    public void ConfigsCarryingTheRetiredStrictFlagStillLoad()
    {
        var config = JsonSerializer.Deserialize<MandoCodeConfig>("""{"strictPlanVerification":true}""");
        Assert.NotNull(config);
        Assert.False(ConfigKeySetter.TrySet(config, "strictPlanVerification", "true").Ok);
    }

    [Fact]
    public async Task UserCancellation_StopsBeforeExecuting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Plan().Steps[0],
            _ => throw new InvalidOperationException("Must not execute"), ct: cts.Token));
    }

    [Fact]
    public async Task CancellationMidExecution_DoesNotBecomeSuccessfulCompletion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanStepRecovery.RunAsync(
            new TaskStep { Instruction = "task" }, (_, ct) => Task.FromCanceled<PlanStepEvidence>(ct),
            _ => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public void FileReadMiddle_SurvivesSeveralOtherToolCalls()
    {
        var source = new string('x', 2200) + "s: 'down', a: 'left', d: 'right'" + new string('x', 2200);
        var messages = Tools("list_all_project_files", "execute_command", "open_desktop_preview").ToList();
        messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("read", "read_file",
            new Dictionary<string, object?> { ["relativePath"] = "script.js" })]));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read", source)]));
        Assert.Contains("s: 'down', a: 'left', d: 'right'", PlanToolEvidence.Capture(messages));
    }

    [Fact]
    public void FileVersions_DetectRealContentChangesAndRecaptureEarlierPaths()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "index.html"), "canvas");
            var first = PlanToolEvidence.SnapshotFileVersions([], folder, ["index.html"]);
            var same = PlanToolEvidence.SnapshotFileVersions([], folder, first.Keys);
            Assert.Equal(first["index.html"], same["index.html"]);
            File.WriteAllText(Path.Combine(folder, "index.html"), "changed");
            var changed = PlanToolEvidence.SnapshotFileVersions([], folder, first.Keys);
            Assert.NotEqual(first["index.html"], changed["index.html"]);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static IEnumerable<ChatMessage> Tools(params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            yield return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(i.ToString(), names[i],
                new Dictionary<string, object?> { ["path"] = "entity.js" })]);
            yield return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(i.ToString(), "ok")]);
        }
    }

    private sealed class Executor(Func<string, PlanStepEvidence> respond) : IPlanStepExecutor
    {
        public Task<string> ExecuteStepAsync(string instruction, List<string> previousResults, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The workflow must use the recovery-aware entry point.");
        public Task WaitForQuiescenceAsync(TimeSpan timeout) => Task.CompletedTask;
        public Task<string> ExecuteAttemptAsync(TaskStep step, List<string> previousResults, Func<string, Task> activity,
            CancellationToken cancellationToken = default) => PlanStepRecovery.RunAsync(step,
                (instruction, _) => Task.FromResult(respond(instruction)), activity, cancellationToken);
    }
}
