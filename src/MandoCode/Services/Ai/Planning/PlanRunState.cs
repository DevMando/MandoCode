using System.Text.Json.Serialization;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Everything needed to resume a plan, in a form that survives JSON round-tripping.
/// </summary>
/// <remarks>
/// <para>
/// Held in the workflow's own shared state rather than in <see cref="PlanRunContext"/>, because the
/// context carries live delegates and a <see cref="CancellationToken"/> and can therefore never be
/// checkpointed. MAF captures shared state at each superstep boundary, so putting the run's facts
/// here is what makes resume possible at all.
/// </para>
/// <para>
/// Deliberately a snapshot of plain data — no behavior, no references to services. The live
/// <see cref="TaskPlan"/> still exists alongside it, because the current consumer contract requires
/// a mutable plan the UI can set <see cref="TaskPlan.Status"/> on. That duplication is temporary:
/// once progress becomes read-only and the legacy runner is gone, this becomes the only
/// representation.
/// </para>
/// </remarks>
public sealed record PlanRunState
{
    /// <summary>The user's request, verbatim where available — authoritative for target paths.</summary>
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = "";

    /// <summary>Every step, in order.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<PlanStepState> Steps { get; init; } = [];

    /// <summary>Zero-based index of the next step to run; equals <c>Steps.Count</c> when finished.</summary>
    [JsonPropertyName("cursor")]
    public int Cursor { get; init; }

    /// <summary>
    /// Results of completed steps, oldest first, in the form each step's context expects.
    /// </summary>
    [JsonPropertyName("previousResults")]
    public IReadOnlyList<string> PreviousResults { get; init; } = [];

    /// <summary>
    /// Files this plan has created, edited or deleted — recorded at the middleware choke point, so
    /// this is evidence a call actually succeeded rather than the model's account of it.
    /// </summary>
    [JsonPropertyName("fileOperations")]
    public IReadOnlyList<PlanFileOperation> FileOperations { get; init; } = [];

    /// <summary>Captures the current shape of a live plan.</summary>
    public static PlanRunState From(
        TaskPlan plan,
        int cursor,
        IReadOnlyList<string> previousResults,
        IReadOnlyList<(string Operation, string Path)> fileOperations) => new()
        {
            Goal = plan.OriginalRequest ?? "",
            Steps = [.. plan.Steps.Select(PlanStepState.From)],
            Cursor = cursor,
            PreviousResults = [.. previousResults],
            FileOperations = [.. fileOperations.Select(f => new PlanFileOperation(f.Operation, f.Path))],
        };
}

/// <summary>One step's durable state.</summary>
public sealed record PlanStepState
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>Short label for display; never sent to the model.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>The text actually executed. This is what a resumed run re-issues.</summary>
    [JsonPropertyName("instruction")]
    public string Instruction { get; init; } = "";

    [JsonPropertyName("status")]
    public TaskStepStatus Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public PlanStepEvidence? Evidence { get; init; }
    public bool VerificationPending { get; init; }
    public int RepairAttempts { get; init; }

    public static PlanStepState From(TaskStep step) => new()
    {
        Number = step.StepNumber,
        Description = step.Description,
        Instruction = step.Instruction,
        Status = step.Status,
        Result = step.Result,
        Error = step.ErrorMessage,
        Evidence = step.Evidence,
        VerificationPending = step.VerificationPending,
        RepairAttempts = step.RepairAttempts,
    };
}

/// <summary>A filesystem change a plan made, as observed by the middleware.</summary>
public sealed record PlanFileOperation(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("path")] string Path);
