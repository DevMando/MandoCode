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
/// Result of computing a diff between two file versions.
/// </summary>
public class DiffResult
{
    public List<DiffLine> Lines { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
    public bool IsNewFile { get; set; }
    public int OldLineCount { get; set; }
    public int NewLineCount { get; set; }
    public int Additions => Lines.Count(l => l.LineType == DiffLineType.Added);
    public int Deletions => Lines.Count(l => l.LineType == DiffLineType.Removed);
}

/// <summary>
/// User's response to a diff approval prompt.
/// </summary>
public enum DiffApprovalResponse
{
    Approved,
    ApprovedNoAskAgain,
    Denied,
    NewInstructions
}

/// <summary>
/// Result of a diff approval prompt including optional user message.
/// </summary>
public class DiffApprovalResult
{
    public DiffApprovalResponse Response { get; set; }
    public string? UserMessage { get; set; }
}
