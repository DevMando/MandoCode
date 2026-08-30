using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Tracks token consumption across the session.
/// Thread-safe via Interlocked operations.
/// </summary>
public class TokenTrackingService
{
    private long _totalPromptTokens;
    private long _totalCompletionTokens;
    private TokenUsageInfo? _lastOperation;

    /// <summary>
    /// Event raised whenever token counts are updated.
    /// </summary>
    public event Action<TokenUsageInfo>? OnTokensUpdated;

    /// <summary>
    /// Total tokens reported by model responses in the current session.
    /// </summary>
    public long TotalSessionTokens =>
        Interlocked.Read(ref _totalPromptTokens) +
        Interlocked.Read(ref _totalCompletionTokens);

    /// <summary>
    /// Total real prompt tokens recorded from model responses.
    /// </summary>
    public long TotalPromptTokens => Interlocked.Read(ref _totalPromptTokens);

    /// <summary>
    /// Total real completion tokens recorded from model responses.
    /// </summary>
    public long TotalCompletionTokens => Interlocked.Read(ref _totalCompletionTokens);

    /// <summary>
    /// The most recent operation's token info.
    /// </summary>
    public TokenUsageInfo? LastOperation => _lastOperation;

    /// <summary>
    /// Records real token usage from a model response.
    /// </summary>
    public void RecordModelUsage(int promptTokens, int completionTokens, string label, double? generationSeconds = null)
    {
        Interlocked.Add(ref _totalPromptTokens, promptTokens);
        Interlocked.Add(ref _totalCompletionTokens, completionTokens);

        var info = new TokenUsageInfo
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            OperationLabel = label,
            GenerationSeconds = generationSeconds
        };

        _lastOperation = info;
        OnTokensUpdated?.Invoke(info);
    }

    /// <summary>
    /// Resets all counters. Called on /clear.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalPromptTokens, 0);
        Interlocked.Exchange(ref _totalCompletionTokens, 0);
        _lastOperation = null;
    }

    /// <summary>
    /// Formats a token count for compact display.
    /// Returns "847", "4.2k", "130k", "1.3M" etc.
    /// </summary>
    public static string FormatTokenCount(long count)
    {
        return count switch
        {
            < 1000 => count.ToString(),
            < 10_000 => $"{count / 1000.0:0.#}k",
            < 1_000_000 => $"{count / 1000:0}k",
            _ => $"{count / 1_000_000.0:0.#}M"
        };
    }
}
