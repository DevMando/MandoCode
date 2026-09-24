using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// How full the model's context window is, for the meter above the prompt. "Used" is the last
/// request's prompt tokens: every request resends the whole conversation, so that is what the next
/// one starts from.
/// </summary>
public static class ContextMeter
{
    public enum Level { Ok, Warm, Full }

    /// <param name="Filled">The used part of the bar, or empty when the window is unknown.</param>
    /// <param name="Empty">The unused part, kept separate so it can be drawn without the level color:
    /// in one color, shaded cells read as nearly full on bright or glowing themes.</param>
    /// <param name="Label">"18k / 32k (56%)", or "~18k tokens in context" without a window.</param>
    public sealed record Reading(string Filled, string Empty, string Label, Level Level)
    {
        public string Bar => Filled + Empty;
    }

    public const int BarCells = 20;

    /// <param name="usedTokens">The last request's prompt tokens.</param>
    /// <param name="windowTokens">The context window, or 0 when it isn't known here (cloud models
    /// manage theirs server-side).</param>
    public static Reading Read(long usedTokens, int windowTokens)
    {
        var used = TokenTrackingService.FormatTokenCount(Math.Max(0, usedTokens));
        if (windowTokens <= 0)
            return new Reading(string.Empty, string.Empty, $"~{used} tokens in context", Level.Ok);

        var fraction = Math.Clamp((double)usedTokens / windowTokens, 0, 1);
        var filled = (int)Math.Round(fraction * BarCells);
        var percent = (int)Math.Round(fraction * 100);
        var level = percent >= 85 ? Level.Full : percent >= 60 ? Level.Warm : Level.Ok;
        return new Reading(
            new string('█', filled),
            new string('░', BarCells - filled),
            $"{used} / {TokenTrackingService.FormatTokenCount(windowTokens)} ({percent}%)",
            level);
    }

    /// <summary>The window this app knows for <paramref name="modelTag"/>: the configured context
    /// length for a local model, 0 for a cloud model or when the daemon's own setting governs.</summary>
    public static int KnownWindow(string? modelTag, int configuredContextLength) =>
        MandoCodeConfig.IsCloudModel(modelTag) ? 0 : Math.Max(0, configuredContextLength);
}
