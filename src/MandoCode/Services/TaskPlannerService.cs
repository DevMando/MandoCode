using System.Text.RegularExpressions;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Service for planning and executing complex multi-step tasks.
/// Breaks down complex requests into smaller, manageable steps to prevent timeouts.
/// </summary>
public class TaskPlannerService
{
    private readonly AIService _aiService;
    private readonly MandoCodeConfig _config;

    /// <summary>
    /// Imperative verbs that indicate a complex action request (must appear at start).
    /// </summary>
    private static readonly string[] ImperativeVerbs =
    {
        "create", "build", "implement", "make", "develop", "write",
        "add", "design", "set up", "configure", "generate", "refactor",
        "update", "modify", "change", "fix", "debug", "optimize"
    };

    /// <summary>
    /// Keywords that indicate a broad scope for the request.
    /// </summary>
    private static readonly string[] ScopeIndicators =
    {
        "game", "application", "app", "feature", "system", "component",
        "page", "form", "api", "endpoint", "service", "module",
        "website", "site", "project", "program", "tool", "utility",
        "class", "function", "method", "interface", "database"
    };

    /// <summary>
    /// Question words that indicate an inquiry rather than a task request.
    /// </summary>
    private static readonly string[] QuestionIndicators =
    {
        "what", "why", "how", "when", "where", "who", "which",
        "can you explain", "tell me about", "describe", "show me"
    };

    public TaskPlannerService(AIService aiService, MandoCodeConfig config)
    {
        _aiService = aiService;
        _config = config;
    }

    /// <summary>
    /// Simple single-action verbs that should never trigger planning on their own.
    /// These represent quick file operations, not complex multi-step tasks.
    /// </summary>
    private static readonly string[] SimpleActionVerbs =
    {
        "delete", "remove", "read", "show", "list", "find",
        "search", "open", "cat", "print", "display", "rename"
    };

