using MandoCode.Models;

namespace MandoCode.Services;

public static class PlanAcceptance
{
    public static List<string> Normalize(IEnumerable<string>? criteria, string instruction)
    {
        var items = (criteria ?? []).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        // Older plans retain their original requirement; never invent new obligations on resume.
        return items.Count == 0 ? [instruction] : items;
    }

    public static string Describe(TaskStep step) => "Acceptance checklist (agreed when this step was proposed):\n" +
        string.Join("\n", Normalize(step.AcceptanceCriteria, step.Instruction).Select((c, i) => $"{i + 1}. {c}"));
}
