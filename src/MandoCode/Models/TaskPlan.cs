namespace MandoCode.Models;

/// <summary>
/// Represents a task plan consisting of multiple steps to complete a complex request.
/// </summary>
public class TaskPlan
{
    /// <summary>
    /// The original user request that generated this plan.
    /// </summary>
    public string OriginalRequest { get; set; } = string.Empty;

    /// <summary>
    /// The list of steps to complete the task.
    /// </summary>
    public List<TaskStep> Steps { get; set; } = new();

    /// <summary>
    /// Current status of the plan execution.
    /// </summary>
    public TaskPlanStatus Status { get; set; } = TaskPlanStatus.Pending;

    /// <summary>
    /// Summary of execution results.
    /// </summary>
    public string? ExecutionSummary { get; set; }

    /// <summary>
    /// Gets the current step being executed (if any).
    /// </summary>
    public TaskStep? CurrentStep => Steps.FirstOrDefault(s => s.Status == TaskStepStatus.InProgress);

    /// <summary>
    /// Gets the number of completed steps.
    /// </summary>
    public int CompletedStepsCount => Steps.Count(s => s.Status == TaskStepStatus.Completed);

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage => Steps.Count > 0
        ? (int)((double)CompletedStepsCount / Steps.Count * 100)
        : 0;
}

/// <summary>
/// Represents a single step in a task plan.
/// </summary>
public class TaskStep
{
    /// <summary>
    /// The step number (1-based).
    /// </summary>
    public int StepNumber { get; set; }

    /// <summary>
    /// Short description of the step for display.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Detailed instruction for the AI to execute this step.
    /// </summary>
    public string Instruction { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the step.
    /// </summary>
    public TaskStepStatus Status { get; set; } = TaskStepStatus.Pending;

    /// <summary>
    /// Result or output from executing this step.
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// Error message if the step failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Status of a task plan.
/// </summary>
public enum TaskPlanStatus
{
    /// <summary>Plan created but not yet started.</summary>
    Pending,

    /// <summary>Plan is currently being executed.</summary>
    InProgress,

    /// <summary>All steps completed successfully.</summary>
    Completed,

    /// <summary>User cancelled the plan.</summary>
    Cancelled,

    /// <summary>Plan execution failed.</summary>
    Failed
}

/// <summary>
/// Status of a task step.
/// </summary>
public enum TaskStepStatus
{
    /// <summary>Step not yet started.</summary>
    Pending,

    /// <summary>Step is currently executing.</summary>
    InProgress,

    /// <summary>Step completed successfully.</summary>
    Completed,

    /// <summary>Step was skipped by user.</summary>
    Skipped,

    /// <summary>Step execution failed.</summary>
    Failed
}
