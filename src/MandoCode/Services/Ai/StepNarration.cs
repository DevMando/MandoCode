using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Turns a model's streamed output into a single short line of "what it is doing right now",
/// suitable for a spinner label.
/// </summary>
/// <remarks>
/// <para>
/// A plan step's text is only rendered once the step finishes — streaming exists for the stall
/// watchdog's heartbeat, not for display. That leaves a long step looking silent: a spinner and
/// nothing else for minutes, which reads as a hang. Observed live, a step sat at "Working…" for
/// four minutes while the model was narrating the whole time.
/// </para>
/// <para>
/// This deliberately shows only the latest line rather than streaming everything to the console.
/// The full response still renders as markdown when the step completes, so printing it live too
/// would duplicate it — and a partially-arrived markdown document cannot be rendered sensibly.
/// </para>
/// </remarks>
public sealed class StepNarration
{
    private readonly StringBuilder _currentLine = new();
    private string _lastCompleteLine = "";

    /// <summary>Feeds one streamed chunk in. Chunks may split mid-line or mid-word.</summary>
    public void Append(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                var line = _currentLine.ToString().Trim();
                if (line.Length > 0) _lastCompleteLine = line;
                _currentLine.Clear();
            }
            else if (c != '\r')
            {
                _currentLine.Append(c);
            }
        }
    }

    /// <summary>
    /// The line to display, or <c>null</c> when nothing worth showing has arrived yet.
    /// Prefers the line currently being written; falls back to the last completed one so the
    /// display doesn't blank out between lines.
    /// </summary>
    public string? Latest
    {
        get
        {
            var partial = _currentLine.ToString().Trim();
            var line = partial.Length > 0 ? partial : _lastCompleteLine;
            return line.Length > 0 ? line : null;
        }
    }

    /// <summary>
    /// <see cref="Latest"/> shortened to <paramref name="maxLength"/>, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A spinner label that wraps corrupts the line the spinner is redrawing, so the caller's
    /// available width is a hard limit rather than a preference.
    /// </remarks>
    public string? Shortened(int maxLength)
    {
        var line = Latest;
        if (line == null) return null;
        if (maxLength <= 1) return null;
        if (line.Length <= maxLength) return line;

        return string.Concat(line.AsSpan(0, maxLength - 1), "…");
    }
}
