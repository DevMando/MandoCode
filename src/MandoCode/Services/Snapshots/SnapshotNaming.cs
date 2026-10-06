namespace MandoCode.Services;

/// <summary>
/// The deterministic half of snapshot auto-naming: turning a model's raw title output into a bare
/// label, and guaranteeing a title doesn't collide with ones already in use. Pure string logic, no
/// LLM or UI — the call that asks a model for a title lives in <see cref="SnapshotEnhancer"/>; an
/// LLM can't be trusted to keep names clean or unique, so that's enforced here.
/// </summary>
public static class SnapshotNaming
{
    /// <summary>Tidies a model-produced title into a bare label: first line only, surrounding quotes
    /// and trailing punctuation stripped, whitespace collapsed, length-capped. Null if nothing usable
    /// survives (the caller then falls back to a non-AI title).</summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var name = raw.Trim();
        // Models sometimes preface ("Title: X") or add a line of reasoning — keep the first line only.
        var newline = name.IndexOfAny(new[] { '\r', '\n' });
        if (newline >= 0) name = name[..newline].Trim();

        name = name.Trim('"', '\'', '`', ' ', '.', ':', '-', '*');
        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (name.Length == 0) return null;
        if (name.Length > 60) name = name[..60].TrimEnd() + "…";
        return name;
    }

    /// <summary>Returns <paramref name="name"/> if no existing title matches it (case-insensitive),
    /// else the first free "name (2)", "name (3)", … — so an auto-generated title can never collide
    /// with one already on a card.</summary>
    public static string MakeUnique(string name, IReadOnlyCollection<string> taken)
    {
        bool Clashes(string s) => taken.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase));
        if (!Clashes(name)) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!Clashes(candidate)) return candidate;
        }
    }
}
