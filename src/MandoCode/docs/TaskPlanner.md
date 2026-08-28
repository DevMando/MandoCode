# Task Planner

## Overview

The Task Planner handles complex, multi-step requests that would otherwise overwhelm a single model turn â€” either by timing out, overflowing the provider's context window, or producing one enormous unstructured response. The current design is a **hybrid LLM-tool approach**: the model itself decides mid-conversation whether a request needs decomposition, via a tool function (`propose_plan`). A thin heuristic acts as a safety net for smaller models that don't reliably self-invoke tool calls.

## How It Works

```
User input
    â”‚
[Process @file references]          â€” inject referenced file content
    â”‚
[Thin heuristic check]              â€” 3+ numbered items OR >400 chars?
    â”‚  if yes: append "call propose_plan now" directive
    â”‚
ChatStreamAsync â†’ model sees the tools
    â”‚
    â”œâ”€ model decides the request is single-shot â†’ responds directly
    â”‚
    â””â”€ model calls propose_plan(goal, steps[])
           â”‚
   [AgentFunctionMiddleware intercepts]
           â”‚
   [PlanHandoff.ProcessAsync â†’ UI callback]
           â”‚
   [DisplayPlan + SelectionPrompt]   â€” user: Execute / Reject / Cancel
           â”‚
   [TaskPlannerService.ExecutePlanAsync]
           â”‚
     for each step:
         BeginScope                  â€” per-step circuit breakers reset
         AIService.ExecutePlanStepAsync
           â”œâ”€ tool calls run through dedup / dup-read / budget checks
           â”œâ”€ if budget exhausted AND auto-continuation on â†’ seed + loop
           â””â”€ if provider rejects context window â†’ synthesize recap + loop
         report progress event to UI
   [Plan completes or cancels; summary string returned to the model]
```

The model sees the `propose_plan` tool result as a recap string (e.g. *"Successfully completed 4 of 4 steps."*) and responds conversationally to close the turn.

## Complexity Detection

There are two paths, roughly: the model's own judgement (primary) and a trimmed heuristic (safety net).

### Primary: model self-classification via `propose_plan`

`PlanningPlugin.ProposePlan` is bound as an AIFunction on every agent build (when `enableTaskPlanning` is true). Its `[Description]` tells the model exactly when to call it:

> *"Propose a multi-step plan for the user's request. Call this ONLY when the request clearly requires multiple distinct file or code operations that depend on each other. Do NOT call for questions, single-file edits, lookups, or one-shot operations. Each step MUST include a non-empty `description` and `instruction`."*

Cloud models (kimi, minimax, qwen3-coder) use this path reliably. The `[Description]` text is the canonical source â€” update it there, not in this doc.

### Safety net: thin heuristic

`TaskPlannerService.RequiresPlanning` exists for small local models that won't self-invoke. It fires only on objective, near-zero-false-positive signals:

| Signal | Why |
|--------|-----|
| 3+ numbered list items at line starts | Users writing `1. â€¦ 2. â€¦ 3. â€¦` unambiguously mean multi-step |
| Message > 400 characters | Users don't write 400+ chars for lookups or simple edits |

When it fires, we **don't** call a separate planning endpoint â€” we just append a directive to the user message before sending:

```
[system: this request looks multi-step. Call propose_plan now with the breakdown before doing any work.]
```

The model sees this and calls `propose_plan`. One code path, one execution path.

## Circuit Breakers

`InvocationScope` provides per-chat/per-step bookkeeping that `AgentFunctionMiddleware` consults before every tool call. Two circuits:

### 1. Duplicate-read detection

If the model calls `read_file_contents(relativePath=X)` a second time in the same scope **and no write or edit to X happened in between**, the call short-circuits with:

> *"You already read 'X' this turn and it hasn't changed. Use the content you already have â€” do NOT re-read the same file."*

Writes and edits invalidate the dedup: after a `write_file`/`edit_file`/`delete_file` on path X, the next read of X is allowed through (content may legitimately have changed). After that read, dedup resumes.

This catches the most common stuck-loop pathology where a model re-reads the same 700-line file five times trying to find something.

### 2. Result-char budget

Every tool result's character count accrues against `toolResultCharBudget` (default 100k chars â‰ˆ 25k tokens). Once over, further tool calls are refused with:

