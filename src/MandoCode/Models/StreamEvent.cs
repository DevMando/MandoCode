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
}
