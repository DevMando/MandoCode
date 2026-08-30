using System.Text.Json;
using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// The durable shape of a running plan.
///
/// This has to survive JSON round-tripping and contain everything a resumed run needs, because it
/// is what MAF captures at each superstep boundary. Anything held only on PlanRunContext is lost —
/// that object carries live delegates and a cancellation token and cannot be serialized.
/// </summary>
public class PlanRunStateTests
{
    private static TaskPlan MakePlan() => new()
    {
        OriginalRequest = "build a pacman game in @Games/",
        Steps =
        [
            new TaskStep
            {
                StepNumber = 1, Description = "html", Instruction = "write index.html",
                Status = TaskStepStatus.Completed, Result = "wrote index.html",
            },
            new TaskStep
            {
                StepNumber = 2, Description = "css", Instruction = "write style.css",
                Status = TaskStepStatus.Failed, ErrorMessage = "disk full",
            },
            new TaskStep
            {
                StepNumber = 3, Description = "js", Instruction = "write game.js",
                Status = TaskStepStatus.Pending,
            },
        ],
    };

    private static PlanRunState Capture() => PlanRunState.From(
        MakePlan(),
        cursor: 2,
        previousResults: ["Step 1 (html): wrote index.html"],
        fileOperations: [("write_file", "index.html")]);

    [Fact]
    public void CapturesEverythingNeededToResume()
    {
        var state = Capture();

        Assert.Equal("build a pacman game in @Games/", state.Goal);
        Assert.Equal(3, state.Steps.Count);
        Assert.Equal(2, state.Cursor);
        Assert.Single(state.PreviousResults);
        Assert.Single(state.FileOperations);
    }

    [Fact]
    public void KeepsInstructionsVerbatim()
    {
        // A resumed run re-issues the instruction, not the short display description — losing it
        // would leave the remaining steps unrunnable.
        var state = Capture();
        Assert.Equal("write game.js", state.Steps[2].Instruction);
    }

    [Fact]
    public void PreservesPerStepOutcomes()
    {
        // Resume must be able to tell finished work from work that never ran, or it redoes writes
        // that already succeeded.
        var state = Capture();

        Assert.Equal(TaskStepStatus.Completed, state.Steps[0].Status);
        Assert.Equal("wrote index.html", state.Steps[0].Result);
        Assert.Equal(TaskStepStatus.Failed, state.Steps[1].Status);
        Assert.Equal("disk full", state.Steps[1].Error);
        Assert.Equal(TaskStepStatus.Pending, state.Steps[2].Status);
    }

    [Fact]
    public void RecordsFilesAlreadyWritten()
    {
        // Evidence from the middleware choke point, not the model's account of what it did.
        var op = Capture().FileOperations.Single();
        Assert.Equal("write_file", op.Operation);
        Assert.Equal("index.html", op.Path);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        // The whole point: MAF serializes shared state into the checkpoint.
        var json = JsonSerializer.Serialize(Capture());
        var back = JsonSerializer.Deserialize<PlanRunState>(json)!;

        Assert.Equal("build a pacman game in @Games/", back.Goal);
        Assert.Equal(2, back.Cursor);
        Assert.Equal(3, back.Steps.Count);
        Assert.Equal("write game.js", back.Steps[2].Instruction);
        Assert.Equal(TaskStepStatus.Completed, back.Steps[0].Status);
        Assert.Equal("index.html", back.FileOperations.Single().Path);
    }

    [Fact]
    public void CarriesNoLiveReferences()
    {
        // A guard against the failure this type exists to prevent: if someone adds a property
        // holding a delegate, service or token, checkpointing silently stops working.
        foreach (var prop in typeof(PlanRunState).GetProperties())
        {
            var t = prop.PropertyType;
            Assert.False(typeof(Delegate).IsAssignableFrom(t), $"{prop.Name} is a delegate");
            Assert.False(t == typeof(CancellationToken), $"{prop.Name} is a CancellationToken");
            Assert.NotEqual(typeof(TaskPlan), t);
        }
    }
}
