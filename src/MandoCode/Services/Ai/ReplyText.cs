using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Reconciles reply text a host already showed early (the part streamed before a tool call) with
/// the turn's authoritative text, which <see cref="AIService.ChatStreamAsync"/> yields only once
/// the turn is over. Shared by the CLI and Desktop so both show the rest of a turn the same way.
/// </summary>
public static class ReplyText
{
    /// <summary>
    /// Removes each of <paramref name="parts"/> from <paramref name="text"/>, in order, ignoring
    /// whitespace differences (a turn's messages are joined with separators the stream never
    /// carried), and returns what is left. False when any part is missing: the final text was
    /// rewritten, e.g. by the fallback parser stripping a text-written tool call.
    /// </summary>
    public static bool TryRemoveInOrder(string text, IReadOnlyList<string> parts, out string? rest)
    {
        rest = null;
        var squeezed = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            squeezed.Append(text[i]);
            origin.Add(i);
        }
        var haystack = squeezed.ToString();

        var removed = new bool[text.Length];
        int from = 0;
        foreach (var part in parts)
        {
            var needle = string.Concat(part.Where(c => !char.IsWhiteSpace(c)));
            if (needle.Length == 0) continue;
            int at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return false;
            for (int i = origin[at]; i <= origin[at + needle.Length - 1]; i++) removed[i] = true;
            from = at + needle.Length;
        }

        var kept = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
            if (!removed[i]) kept.Append(text[i]);
        rest = kept.ToString();
        return true;
    }

    /// <summary>The part of <paramref name="text"/> not yet shown, trimmed: everything when nothing
    /// was shown early, or when the early parts can't be found in it.</summary>
    public static string Unshown(string text, IReadOnlyList<string> shown) =>
        shown.Count > 0 && TryRemoveInOrder(text, shown, out var rest) ? rest!.Trim() : text.Trim();
}
