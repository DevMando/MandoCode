using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PlanVerificationRecoveryTests
{
    private static TaskPlan Plan() => new() { OriginalRequest = "Build movement", Steps =
        [new TaskStep { StepNumber = 1, Instruction = "Implement intersection turns", Description = "Movement" }] };

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));
    private static ChatResponse Verdict(bool success, string reason) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent("verdict", "report_plan_step_outcome", new Dictionary<string, object?>
            { ["success"] = success, ["reason"] = reason })]));

    private sealed class Client(Func<int, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => respond(++Calls, options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Executor(IChatClient client) : IPlanStepExecutor
    {
        public List<string> Executions { get; } = [];
        public Task<string> ExecuteStepAsync(string instruction, List<string> previousResults, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The workflow must use the recovery-aware entry point.");
        public Task WaitForQuiescenceAsync(TimeSpan timeout) => Task.CompletedTask;
        public Task<string> ExecuteAttemptAsync(TaskStep step, List<string> previousResults, Func<string, Task> activity,
            CancellationToken cancellationToken = default) => PlanStepRecovery.RunAsync(step,
                (instruction, ct) =>
                {
                    Executions.Add(instruction);
                    return Task.FromResult(new PlanStepEvidence(instruction, "Movement implemented",
                        "execute_command: node test_acceptance.mjs -> 41 passed, 0 failed"));
                },
                (evidence, ct) => PlanStepVerifier.VerifyAsync(client, evidence, TimeSpan.FromSeconds(1), 1024, activity, ct),
                activity, cancellationToken);
    }

    [Fact]
    public async Task MalformedVerifierResponse_RetriesVerification_ExecutesExactlyOnce()
    {
        using var client = new Client((n, _, _) => Task.FromResult(n == 1 ? Text("Looks fine") : Verdict(true, "41 tests passed")));
        var executor = new Executor(client);
        var plan = Plan();
        await foreach (var _ in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan)) { }
        Assert.Single(executor.Executions);
        Assert.Equal(2, client.Calls);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public async Task ToolChoiceFailures_FallBackToSchemaWithoutExecutingAgain()
    {
        using var client = new Client((n, options, _) =>
        {
            if (n < 3) return Task.FromResult(Text("No tool call"));
            Assert.Null(options!.Tools);
            Assert.NotNull(options.ResponseFormat);
            return Task.FromResult(Text("{\"success\":true,\"reason\":\"41 checks passed\"}"));
        });
        var executor = new Executor(client);
        var plan = Plan();
        await foreach (var _ in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan)) { }
        Assert.Single(executor.Executions);
        Assert.Equal(3, client.Calls);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public async Task UnavailableVerdict_PausesAndResumesVerificationFromCheckpoint()
    {
        using var broken = new Client((_, _, _) => Task.FromResult(Text("{\"reason\":\"missing success field\"}")));
        var executor = new Executor(broken);
        var plan = Plan();
        PlanRunState? saved = null;
        var events = new List<TaskProgressType>();
        await foreach (var e in new WorkflowPlanRunner(executor, onStateSaved: state => saved = state).ExecutePlanAsync(plan))
            events.Add(e.ProgressType);
        Assert.Equal(TaskPlanStatus.Paused, plan.Status);
        Assert.Contains(TaskProgressType.StepVerificationUnavailable, events);
        Assert.DoesNotContain(TaskProgressType.StepFailed, events);
        Assert.True(saved!.Steps[0].VerificationPending);
        Assert.Single(executor.Executions);

        var restored = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(saved))!;
        using var working = new Client((_, _, _) => Task.FromResult(Verdict(true, "Checks passed")));
        var resumedExecutor = new Executor(working);
        var resumedPlan = PlanCheckpointStore.ToPlan(restored);
        await foreach (var _ in new WorkflowPlanRunner(resumedExecutor).ResumeAsync(resumedPlan, restored)) { }
        Assert.Empty(resumedExecutor.Executions);
        Assert.Equal(TaskPlanStatus.Completed, resumedPlan.Status);
    }

    [Fact]
    public async Task ManualRetryVerification_DoesNotConsumeRepairAttempts()
    {
        using var client = new Client((n, _, _) => Task.FromResult(n <= 3 ? Text("malformed") : Verdict(true, "Passed")));
        var executor = new Executor(client);
        var plan = Plan();
        await foreach (var e in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan))
            if (e.ProgressType == TaskProgressType.StepVerificationUnavailable) plan.Steps[0].Status = TaskStepStatus.Pending;
        Assert.Single(executor.Executions);
        Assert.Equal(0, plan.Steps[0].RepairAttempts);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public async Task GenuineFailure_PassesDiagnosisAndTestCommandIntoTargetedRepair()
    {
        using var client = new Client((n, _, _) => Task.FromResult(n == 1
            ? Verdict(false, "39 passed, 2 failed: DOWN turn at intersection. Rerun node test_acceptance.mjs.")
            : Verdict(true, "41 passed")));
        var executor = new Executor(client);
        var plan = Plan();
        await foreach (var e in new WorkflowPlanRunner(executor).ExecutePlanAsync(plan))
            if (e.ProgressType == TaskProgressType.StepFailed) plan.Steps[0].Status = TaskStepStatus.Pending;
        Assert.Equal(2, executor.Executions.Count);
        Assert.Contains("39 passed, 2 failed", executor.Executions[1]);
        Assert.Contains("node test_acceptance.mjs", executor.Executions[1]);
        Assert.Contains("Targeted repair", executor.Executions[1]);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
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

    [Fact]
    public void CodeEditAfterTest_InvalidatesPreviousChecks()
    {
        Assert.NotNull(PlanToolEvidence.AssessFreshness(Tools("execute_command", "edit_file")));
        Assert.NotNull(PlanToolEvidence.AssessFreshness(Tools("execute_command", "edit_file", "read_file")));
        Assert.Null(PlanToolEvidence.AssessFreshness(Tools("edit_file", "execute_command")));
    }

    [Fact]
    public async Task StaleChecks_CannotBeOverriddenByPassingModelVerdict()
    {
        var step = Plan().Steps[0];
        var verdictCalls = 0;
        var failure = await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "SUCCESS", "test then edit", "Rerun tests after entity.js edit")),
            (_, _) => { verdictCalls++; return Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Passed, "Looks good")); },
            _ => Task.CompletedTask));
        Assert.Equal(0, verdictCalls);
        Assert.Contains("Rerun tests", failure.Message);
        Assert.False(step.VerificationPending);
    }

    [Fact]
    public async Task VerificationTimeouts_AreUnavailableAndBounded()
    {
        using var client = new Client(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Text(""); });
        var result = await PlanStepVerifier.VerifyAsync(client, new("step", "claim", "evidence"),
            TimeSpan.FromMilliseconds(20), 1024).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlanVerificationStatus.Unavailable, result.Status);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task UserCancellation_DoesNotRetryVerification()
    {
        using var cts = new CancellationTokenSource();
        using var client = new Client((_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Text("")); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanStepVerifier.VerifyAsync(client,
            new("step", "claim", "evidence"), TimeSpan.FromSeconds(1), 1024, ct: cts.Token));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task TargetedReadRepair_KeepsEarlierHtmlEvidenceForVerifier()
    {
        var step = Plan().Steps[0];
        var files = new Dictionary<string, string> { ["index.html"] = "html-hash", ["script.js"] = "script-hash" };
        var executions = 0;
        var verifications = 0;
        Task<string> Attempt() => PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "done", ++executions == 1
                ? "read index.html: <canvas id=game><script src=script.js>; script.js: w/W -> up"
                : "read script.js: s/S -> down, a/A -> left, d/D -> right; node --check exit 0", FileVersions: files)),
            (evidence, _) =>
            {
                if (++verifications == 1) return Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Failed, "Read full WASD mapping"));
                Assert.Contains("<canvas id=game>", evidence.ToolEvidence);
                Assert.Contains("s/S -> down", evidence.ToolEvidence);
                return Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Passed, "HTML and full KEYMAP substantiated"));
            }, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(Attempt);
        await Attempt();
        Assert.Equal(2, verifications);
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
}
