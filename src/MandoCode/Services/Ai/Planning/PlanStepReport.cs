using System.Text.RegularExpressions;

namespace MandoCode.Services;

/// <summary>
/// Parses the explicit terminal marker required from a plan-step model call. Tool execution and
/// model completion are not the same as task success: a model can successfully return prose that
/// says its verification failed. The marker turns that distinction into workflow state.
/// </summary>
public static partial class PlanStepReport
{
    public const string Contract =
        "At the very end of your final response, report the step outcome on its own line. " +
        "Use [PLAN_STEP_RESULT:SUCCESS] only when this step's instruction and verification are " +
        "actually satisfied. If anything required is missing, incorrect, or unverifiable, use " +
        "[PLAN_STEP_RESULT:FAILED] followed by a concise reason. Never call a failed verification success.";

    public static PlanStepReportResult Parse(string response)
    {
        response ??= "";
        var matches = MarkerRegex().Matches(response);
        if (matches.Count == 0)
            return new PlanStepReportResult(null, response.TrimEnd(), null);

        var marker = matches[^1];
        var succeeded = marker.Groups[1].Value.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
        var before = response[..marker.Index].TrimEnd();
        var after = response[(marker.Index + marker.Length)..].Trim();
        var display = string.IsNullOrEmpty(after)
            ? before
            : string.IsNullOrEmpty(before) ? after : before + Environment.NewLine + after;

        string? failure = null;
        if (!succeeded)
        {
            failure = string.IsNullOrWhiteSpace(after) ? LastMeaningfulLine(before) : after;
            if (string.IsNullOrWhiteSpace(failure)) failure = "The step reported that its requirements were not satisfied.";
        }

        return new PlanStepReportResult(succeeded, display, failure);
    }

    private static string? LastMeaningfulLine(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();

    [GeneratedRegex(@"\[PLAN_STEP_RESULT\s*:\s*(SUCCESS|FAILED)\]", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRegex();
}

public sealed record PlanStepReportResult(bool? Succeeded, string DisplayText, string? FailureReason);

/// <summary>A model call completed normally but explicitly reported that the step did not.</summary>
public sealed class PlanStepReportedFailureException(string message) : Exception(message);
