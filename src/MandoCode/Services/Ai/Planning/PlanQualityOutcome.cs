using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using MandoCode.Models;

namespace MandoCode.Services;

public static class PlanQualityOutcome
{
    public static IReadOnlyDictionary<string, int> CaptureTests(IEnumerable<ChatMessage> messages)
    {
        var history = messages.ToList();
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.Last());
        var exits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in history.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            if (!calls.TryGetValue(result.CallId, out var call) || call.Name != "execute_command" ||
                call.Arguments == null || !call.Arguments.TryGetValue("command", out var value)) continue;
            var command = value?.ToString()?.Trim() ?? "";
            // Identify executable checks, never guess from prose such as '0 failed'.
            if (!Regex.IsMatch(command, @"(?:^|&&\s*)(?:node\s+(?:--check\s+\S+|\S*(?:test|verify|check)\S*\.(?:js|mjs|cjs))|dotnet\s+test\b|(?:npm|pnpm|yarn)\s+(?:run\s+)?test\b|(?:python\s+-m\s+)?pytest\b)", RegexOptions.IgnoreCase)) continue;
            var match = Regex.Match(result.Result?.ToString() ?? "", @"\AExit code:\s*(-?\d+)\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var code)) exits[command] = code;
        }
        return exits;
    }

    public static PlanStepEvidence Merge(PlanStepEvidence? previous, PlanStepEvidence current)
    {
        if (previous?.Instruction != current.Instruction) return current;
        var exits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in previous.TestExitCodes ?? new Dictionary<string, int>())
            if (entry.Value != 0) exits[entry.Key] = entry.Value;
        foreach (var entry in current.TestExitCodes ?? new Dictionary<string, int>()) exits[entry.Key] = entry.Value;
        return current with { TestExitCodes = exits };
    }

    public static string? Failure(PlanStepEvidence evidence)
    {
        var failed = evidence.TestExitCodes?.Where(e => e.Value != 0).ToArray() ?? [];
        if (failed.Length > 0) return "Final quality checks remain failed. Repair and rerun the same checks: " +
            string.Join("; ", failed.Select(e => $"{e.Key} (exit {e.Value})"));
        if (evidence.ReportedFailure != null) return evidence.ReportedFailure;
        return evidence.ExplicitSuccess == true ? null :
            "The final quality phase did not report an explicit successful outcome. Finish the checks or report the blocker; work is saved.";
    }
}
