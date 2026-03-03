namespace MandoCode.Models;

/// <summary>
/// Represents token usage data for a single operation.
/// </summary>
public record TokenUsageInfo
{
    /// <summary>
    /// Number of input/prompt tokens consumed.
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// Number of output/completion tokens generated.
    /// </summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// Total tokens (prompt + completion).
    /// </summary>
    public int TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// Label describing the operation (e.g. "Chat", "Plan", "Read src/Program.cs").
    /// </summary>
    public string OperationLabel { get; init; } = "";

    /// <summary>
    /// True when the token count is an estimate (e.g. chars/4 heuristic) rather than a real count.
    /// </summary>
    public bool IsEstimate { get; init; }

    /// <summary>
    /// Time in seconds the model spent generating output tokens (from Ollama EvalDuration).
    /// Null when unavailable or for estimated operations.
    /// </summary>
    public double? GenerationSeconds { get; init; }

    /// <summary>
    /// Tokens per second for output generation, or null if timing unavailable.
    /// </summary>
    public double? TokensPerSecond =>
        GenerationSeconds is > 0 && CompletionTokens > 0
            ? CompletionTokens / GenerationSeconds.Value
            : null;
}