> *"Tool-call budget of N chars is exhausted for this turn. Stop calling tools and respond to the user directly with what you have so far."*

The budget's purpose: prevent unbounded tool output from growing the model's context until the provider rejects the request. 100k is a floor safe for 32k-context providers.

## Auto-Continuation

When the result-char budget is exhausted, the model is forced into a text response â€” which naturally acts as a progress summary. If `enableAutoContinuation` is on and `continuations < maxAutoContinuations`, the loop:

1. Adds the summary text as an assistant message.
2. Appends a user message: *"Continue from where you left off. The tool-call budget has been reset."*
3. Begins a fresh `InvocationScope`.
4. Re-invokes the chat call.

This is **implicit compaction**: between turns, the heavyweight tool-result messages don't persist in chat history â€” only the assistant's text. The next turn starts with `system + user + assistant-summary + continue` and a clean scope.

Applied identically in `ChatStreamAsync` (direct chats) and `ExecutePlanStepAsync` (plan steps). Each plan step gets its own continuation budget.

## Synthetic-Summary Recovery

When the provider rejects a request with `context window exceeds limit` / `maximum context` / `prompt is too long` â€” distinct from our own budget circuit â€” we:

1. Walk the step's `stepHistory` via `SynthesizeHistorySummary`, producing a ~1.5k-char recap of what tool calls ran and what they returned (truncated hard).
2. Rebuild the history: system + plan context + `"Continue this step. Previous partial progress (tool-call trace): <recap>. Do NOT redo this work."` + step instruction.
3. Re-invoke with a fresh scope. Counts against `maxAutoContinuations`.

For direct chats, `CompactChatHistoryAsync` does the equivalent on the persistent `_chatHistory`: replaces it with `system + recap + last user message`.

Caveat: recovery isn't flawless. The recap is harness-generated (the turn never completed, so the model produced no summary of its own). Some mid-reasoning nuance is lost. It's "automatic graceful degradation," not "seamless continuation."

## Shell-Aware System Prompt

`ShellEnvironment` detects the OS at startup and appends a rules block to the system prompt:

- **Windows (cmd.exe):** warns against unix tools (`head`, `grep`, `cat`, `ls`), prefers MandoCode's own file tools (`read_file_contents`, `search_text_in_files`, `list_files_match_glob_pattern`).
- **Linux/macOS (bash):** notes POSIX tools are available, same preference for the typed file tools where possible.

The current execute_command shell is cmd.exe on Windows (see `FileSystemPlugin.cs`). If that changes, update `ShellEnvironment.SystemPromptRules` accordingly â€” the doc string is the source of truth for what the model sees.

## Integration with `@` File References

`@path` references are resolved by `ProcessFileReferences` in `App.razor` **before** `RequiresPlanning` runs, so the heuristic sees the request's intent, not a multi-kilobyte paste. The resolved file content is injected into the user message. If the injected content is very long, the 400-char threshold may fire the directive â€” which is usually the right behaviour.

## Configuration

