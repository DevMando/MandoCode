using System.Text.RegularExpressions;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>Adds a normal, checkpointed executor step before the plan is shown for approval.</summary>
public static class PlanFinalQuality
{
    private const string Start = "Run the final quality pass on the completed deliverable.";

    public static bool IsQualityStep(TaskStep step) => step.IsFinalQualityPhase ??
        step.Instruction.StartsWith(Start, StringComparison.Ordinal);

    public static TaskPlan ForRevision(TaskPlan current, TaskPlan candidate) =>
        current.Steps.Any(IsQualityStep) ? Ensure(candidate) : candidate;

    public static TaskPlan Ensure(TaskPlan plan)
    {
        if (plan.Steps.Count == 0 || plan.Steps.Any(IsQualityStep)) return plan;
        var work = string.Join("\n", plan.Steps
            .Where(s => !Regex.IsMatch(s.Instruction, @"\bread[- ]only\b", RegexOptions.IgnoreCase))
            .Select(s => Regex.Replace(s.Description + " " + s.Instruction,
                @"\b(?:do not|don't|never)\s+[^.!?\r\n;]*", "", RegexOptions.IgnoreCase)));
        // Read-only investigation plans need no implementation quality phase.
        if (!Regex.IsMatch(work, @"\b(implement|create|build|fix|repair|refactor|add|modify|write|update|set up)\b", RegexOptions.IgnoreCase)) return plan;
        var existing = new List<TaskStep>();
        while (plan.Steps.LastOrDefault() is { Status: TaskStepStatus.Pending } last &&
            Regex.IsMatch(last.Description.Trim(), @"^(?:test and repair(?: the finished result)?|final (?:quality pass|testing|validation)|test and fix(?: bugs)?)$", RegexOptions.IgnoreCase))
        {
            existing.Insert(0, last);
            plan.Steps.RemoveAt(plan.Steps.Count - 1);
        }
        plan.Steps.Add(new TaskStep
        {
            StepNumber = plan.Steps.Count == 0 ? 1 : plan.Steps.Max(s => s.StepNumber) + 1,
            Description = "Test and repair the finished result",
            IsFinalQualityPhase = true,
            Instruction = Start + " Use the original request and prior step results to identify the main user-facing outcomes. " +
                "Inspect and run existing relevant tests first. For software, add a small reusable smoke-test suite where " +
                "coverage is missing, using existing project tooling. Test the actual deliverable, not copied source data " +
                "or unconditional assertions. Exercise the normal path, important failure cases, and reset/restart behavior where applicable. " +
                "For interactive projects, exercise controls and inspect real console errors and displayed state with available browser tools. " +
                "Opening or refreshing a preview alone is not evidence of successful rendering or interaction. " +
                "For non-executable deliverables, use appropriate content and consistency checks instead of inventing code tests. " +
                "Fix concrete defects without expanding scope or weakening tests, then rerun affected checks after the final edit. " +
                "Keep test files. Use focused repairs within the existing execution budget; report an unresolved failure " +
                "with its concrete blocker if checks still fail. Do not install new dependencies or tools without required approval. " +
                "Report exact commands and results, browser observations, fixes, and any untested outcomes separately. " +
                "If browser console output was not captured, explicitly say 'console not inspected'. Syntax checks and " +
                "mocked-browser tests do not prove a full browser playthrough, visual appearance, or absence of browser errors. " +
                "Never claim untested behavior passed. If a required check cannot run, report FAILED with the missing capability " +
                "or decision so the existing recovery flow can pause or retry; do not keep looping.",
            AcceptanceCriteria = [
                "Main requested outcomes have appropriate checks against the actual deliverable; existing tests are reused where suitable",
                "Observed failures are repaired and affected checks rerun, or an explicit unresolved failure is reported",
                "The final report distinguishes executed checks, browser observations, and untested outcomes, with reusable tests retained"
            ]
        });
        if (existing.Count > 0)
        {
            var quality = plan.Steps[^1];
            quality.Instruction += "\nProject-specific checks from the proposed final phase:\n" +
                string.Join("\n", existing.Select(s => s.Instruction));
            quality.AcceptanceCriteria.AddRange(existing.SelectMany(s => s.AcceptanceCriteria).Distinct());
        }
        return plan;
    }
}
