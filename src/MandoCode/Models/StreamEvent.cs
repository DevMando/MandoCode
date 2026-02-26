namespace MandoCode.Models;

/// <summary>
/// Represents different types of events during AI response streaming.
/// </summary>
public abstract class StreamEvent
{
    public string Type => GetType().Name;
}

/// <summary>
/// Text content chunk from the AI response.
/// </summary>
public class TextChunk : StreamEvent
{
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Function call being executed by the AI.
/// </summary>
public class FunctionCall : StreamEvent
{
    public string FunctionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

/// <summary>
/// Result of a function call execution.
/// </summary>
public class FunctionExecutionResult : StreamEvent
{
    public string FunctionName { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public OperationDisplayEvent? OperationDisplay { get; set; }
}

/// <summary>
/// Rich operation display event for Claude-Code-style output.
/// </summary>
public class OperationDisplayEvent : StreamEvent
{
    /// <summary>
    /// Type of operation: Write, Update, Read, Delete, CreateFolder, Search, List, Glob
    /// </summary>
    public string OperationType { get; set; } = string.Empty;

    /// <summary>
    /// File or folder path involved in the operation.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Total number of lines in the written/read/deleted file.
    /// </summary>
    public int LineCount { get; set; }

    /// <summary>
    /// First N lines of content for preview (new file writes).
    /// </summary>
    public string? ContentPreview { get; set; }

    /// <summary>
    /// Number of lines not shown in the preview.
    /// </summary>
    public int RemainingLines { get; set; }

    /// <summary>
    /// Collapsed inline diff lines for update operations.
    /// </summary>
    public List<DiffLine>? InlineDiff { get; set; }

    /// <summary>
    /// Number of lines added (for updates).
    /// </summary>
    public int Additions { get; set; }

    /// <summary>
    /// Number of lines removed (for updates).
    /// </summary>
    public int Deletions { get; set; }

    /// <summary>
    /// Whether the file was newly created.
    /// </summary>
    public bool IsNewFile { get; set; }

    /// <summary>
    /// Whether the diff approval was already shown to the user.
    /// When true, the renderer shows a compact display without preview/diff.
    /// </summary>
    public bool ApprovalWasShown { get; set; }
}