```json
{
  "enableTaskPlanning": true,
  "enableAutoContinuation": true,
  "maxAutoContinuations": 3,
  "toolResultCharBudget": 100000,
  "requestTimeoutMinutes": 15,
  "enableFallbackFunctionParsing": true,
  "functionDeduplicationWindowSeconds": 5,
  "maxRetryAttempts": 2
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `enableTaskPlanning` | `true` | Registers the `propose_plan` tool and runs the heuristic directive |
| `enableAutoContinuation` | `true` | On budget exhaustion, auto-continue with a fresh scope |
| `maxAutoContinuations` | `3` (0â€“10) | Cap on auto-continuations per user request |
| `toolResultCharBudget` | `100000` (50kâ€“4M) | Tool-result chars per scope before the budget circuit fires |
| `requestTimeoutMinutes` | `15` (1â€“60) | Per-chat / per-step wall-clock timeout |
| `enableFallbackFunctionParsing` | `true` | Parse function calls from text output (small models) |
| `functionDeduplicationWindowSeconds` | `5` | Time-based write dedup (legacy; scope dup-read is the stronger guard) |
| `maxRetryAttempts` | `2` | Retry count for transient HTTP errors |

CLI equivalents: `mandocode --config set autoContinue false`, `--config set toolBudget 200000`, `--config set timeout 30`.

---

## Architecture

### Core Components

| File | Purpose |
|------|---------|
| `Plugins/PlanningPlugin.cs` | The `propose_plan` tool function + `PlanStepProposal` record |
| `Services/PlanHandoff.cs` | Bridge between `AgentFunctionMiddleware` and the UI approval callback |
| `Services/TaskPlannerService.cs` | Slim heuristic (`RequiresPlanning`), typed proposal mapping (`FromProposals`), step execution orchestration |
| `Services/InvocationScope.cs` | Per-scope state: read-dedup set, path-modification flags, result-char budget |
| `Services/AgentFunctionMiddleware.cs` | Intercepts `propose_plan`, runs circuit breakers, records scope bookkeeping, diff-approval handoff |
| `Services/AIService.cs` | Chat loop with auto-continuation + synthetic-summary recovery, per-turn scope lifecycle |
| `Services/ShellEnvironment.cs` | Runtime shell detection; injects OS-specific rules into the system prompt |
| `Services/FunctionCompletionTracker.cs` | Event-based function-completion waits between plan steps |
| `Services/DiffService.cs` | LCS-based diff with context collapsing (used by the approval UI) |
| `Services/RetryPolicy.cs` | Exponential backoff for transient HTTP errors |
| `Models/TaskPlan.cs` | `TaskPlan`, `TaskStep`, `TaskPlanStatus`, `TaskStepStatus` |
| `Models/TaskProgressEvent.cs` | Progress event types for UI updates |
| `Models/SystemPrompts.cs` | `BuildMandoCodeAssistant(webSearchEnabled)` (the main prompt; shell rules are appended at runtime) |

### Data Models

```csharp
public record PlanStepProposal(
    [property: Description("Short description for UI (<=60 chars)")] string description,
    [property: Description("Detailed instruction the AI will execute for this step")] string instruction);

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
    public string Description { get; set; }     // Short, shown in the approval table
    public string Instruction { get; set; }     // Detailed, sent to the model for this step
    public TaskStepStatus Status { get; set; }  // Pending, InProgress, Completed, Skipped, Failed
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
}
```

`PlanStepProposal` uses camelCase param names deliberately â€” matches the JSON the model emits. PascalCase caused silent deserialization failures with local Ollama tool calls (outer array deserialized, inner strings came through empty).

### Typed Proposal Mapping

There's exactly one function converting the model's tool-call args to `TaskStep[]`:

```csharp
public static List<TaskStep> FromProposals(PlanStepProposal[] proposals)
```

It drops fully-empty proposals (guard against casing mismatches silently producing a plan of blanks), mirrors a single-populated field into the other if only one arrived, and truncates descriptions to 60 chars. No text parsing â€” if the model speaks the schema, we materialise straight-line.

---

## Key Features

### Event-Based Completion Tracking

`FunctionCompletionTracker` waits for all pending function invocations to settle between steps. `ExecutePlanPlanStepAsync` waits up to 5s for in-flight tool calls before returning, so step N's writes are durable before step N+1 starts.

### Retry Policy

Transient HTTP errors (connection reset, 502/503/504, socket failures) retry with exponential backoff: 500ms â†’ 1s â†’ 2s. Applied to both `ChatStreamAsync` and `ExecutePlanStepAsync`. Provider-side context-window rejections are **not** considered transient â€” they go to the synthetic-summary recovery path instead.

### Intelligent Deduplication

Two layers:

| Layer | Scope | Window/trigger |
|-------|-------|----------------|
| Time-based dedup | Global (`AgentFunctionMiddleware._recentCalls`) | 2s (reads) / 5s configurable (writes) â€” identical args within the window reuse the cached result |
| Scope-level dup-read | Per chat turn / plan step (`InvocationScope`) | Same-path re-read with no intervening write â†’ short-circuit with a stern message |

The scope-level check is the stronger one; the time-based window is a legacy belt-and-braces.

### Fallback Function Parsing

Some local models output function calls as JSON text instead of proper tool calls. `AIService.ProcessTextFunctionCallsAsync` detects and executes those. Handles three JSON shapes: `{"name": ..., "parameters": ...}`, `{"function_call": {...}}`, `{"tool_calls": [{"function": ...}]}`. Controlled by `enableFallbackFunctionParsing`.

---

## User Interaction Flow

### Plan Approval

When the model calls `propose_plan` and `PlanHandoff.OnPlanRequested` fires, `App.razor.HandleProposedPlanAsync` runs:

1. Stops the outer "Thinking..." spinner (so Spectre doesn't fight it).
2. Renders the plan in a bordered table (step number + description).
3. Prompts: **Execute plan** / **Reject (answer without a plan)** / **Cancel request**.
4. On *Execute*: drives `TaskPlannerService.ExecutePlanAsync`, handles each progress event, returns a summary string to the model.
5. On *Reject*: returns *"User rejected the proposed plan; respond to the original request directly."* â€” the model sees this as the tool result and continues normally.
6. On *Cancel*: returns a cancel instruction; the model stops.

### Error Handling During Execution

Step-level failures (thrown exceptions during `ExecutePlanStepAsync`) go through `HandleProgressEvent`'s `StepFailed` case. The user gets **Skip this step and continue** or **Cancel the plan**.

Context-window rejections don't reach this path â€” they trigger synthetic-summary recovery transparently.

### Progress Feedback

Per step:
- Step header on start (`Step 3/5: Create main.js`)
- Real-time function-invocation lines (`â— Writing to path`, `â— Read file (72 lines)`)
- Contextual spinner messages between tool calls (`Analyzing 2 files...`) with an elapsed counter and rotating "fun" message
- Inline auto-continuation marker when the budget fires: `âŸ³ Auto-continuing (1/3) â€” tool budget reset.`
- Inline recovery marker on provider overflow: `âš  Provider rejected request (context window full). Restarting step with a compacted summary (1/3).`

### Taskbar Progress (Windows Terminal)

OSC 9;4 codes fire on step transitions: green fill advances by `currentStep / totalSteps`, turns red on failure, clears on plan completion. Direct chats pulse indeterminate.

---

## Testing Recommendations

| Test Case | Expected Behaviour |
|-----------|-------------------|
| `what is 2+2?` | No planning; model answers directly |
| `delete poem.txt` | No planning; model calls `delete_file` |
| `create a function that does X` | Heuristic doesn't fire; model decides (usually single-shot) |
| `1. Scaffold project\n2. Add tests\n3. Wire CI` | Heuristic fires â†’ directive appended â†’ model calls `propose_plan` |
| `Build a Three.js tic-tac-toe game with hover effects and win detection` | Model calls `propose_plan` on its own (cloud models) |
| Model repeats `read_file_contents` for same path twice | Second call short-circuits with the "already read" message |
| Tool results top the 100k-char budget | Budget circuit fires, model emits text summary, auto-continuation kicks in |
| Provider returns `context window exceeds limit` | Synthetic-summary recovery compacts + retries |
| User hits Ctrl+C mid-step | Step cancels cleanly; plan status â†’ Cancelled |
| Plan proposed with empty strings | `FromProposals` drops them; `PlanHandoff` returns "no steps" message |

Unit tests cover the deterministic parts: `TaskPlannerServiceTests`, `InvocationScopeTests`, `PlanHandoffTests`, `ShellEnvironmentTests`, `MandoCodeConfigTests`. End-to-end flows are manual.

---

## Related Features

### Diff Approvals

File writes and deletions intercepted by `AgentFunctionMiddleware` trigger a diff approval UI before execution. This works alongside the task planner â€” during planned step execution, each file operation still requires approval (unless globally or per-session bypassed).

See the main [README](../../../README.md#diff-approvals) for user-facing documentation.

---

## Migration to MAF Workflows (in progress)

The planner is being rebuilt on `Microsoft.Agents.AI.Workflows`. The driver is a single root cause:
**the whole plan currently executes inside one `propose_plan` tool call.** Because the outer model's
turn is still open, it never sees the steps run, treats the summary as "not started yet," and redoes
the work — so `BuildManifest`, the App.razor stop directive, and the `PlanWorkCompleted` gate all
exist to stop it. The watchdog pause and the prompt-gate release dance have the same origin.

Decision: an **authored `WorkflowBuilder` graph**, not Magentic. Magentic's .NET manager is more
local-model-tolerant than expected (prompt-injected schema, tolerant JSON extraction, 3 retries),
but MandoCode has one agent — so `next_speaker` selection is degenerate — it throws and kills the
run after 3 ledger parse failures with no way to raise the limit, and **its manager cannot have
tools**, which would mean planning code changes against a repo it can't read. Handoff is rejected
too: it routes via `handoff_to_*` tool calls that cannot be forced, the worst possible dependency on
7B–30B instruction-following.

| Phase | Scope | State |
|---|---|---|
| 0 | Spike the real 1.19.0 assembly | done |
| 1 | Freeze identity / checkpoint envelope / config key; extract `IPlanRunner` + `IPlanStepExecutor` | done |
| 2 | Un-nest: `propose_plan` returns a receipt, the host runs the plan after the turn drains | done |
| 3 | The graph behind `planner=workflow`: fixed topology, triage owns plan state | done |
| 3b | Move approval and step decisions onto `RequestPort`s; make progress read-only | |
| 4 | Checkpointing + resume | |
| 5 | Retry / replan / forced-tool-use escalation | |
| 6 | Flip the `planner` default, then delete the legacy runner | |

Spike findings worth keeping:

- A middleware-decorated agent **keeps its middleware** when used as a workflow executor (verified on
  net10.0 and net8.0). The guard circuits survive the boundary.
- `Microsoft.Agents.AI.Workflows` requires `Microsoft.Agents.AI` at the *same* version — a mismatch is
  `NU1605`, which `TreatWarningsAsErrors` turns into a build error. Bump both together.
- No `<NoWarn>` is needed: `WorkflowBuilder`, `RequestPort`, `InProcessExecution`, `CheckpointManager`
  and `FileSystemJsonCheckpointStore` carry no `[Experimental]` attribute in 1.19.0.
- ⚠️ **`WatchStreamAsync` returns before the run quiesces.** Poll `GetStatusAsync()` until it leaves
  `RunStatus.Running` before declaring a plan finished — measuring at stream-end reports a plan
  complete while its steps are still writing files.

Graph-authoring notes (all learned the hard way against the real assembly):

- Every executor that sends must declare `[SendsMessage(typeof(T))]`; the finalizer needs
  `[YieldsOutput(typeof(T))]`. Omitting one throws at **runtime**, not compile time.
- State written without a scope name is **executor-private**. Plan state uses the named-scope
  overload — the first attempt silently reported "0 steps completed".
- The conditional `AddEdge<T>` overloads are mutually ambiguous to C# overload resolution, and hand
  the condition a nullable `T?`. Plain edges plus typed messages plus an explicit `targetId` route
  unambiguously, so conditions are not needed.
- `Workflow.ToString()` and `EdgeInfo.ToString()` return type names only — a topology test built on
  them passes vacuously. Use `ReflectExecutors()` / `ReflectEdges()` / `WorkflowVisualizer`.
- `InProcessExecution.RunStreamingAsync`'s third positional parameter is `sessionId`, not the
  cancellation token.

`planner` selects the engine (`legacy` | `workflow` | `default`) and is re-read per plan by
`PlanRunnerSelector`, so it can be flipped mid-session without losing history — which is what makes
an honest A/B against a local model practical. `PlanRunnerBehaviorTests` runs every behavioral case
against **both** engines: while both are selectable, any divergence would make that A/B
uninterpretable, since a behavior difference would be indistinguishable from a model difference.

Executor and agent identities are fixed in `PlanExecutorIds` and must not drift: MAF derives
workflow-executor identity from both the agent's `Id` and `Name`, and a checkpoint written under one
identity can never be resumed under another. `PlanExecutorIdsTests` holds a golden list precisely so
a rename fails loudly.

---

## Future Improvements

- [ ] Step dependency graph for parallel execution of independent steps
- [~] Plan persistence for resuming interrupted plans across sessions — in progress, phase 4 above;
      envelope and identity scheme already landed in phase 1
- [~] User-editable plan before approval — in progress, phase 3 above; the approval view will also
      show each step's `Instruction`, which today is never shown before the user approves it
- [x] ~~Mid-generation progress signals~~ — shipped: `StreamingMode` config streams chunks as the model emits them (via `_agent.RunStreamingAsync`, buffered through `StreamBuffering` for the stall-watchdog heartbeat), with non-streaming still available as a fallback for models where streaming + auto-invoke proved unreliable.
- [ ] Smarter `Required` vs `Auto` tool-choice on plan-step first turn (force a tool call on steps that the model drifts into prose on)
