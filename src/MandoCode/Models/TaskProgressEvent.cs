namespace MandoCode.Models;

/// <summary>
/// Represents a progress event during task plan execution.
/// </summary>
public class TaskProgressEvent : StreamEvent
{
    /// <summary>
    /// Type of progress event.
    /// </summary>
    public TaskProgressType ProgressType { get; set; }

    /// <summary>
    /// Current step number (1-based).
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// Total number of steps in the plan.
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// Description of the current step.
    /// </summary>
    public string StepDescription { get; set; } = string.Empty;

    /// <summary>
    /// Additional message or content for this event.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The task plan this event is related to.
    /// </summary>
    public TaskPlan? Plan { get; set; }

    /// <summary>
    /// Creates a new plan created event.
    /// </summary>
    public static TaskProgressEvent PlanCreated(TaskPlan plan) => new()
    {
        ProgressType = TaskProgressType.PlanCreated,
        TotalSteps = plan.Steps.Count,
        Plan = plan,
        Message = $"Created plan with {plan.Steps.Count} steps"
    };

    /// <summary>
    /// Creates a new step started event.
    /// </summary>
    public static TaskProgressEvent StepStarted(TaskPlan plan, TaskStep step) => new()
    {
        ProgressType = TaskProgressType.StepStarted,
        CurrentStep = step.StepNumber,
        TotalSteps = plan.Steps.Count,
        StepDescription = step.Description,
        Plan = plan
    };

    /// <summary>
    /// Creates a new step completed event.
    /// </summary>
    public static TaskProgressEvent StepCompleted(TaskPlan plan, TaskStep step, string? result = null) => new()
    {
        ProgressType = TaskProgressType.StepCompleted,
        CurrentStep = step.StepNumber,
        TotalSteps = plan.Steps.Count,
        StepDescription = step.Description,
        Message = result,
        Plan = plan
    };

    /// <summary>
    /// Creates a new step failed event.
    /// </summary>
    public static TaskProgressEvent StepFailed(TaskPlan plan, TaskStep step, string errorMessage) => new()
    {
        ProgressType = TaskProgressType.StepFailed,
        CurrentStep = step.StepNumber,
        TotalSteps = plan.Steps.Count,
        StepDescription = step.Description,
        Message = errorMessage,
        Plan = plan
    };

    /// <summary>
    /// Creates a new plan completed event.
    /// </summary>
    public static TaskProgressEvent PlanCompleted(TaskPlan plan) => new()
    {
        ProgressType = TaskProgressType.PlanCompleted,
        TotalSteps = plan.Steps.Count,
        CurrentStep = plan.Steps.Count,
        Plan = plan,
        Message = "All steps completed successfully"
    };

    /// <summary>
    /// Creates a new plan cancelled event.
    /// </summary>
    public static TaskProgressEvent PlanCancelled(TaskPlan plan) => new()
    {
        ProgressType = TaskProgressType.PlanCancelled,
        TotalSteps = plan.Steps.Count,
        Plan = plan,
        Message = "Plan cancelled by user"
    };
}

/// <summary>
/// Types of task progress events.
/// </summary>
public enum TaskProgressType
{
    /// <summary>A plan has been created and is awaiting approval.</summary>
    PlanCreated,

    /// <summary>A step has started execution.</summary>
    StepStarted,

    /// <summary>A step has completed successfully.</summary>
    StepCompleted,

    /// <summary>A step has failed.</summary>
    StepFailed,

    /// <summary>All steps in the plan have been completed.</summary>
    PlanCompleted,

    /// <summary>The plan has been cancelled by the user.</summary>
    PlanCancelled
}
