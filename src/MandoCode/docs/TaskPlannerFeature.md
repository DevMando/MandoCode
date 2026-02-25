# Task Planner Feature

## Overview

The Task Planner handles complex, multi-step requests that would otherwise cause timeouts when using smaller or slower AI models. Instead of attempting to process an entire complex request in a single API call, the Task Planner breaks it down into manageable steps and executes them sequentially with progress tracking and error recovery.

## How It Works

```
User Input
    |
[Process @file references] - Attach referenced file content
    |
[Complexity Detection] --> Simple request/Question? --> Direct execution
    | Complex request
[Create Plan] - AI generates numbered steps
    |
[Display Plan] - User can approve, skip planning, or cancel
    | Approved
[Execute Steps] - Each step runs with completion tracking
    |
Complete
```

### Complexity Detection

The system uses heuristics to detect complex requests while filtering out simple questions:

**Questions are NOT planned** (filtered first):
- Messages ending with `?`
- Messages starting with: "what", "why", "how", "when", "where", "who", "which", "can you explain", "tell me about", "describe", "show me"

**Triggers planning:**
- **Imperative verb at start + scope indicator**: "Create a game", "Build an API", "Implement a feature"
- **Numbered lists with 2+ items**: "1. Create X\n2. Add Y"
- **Long requests**: Messages over 250 characters
- **Multiple explicit tasks**: Using "and" or "also" with an imperative verb

**Imperative verbs** (must appear at start):
- "create", "build", "implement", "make", "develop", "write", "add", "design", "set up", "configure", "generate", "refactor", "update", "modify", "change", "fix", "debug", "optimize"

**Scope indicators**:
- "game", "application", "app", "feature", "system", "component", "page", "form", "api", "endpoint", "service", "module", "website", "site", "project", "program", "tool", "utility", "class", "function", "method", "interface", "database"

### Integration with `@` File References

When a user includes `@file` references in a complex request, the file content is resolved and injected **before** the planner evaluates the input. This means:

- The AI has full context of referenced files when generating the plan
- Plan steps can reference the content of attached files
- Example: `refactor @src/MandoCode/Services/AIService.cs to use the strategy pattern` triggers planning with the file content available

### Configuration

```json
{
  "enableTaskPlanning": true,
  "enableFallbackFunctionParsing": true,
  "functionDeduplicationWindowSeconds": 5,
  "maxRetryAttempts": 2
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `enableTaskPlanning` | `true` | Enable/disable the task planner |
| `enableFallbackFunctionParsing` | `true` | Parse function calls from text output |
| `functionDeduplicationWindowSeconds` | `5` | Window for preventing duplicate operations |
| `maxRetryAttempts` | `2` | Retry count for transient errors |

---

## Architecture

### Core Components

| File | Purpose |
|------|---------|
| `Services/TaskPlannerService.cs` | Complexity detection, plan parsing, execution orchestration |
| `Services/AIService.cs` | AI communication, plan generation, step execution |
| `Services/FunctionCompletionTracker.cs` | Event-based function completion tracking |
| `Services/FunctionInvocationFilter.cs` | Function deduplication, execution events |
| `Services/RetryPolicy.cs` | Exponential backoff retry for transient errors |
| `Services/FileAutocompleteProvider.cs` | File content reading for `@` references (used before planning) |
| `Models/TaskPlan.cs` | Data models for plans and steps |
| `Models/TaskProgressEvent.cs` | Progress event types for UI updates |
| `Models/SystemPrompts.cs` | Planning prompt with structured format |

### TaskPlan Model

```csharp
public class TaskPlan
{
    public string OriginalRequest { get; set; }
    public List<TaskStep> Steps { get; set; }
    public TaskPlanStatus Status { get; set; }  // Pending, InProgress, Completed, Cancelled, Failed
    public string? ExecutionSummary { get; set; }
}

public class TaskStep
{
    public int StepNumber { get; set; }
    public string Description { get; set; }     // Short description for UI
    public string Instruction { get; set; }     // Detailed instruction for AI
    public TaskStepStatus Status { get; set; }  // Pending, InProgress, Completed, Skipped, Failed
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
}
```

### Plan Parsing

The `TaskPlannerService` supports multiple plan formats (tried in order):

1. **Structured format with markers** (preferred):
   ```
   ---PLAN-START---
   STEP 1: Description
   DO: Detailed instruction
   ---PLAN-END---
   ```

2. **JSON format**:
   ```json
   [{"description": "Step 1", "instruction": "Details"}]
   ```

3. **Legacy STEP/INSTRUCTION format**:
   ```
   STEP 1: Description
   INSTRUCTION: Detailed instruction
   ```

4. **Numbered lists**:
   ```
   1. Description - instruction
   2. Another step - details
   ```

---

## Key Features

### Event-Based Completion Tracking

Uses `FunctionCompletionTracker` with semaphore-based signaling to wait for all function invocations to complete before proceeding to the next step:

```csharp
// Waits until all pending function calls finish (up to 30s timeout)
await _aiService.CompletionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(30));
```

### Retry Policy

Transient errors (HTTP, timeout, socket) are automatically retried with exponential backoff:

```
Attempt 1 -> fail -> wait 500ms
Attempt 2 -> fail -> wait 1000ms
Attempt 3 -> fail -> throw
```

Applied to:
- `ChatAsync()` — main chat endpoint
- `ExecutePlanStepAsync()` — plan step execution

### Intelligent Deduplication

Prevents duplicate function calls within configurable time windows:

| Operation Type | Window | Key Components |
|---------------|--------|----------------|
| Read operations | 2 seconds | Function name + all arguments |
| Write operations | 5 seconds (configurable) | Function name + path + content hash (SHA256) |

### Fallback Function Parsing

Some local models output function calls as JSON text instead of proper tool calls. The fallback parser handles multiple formats:

```json
// Standard format
{"name": "write_file", "parameters": {...}}

// OpenAI-style function_call
{"function_call": {"name": "func", "arguments": {...}}}

// Tool calls format
{"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}
```

---

## User Interaction Flow

### Plan Display

Plans are presented in a table with step numbers and descriptions. The user chooses:

- **Execute plan** — runs all steps sequentially with progress output
- **Execute directly (skip planning)** — sends the original request as-is
- **Cancel** — aborts the request

### Error Handling During Execution

When a step fails, the user is prompted:

- **Skip this step and continue** — marks the step as skipped, proceeds to the next
- **Cancel the plan** — stops execution, marks the plan as cancelled

### Progress Feedback

Each step shows:
- Step number and description when starting
- Function invocation notifications in real-time
- Result summary when completed (truncated to 500 chars for display)
- Success/failure status

---

## Testing Recommendations

| Test Case | Expected Behavior |
|-----------|-------------------|
| "What is 2+2?" | No planning (question) |
| "What does 'create' mean?" | No planning (question with action word) |
| "Create a tic-tac-toe game" | Planning triggered |
| "1. Create X, 2. Add Y" | Planning triggered (numbered list) |
| "Create folder TestFolder" | Uses `create_folder` function |
| Same write twice quickly | Second should be skipped (deduplication) |
| "refactor @src/file.cs to use interfaces" | Planning triggered with file content attached |

---

## Future Improvements

- [ ] Step dependency graph for parallel execution of independent steps
- [ ] LLM-based complexity classification for ambiguous requests
- [ ] Plan persistence for resuming interrupted plans
- [ ] User-editable steps before execution
- [ ] Estimated complexity scoring
