using Xunit;
using Microsoft.SemanticKernel;
using ArdinCode.Models;
using ArdinCode.Plugins;
using ArdinCode.Services;

namespace ArdinCode.Tests;

/// <summary>
/// Regression tests for the post-plan mutation gate and the plan manifest.
/// Observed live: after a plan finished ("4 of 4 steps completed"), the outer model —
/// which never sees the steps run, since each executes in its own chat history —
/// treated the summary as "not started yet", re-created the project folder, and
/// overwrote the finished build with a fresh skeleton, all auto-approved. The manifest
/// gives the model evidence of the work; the gate mechanically refuses filesystem
/// mutations for the rest of the turn even if the model ignores it.
/// </summary>
public class PostPlanMutationGateTests
{
    private static (Kernel Kernel, FunctionInvocationFilter Filter) BuildKernel(
        Delegate? method = null,
        string functionName = "test_func")
    {
        var filter = new FunctionInvocationFilter(5);
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromFunctions("TestPlugin", new[]
        {
            KernelFunctionFactory.CreateFromMethod(method ?? (() => "ok"), functionName)
        });
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(filter);
        return (kernel, filter);
    }

    [Fact]
    public async Task CompletedPlanScope_RefusesMutatingCall_WithoutInvokingIt()
    {
        var invoked = false;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => { invoked = true; return "written"; },
            functionName: "write_file");

        using var scope = filter.BeginScope();
        scope.MarkPlanWorkCompleted();