    /// <summary>
    /// Determines if a user message should trigger the planning workflow.
    /// Uses improved heuristics to avoid planning for simple questions or requests.
    /// </summary>
    public bool RequiresPlanning(string userMessage)
    {
        if (!_config.EnableTaskPlanning)
            return false;

        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Rule 0: Questions don't require planning
        if (IsQuestion(lower))
            return false;

        // Rule 1: Simple single-action requests never need planning
        // Short requests starting with simple verbs like "delete file X" should execute directly
        if (IsSimpleSingleAction(lower))
            return false;

        // Rule 2: Check for numbered list with 3+ items (explicit multi-step)
        var numberedListCount = Regex.Matches(lower, @"^\s*\d+[\.\)]\s+", RegexOptions.Multiline).Count;
        if (numberedListCount >= 3)
            return true;

        // Rule 3: Check for imperative verb at start + scope indicator + enough substance
        // Short requests like "create a function" or "add a component" are single actions — not plans
        var startsWithImperative = StartsWithImperative(lower);
        var hasScope = ScopeIndicators.Any(s => lower.Contains(s));
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (startsWithImperative && hasScope && wordCount >= 12)
            return true;

        // Rule 4: Very long requests likely have detailed requirements
        if (trimmed.Length > 400)
            return true;

        // Rule 5: Multiple explicit tasks connected with "and" or "also" — only when request is substantial
        if (wordCount >= 10 && StartsWithImperative(lower))
        {
            var hasMultipleTasks = lower.Contains(" and then ") ||
                                  (lower.Contains(" also ") && lower.Contains(" and "));
            if (hasMultipleTasks)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects simple single-action requests that should skip planning.
    /// e.g., "delete poem.txt", "remove the file", "show me Program.cs"
    /// </summary>
    private static bool IsSimpleSingleAction(string lowerMessage)
    {
        // Must start with a simple action verb
        var startsWithSimple = SimpleActionVerbs.Any(v =>
            lowerMessage.StartsWith(v + " "));

        if (!startsWithSimple)
            return false;

        // Short requests with simple verbs are always simple actions
        if (lowerMessage.Length < 150 && !lowerMessage.Contains(" and ") && !lowerMessage.Contains(" also "))
            return true;

        return false;
    }

    /// <summary>
    /// Determines if the message is a question rather than a task request.
    /// </summary>
    private static bool IsQuestion(string lowerMessage)
    {
        // Check if message ends with question mark
        if (lowerMessage.TrimEnd().EndsWith('?'))
            return true;

        // Check if message starts with question words
        foreach (var question in QuestionIndicators)
        {
            if (lowerMessage.StartsWith(question))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if the message starts with an imperative verb (action request).
    /// </summary>
    private static bool StartsWithImperative(string lowerMessage)
    {
        foreach (var verb in ImperativeVerbs)
        {
            // Check for verb at start, followed by space or 'a'/'an'/'the'
            if (lowerMessage.StartsWith(verb + " ") ||
                lowerMessage.StartsWith(verb + " a ") ||
                lowerMessage.StartsWith(verb + " an ") ||
                lowerMessage.StartsWith(verb + " the "))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates a task plan from a user request.
    /// </summary>
    public async Task<TaskPlan> CreatePlanAsync(string userMessage)
    {
        var plan = new TaskPlan
        {
            OriginalRequest = userMessage,
            Status = TaskPlanStatus.Pending
        };

        try
        {
            // Get the plan from the AI using the planning prompt
            var planResponse = await _aiService.GetPlanAsync(userMessage);

            // Parse the response into steps
            plan.Steps = ParsePlanResponse(planResponse);

            if (plan.Steps.Count == 0)
            {
                // If parsing failed, create a single step with the original request
                plan.Steps.Add(new TaskStep
                {
                    StepNumber = 1,
                    Description = "Execute request",
                    Instruction = userMessage,
                    Status = TaskStepStatus.Pending
                });
            }
        }
        catch (Exception ex)
        {
            // If planning fails, fall back to a single step
            plan.Steps.Add(new TaskStep
            {
                StepNumber = 1,
                Description = "Execute request (planning failed)",
                Instruction = userMessage,
                Status = TaskStepStatus.Pending
            });

            plan.ExecutionSummary = $"Planning failed: {ex.Message}. Falling back to direct execution.";
        }

        return plan;
    }

    /// <summary>
    /// Executes a task plan step by step, yielding progress events.
    /// </summary>
    public async IAsyncEnumerable<TaskProgressEvent> ExecutePlanAsync(TaskPlan plan)
    {
        plan.Status = TaskPlanStatus.InProgress;
        var previousResults = new List<string>();

        yield return TaskProgressEvent.PlanCreated(plan);

        foreach (var step in plan.Steps)
        {
            // Skip if already completed or skipped
            if (step.Status == TaskStepStatus.Completed || step.Status == TaskStepStatus.Skipped)
                continue;

            step.Status = TaskStepStatus.InProgress;
            yield return TaskProgressEvent.StepStarted(plan, step);

            // Execute the step - handle result outside try/catch for yielding
            TaskProgressEvent? stepEvent = null;
            bool shouldCancel = false;

            try
            {
                // Execute the step with context from previous results
                var result = await _aiService.ExecutePlanStepAsync(step.Instruction, previousResults);

                step.Result = result;
                step.Status = TaskStepStatus.Completed;
                previousResults.Add($"Step {step.StepNumber} ({step.Description}): {result}");

                stepEvent = TaskProgressEvent.StepCompleted(plan, step, result);
            }
            catch (Exception ex)
            {
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = ex.Message;
                stepEvent = TaskProgressEvent.StepFailed(plan, step, ex.Message);

                // Check if we should continue (step failure handling is done in UI)
                if (plan.Status == TaskPlanStatus.Cancelled)
                {
                    shouldCancel = true;
                }
                else
                {
                    // If not cancelled, the UI has decided to continue (skip this step)
                    step.Status = TaskStepStatus.Skipped;
                }
            }

            // Wait for all function invocations to complete before moving to next step
            // Uses event-based tracking with 30s timeout as safety net
            await _aiService.CompletionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(30));

            // Yield outside try/catch
            if (stepEvent != null)
            {
                yield return stepEvent;
            }

            if (shouldCancel)
            {
                yield return TaskProgressEvent.PlanCancelled(plan);
                yield break;
            }
        }

        // Determine final plan status
        var allCompleted = plan.Steps.All(s =>
            s.Status == TaskStepStatus.Completed || s.Status == TaskStepStatus.Skipped);

        var anyFailed = plan.Steps.Any(s => s.Status == TaskStepStatus.Failed);

        if (allCompleted && !anyFailed)
        {
            plan.Status = TaskPlanStatus.Completed;
            plan.ExecutionSummary = $"Successfully completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps.";
            yield return TaskProgressEvent.PlanCompleted(plan);
        }
        else if (plan.Status != TaskPlanStatus.Cancelled)
        {
            plan.Status = TaskPlanStatus.Failed;
            plan.ExecutionSummary = $"Completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps with some failures.";
        }
    }

    /// <summary>
    /// Marks a step as skipped and updates plan accordingly.
    /// </summary>
    public void SkipStep(TaskPlan plan, TaskStep step)
    {
        step.Status = TaskStepStatus.Skipped;
    }

    /// <summary>
    /// Cancels a plan execution.
    /// </summary>
    public void CancelPlan(TaskPlan plan)
    {
        plan.Status = TaskPlanStatus.Cancelled;
        plan.ExecutionSummary = "Plan cancelled by user.";
    }

    /// <summary>
    /// Parses an AI response into task steps.
    /// Tries multiple formats in order of preference:
    /// 1. Structured format with ---PLAN-START/END--- markers (STEP N: desc, DO: instruction)
    /// 2. JSON format (array of step objects)
    /// 3. Legacy format (STEP N: desc, INSTRUCTION: instruction)
    /// 4. Numbered list format (1. description - instruction)
    /// </summary>
    private List<TaskStep> ParsePlanResponse(string response)
    {
        var steps = new List<TaskStep>();

        // Try 1: New structured format with markers
        steps = TryParseStructuredFormat(response);
        if (steps.Count > 0)
            return NormalizeStepNumbers(steps);

        // Try 2: JSON format
        steps = TryParseJsonFormat(response);
        if (steps.Count > 0)
            return NormalizeStepNumbers(steps);

        // Try 3: Legacy STEP/INSTRUCTION format
        steps = TryParseLegacyFormat(response);
        if (steps.Count > 0)
            return NormalizeStepNumbers(steps);

        // Try 4: Numbered list format
        steps = TryParseNumberedListFormat(response);
        if (steps.Count > 0)
            return NormalizeStepNumbers(steps);

        // Try 5: Generic step pattern
        steps = TryParseGenericStepFormat(response);
        return NormalizeStepNumbers(steps);
    }

    /// <summary>
    /// Parses the new structured format with ---PLAN-START--- and ---PLAN-END--- markers.
    /// Format: STEP N: description\nDO: instruction
    /// </summary>
    private static List<TaskStep> TryParseStructuredFormat(string response)
    {
        var steps = new List<TaskStep>();

        // Extract content between markers
        var markerPattern = @"---PLAN-START---\s*([\s\S]*?)\s*---PLAN-END---";
        var markerMatch = Regex.Match(response, markerPattern, RegexOptions.IgnoreCase);

        if (!markerMatch.Success)
            return steps;

        var planContent = markerMatch.Groups[1].Value;

        // Parse STEP N: description\nDO: instruction
        var stepPattern = @"STEP\s+(\d+):\s*(.+?)(?=\r?\n)\s*DO:\s*(.+?)(?=(?:STEP\s+\d+:|$))";
        var matches = Regex.Matches(planContent, stepPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count >= 4)
            {
                var stepNumber = int.Parse(match.Groups[1].Value);
                var description = match.Groups[2].Value.Trim();
                var instruction = match.Groups[3].Value.Trim();

                steps.Add(CreateTaskStep(stepNumber, description, instruction));
            }
        }

        return steps;
    }

    /// <summary>
    /// Parses JSON format response (array of step objects).
    /// </summary>
    private static List<TaskStep> TryParseJsonFormat(string response)
    {
        var steps = new List<TaskStep>();

        try
        {
            // Look for JSON array in response
            var jsonPattern = @"\[\s*\{[\s\S]*?\}\s*\]";
            var jsonMatch = Regex.Match(response, jsonPattern);

            if (!jsonMatch.Success)
                return steps;

            var jsonArray = System.Text.Json.JsonDocument.Parse(jsonMatch.Value);

            int stepNumber = 1;
            foreach (var element in jsonArray.RootElement.EnumerateArray())
            {
                var description = element.TryGetProperty("description", out var descProp)
                    ? descProp.GetString() ?? ""
                    : element.TryGetProperty("step", out var stepProp)
                        ? stepProp.GetString() ?? ""
                        : "";

                var instruction = element.TryGetProperty("instruction", out var instrProp)
                    ? instrProp.GetString() ?? ""
                    : element.TryGetProperty("do", out var doProp)
                        ? doProp.GetString() ?? ""
                        : element.TryGetProperty("action", out var actionProp)
                            ? actionProp.GetString() ?? ""
                            : description;

                if (!string.IsNullOrWhiteSpace(description) || !string.IsNullOrWhiteSpace(instruction))
                {
                    steps.Add(CreateTaskStep(stepNumber++, description, instruction));
                }
            }
        }
        catch
        {
            // JSON parsing failed, return empty list
        }

        return steps;
    }

    /// <summary>
    /// Parses legacy format: STEP N: description followed by INSTRUCTION: details
    /// </summary>
    private static List<TaskStep> TryParseLegacyFormat(string response)
    {
        var steps = new List<TaskStep>();

        var stepPattern = @"STEP\s+(\d+):\s*(.+?)(?=\r?\n)[\s\S]*?INSTRUCTION:\s*(.+?)(?=(?:STEP\s+\d+:|$))";
        var matches = Regex.Matches(response, stepPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count >= 4)
            {
                var stepNumber = int.Parse(match.Groups[1].Value);
                var description = match.Groups[2].Value.Trim();
                var instruction = match.Groups[3].Value.Trim();

                steps.Add(CreateTaskStep(stepNumber, description, instruction));
            }
        }

        return steps;
    }

    /// <summary>
    /// Parses numbered list format: 1. description - instruction
    /// </summary>
    private static List<TaskStep> TryParseNumberedListFormat(string response)
    {
        var steps = new List<TaskStep>();

        var numberedPattern = @"(\d+)\.\s+(.+?)(?=(?:\r?\n\d+\.|$))";
        var matches = Regex.Matches(response, numberedPattern, RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count >= 3)
            {
                var stepNumber = int.Parse(match.Groups[1].Value);
                var content = match.Groups[2].Value.Trim();

                // Split on " - " or ": " to separate description from instruction
                var parts = Regex.Split(content, @"\s*[-:]\s*", RegexOptions.None, TimeSpan.FromSeconds(1));
                var description = parts[0].Trim();
                var instruction = parts.Length > 1 ? string.Join(" ", parts.Skip(1)).Trim() : description;

                steps.Add(CreateTaskStep(stepNumber, description, instruction));
            }
        }

        return steps;
    }

    /// <summary>
    /// Parses generic step format: Step N: description (newline) details
    /// </summary>
    private static List<TaskStep> TryParseGenericStepFormat(string response)
    {
        var steps = new List<TaskStep>();

        var altPattern = @"(?:Step\s*)?(\d+)[:\)]\s*([^\n]+)\n([^S\d]+?)(?=(?:Step\s*\d|$|\d+[:\)]))";
        var matches = Regex.Matches(response, altPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count >= 3)
            {
                var stepNumber = int.Parse(match.Groups[1].Value);
                var description = match.Groups[2].Value.Trim();
                var instruction = match.Groups.Count > 3 ? match.Groups[3].Value.Trim() : description;

                steps.Add(CreateTaskStep(stepNumber, description,
                    string.IsNullOrWhiteSpace(instruction) ? description : instruction));
            }
        }

        return steps;
    }

    /// <summary>
    /// Creates a TaskStep with proper truncation.
    /// </summary>
    private static TaskStep CreateTaskStep(int stepNumber, string description, string instruction)
    {
        // Clean up instruction (remove trailing whitespace)
        instruction = Regex.Replace(instruction, @"\s+$", "");

        return new TaskStep
        {
            StepNumber = stepNumber,
            Description = description.Length > 60 ? description[..57] + "..." : description,
            Instruction = instruction,
            Status = TaskStepStatus.Pending
        };
    }

    /// <summary>
    /// Ensures steps are numbered sequentially starting from 1.
    /// </summary>
    private static List<TaskStep> NormalizeStepNumbers(List<TaskStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            steps[i].StepNumber = i + 1;
        }
        return steps;
    }
}
