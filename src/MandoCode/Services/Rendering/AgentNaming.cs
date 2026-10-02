namespace MandoCode.Services;

/// <summary>
/// Default agent labels — "Agent 1", "Agent 2", … — reusing the lowest free number so closing
/// "Agent 2" then opening a new tab gives "Agent 2" again rather than an ever-climbing count.
/// Pure so it can be tested without a live <see cref="SessionManager"/> (which needs the whole
/// app object graph). User-renamed titles are simply names that happen to be taken.
/// </summary>
public static class AgentNaming
{
    public static string NextFreeName(IEnumerable<string?> existingTitles)
    {
        var taken = new HashSet<string>(
            existingTitles.Where(t => !string.IsNullOrEmpty(t))!, StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = $"Agent {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