        var result = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"],
            new KernelArguments { ["relativePath"] = "Test/index.html", ["content"] = "<html>" });

        Assert.False(invoked);
        Assert.Contains("already completed", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("edit_file")]
    [InlineData("delete_file")]
    [InlineData("delete_folder")]
    [InlineData("create_folder")]
    public async Task CompletedPlanScope_RefusesAllMutatingFunctions(string functionName)
    {
        var invoked = false;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath) => { invoked = true; return "ok"; },
            functionName: functionName);

        using var scope = filter.BeginScope();
        scope.MarkPlanWorkCompleted();

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"][functionName],
            new KernelArguments { ["relativePath"] = "Test" });

        Assert.False(invoked);
    }

    [Fact]
    public async Task CompletedPlanScope_StillAllowsReads()
    {
        var invoked = false;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath) => { invoked = true; return "file contents"; },
            functionName: "read_file_contents");

        using var scope = filter.BeginScope();
        scope.MarkPlanWorkCompleted();

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["read_file_contents"],
            new KernelArguments { ["relativePath"] = "Test/index.html" });

        Assert.True(invoked);
    }

    [Fact]
    public async Task FreshScope_AllowsMutationsAgain()
    {
        // The gate is turn-scoped: the next user message gets a new scope and the
        // model must be free to apply follow-up changes the user asks for.
        var invoked = false;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => { invoked = true; return "written"; },
            functionName: "write_file");

        using (var planTurn = filter.BeginScope())
        {
            planTurn.MarkPlanWorkCompleted();
        }

        using var nextTurn = filter.BeginScope();
        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"],
            new KernelArguments { ["relativePath"] = "Test/index.html", ["content"] = "<html>" });

        Assert.True(invoked);
    }

    [Fact]
    public async Task RejectedPlan_DoesNotEngageGate()
    {
        // PlanAlreadyProcessed alone (set for rejected plans too) must NOT gate
        // mutations — after a rejection the model does the work directly.
        var invoked = false;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => { invoked = true; return "written"; },
            functionName: "write_file");

        using var scope = filter.BeginScope();
        scope.MarkPlanProcessed();

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"],
            new KernelArguments { ["relativePath"] = "Test/index.html", ["content"] = "<html>" });

        Assert.True(invoked);
    }

    // ---- PlanHandoff manifest ----

    private static PlanStepProposal[] TwoStepProposal() => new[]
    {
        new PlanStepProposal("Create project structure", "Create the folders and files"),
        new PlanStepProposal("Build the game", "Write the game code")
    };

    [Fact]
    public async Task ExecutedPlan_ReturnsManifest_WithStepsFilesAndStopDirective()
    {
        var handoff = new PlanHandoff();
        handoff.OnPlanRequested = (plan, ct) =>
        {
            // Simulate execution: filter records ops while _isExecuting is true.
            handoff.RecordFileOperation("create_folder", "Test");
            handoff.RecordFileOperation("write_file", "Test/index.html");
            handoff.RecordFileOperation("edit_file", "Test/index.html");

            plan.Steps[0].Status = TaskStepStatus.Completed;
            plan.Steps[0].Result = "Created Test/ with the project scaffold.";
            plan.Steps[1].Status = TaskStepStatus.Completed;
            plan.Steps[1].Result = "Implemented the full game.";
            plan.Status = TaskPlanStatus.Completed;
            return Task.FromResult("raw UI summary");
        };

        var result = await handoff.ProcessAsync("create a game", TwoStepProposal());

        Assert.True(handoff.LastPlanExecutedWork);
        Assert.Contains("2 of 2 steps completed", result);
        Assert.Contains("Create project structure", result);
        Assert.Contains("Test/index.html (write_file, edit_file)", result);
        Assert.Contains("ALREADY DONE", result);
        Assert.DoesNotContain("raw UI summary", result);
    }

    [Fact]
    public async Task RejectedPlan_PassesSummaryThrough_AndDoesNotArmGate()
    {
        var handoff = new PlanHandoff();
        handoff.OnPlanRequested = (plan, ct) =>
            Task.FromResult("User rejected the proposed plan.");

        var result = await handoff.ProcessAsync("create a game", TwoStepProposal());

        Assert.False(handoff.LastPlanExecutedWork);
        Assert.Equal("User rejected the proposed plan.", result);
    }

    [Fact]
    public async Task PartiallyCompletedPlan_ManifestShowsFailedStep_AndArmsGate()
    {
        var handoff = new PlanHandoff();
        handoff.OnPlanRequested = (plan, ct) =>
        {
            handoff.RecordFileOperation("write_file", "Test/index.html");
            plan.Steps[0].Status = TaskStepStatus.Completed;
            plan.Steps[0].Result = "Scaffold done.";
            plan.Steps[1].Status = TaskStepStatus.Failed;
            plan.Steps[1].ErrorMessage = "Connection to Ollama failed.";
            return Task.FromResult("raw UI summary");
        };

        var result = await handoff.ProcessAsync("create a game", TwoStepProposal());

        // Step 1's work exists on disk — the gate must protect it even though
        // the plan didn't fully finish.
        Assert.True(handoff.LastPlanExecutedWork);
        Assert.Contains("1 of 2 steps completed", result);
        Assert.Contains("[FAILED] Step 2", result);
        Assert.Contains("Connection to Ollama failed.", result);
    }

    [Fact]
    public void RecordFileOperation_OutsideExecution_IsIgnored()
    {
        var handoff = new PlanHandoff();
        handoff.RecordFileOperation("write_file", "stray.txt"); // not executing — dropped

        var plan = new TaskPlan
        {
            OriginalRequest = "goal",
            Steps = new List<TaskStep>
            {
                new() { StepNumber = 1, Description = "step", Status = TaskStepStatus.Completed }
            }
        };

        var manifest = PlanHandoff.BuildManifest(plan, Array.Empty<(string, string)>());
        Assert.DoesNotContain("stray.txt", manifest);
    }

    [Fact]
    public void BuildManifest_CapsLongStepResults()
    {
        var plan = new TaskPlan
        {
            OriginalRequest = "goal",
            Steps = new List<TaskStep>
            {
                new()
                {
                    StepNumber = 1,
                    Description = "big step",
                    Status = TaskStepStatus.Completed,
                    Result = new string('x', 5000)
                }
            }
        };

        var manifest = PlanHandoff.BuildManifest(plan, Array.Empty<(string, string)>());

        Assert.Contains("…", manifest);
        // Whole manifest stays bounded: capped result + fixed scaffolding.
        Assert.True(manifest.Length < 1200, $"Manifest too long: {manifest.Length} chars");
    }
}
