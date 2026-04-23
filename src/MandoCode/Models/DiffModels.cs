namespace MandoCode.Models;

/// <summary>
/// Type of change for a diff line.
/// </summary>
public enum DiffLineType
{
    Added,
    Removed,
    Unchanged
}

/// <summary>
/// Represents a single line in a diff output.
/// </summary>
public class DiffLine
{
    public DiffLineType LineType { get; set; }
    public string Content { get; set; } = string.Empty;
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
}

/// <summary>
/// User's response to a diff approval prompt.
/// </summary>
public enum DiffApprovalResponse
{
    Approved,
    ApprovedNoAskAgain,
    Denied,
    NewInstructions,
    /// <summary>
    /// User wants to stop the entire currently-running plan, not just this single tool call.
    /// Only offered as a choice when <c>PlanHandoff.IsExecuting</c> is true. Propagates up
    /// through the invocation scope and terminates <c>ExecutePlanAsync</c>.
    /// </summary>
    CancelPlan
}

/// <summary>
/// Result of a diff approval prompt including optional user message.
/// </summary>
public class DiffApprovalResult
{
    public DiffApprovalResponse Response { get; set; }
    public string? UserMessage { get; set; }
}
