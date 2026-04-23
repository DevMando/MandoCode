# Changelog

All notable changes to MandoCode will be documented in this file.

## [Unreleased]

### Added
- **Propose-plan tool** — models self-classify multi-step requests via a `propose_plan` Semantic Kernel function. Strongly-typed `PlanStepProposal[]` args replace text parsing; the filter intercepts the call and hands off to the approval UI via `PlanHandoff`.
- **Per-turn tool-call circuit breakers** — `InvocationScope` tracks duplicate-read detection (with write-invalidation) and a result-char budget, catching runaway tool loops before they overflow the provider's context window.
- **Auto-continuation** — when the tool-result budget is exhausted, the assistant's text response acts as implicit compaction and the turn auto-continues with a fresh scope. Config: `enableAutoContinuation`, `maxAutoContinuations`.
- **Synthetic-summary recovery** — on provider `context window exceeds` errors, both plan steps and direct chats compact their history via `SynthesizeHistorySummary` / `CompactChatHistoryAsync` and retry automatically, instead of dying mid-step.
- **Shell-aware system prompt** — `ShellEnvironment` detects cmd.exe vs bash at startup and appends OS-specific rules so the model stops emitting `head`/`grep`/`cat` on Windows. Prefers MandoCode's own `read_file_contents` / `search_text_in_files` / `list_files_match_glob_pattern` over shelling out.
- **Config keys**: `requestTimeoutMinutes` (default 15, range 1–60), `toolResultCharBudget` (default 100k, range 50k–4M), `enableAutoContinuation` (default true), `maxAutoContinuations` (default 3, range 0–10).
- **Clearer overflow error** — when recovery can't rescue the turn (e.g. max continuations hit), `FormatErrorMessage` now detects context-overflow patterns and returns an actionable message (`/clear`, lower budget, switch model) instead of "make sure Ollama is running".

### UI
- **Spinner shows elapsed time** — long waits now display `Defragging drives... · 2m 15s` so it's clear the model is still working vs. frozen.
- **Rotating spinner messages** — the random "fun" status message now rotates every 15s during a single wait; previously it was picked once and never changed.
- **Line-clear before redraw** — spinner uses `\x1b[2K` before each frame so variable-length elapsed text (`9s` → `10s`, `59s` → `1m 0s`) doesn't leave stale chars behind.
- **New plan approval UI** — `HandleProposedPlanAsync` replaces `HandlePlannedExecutionAsync`. Triggered mid-conversation when the model calls `propose_plan`; no more separate "Analyzing request and creating plan..." spinner phase. Three choices: *Execute plan* / *Reject (answer without a plan)* / *Cancel request*.
- **Inline auto-continuation markers** — when the tool-result budget fires, the user sees `⟳ Auto-continuing (1/3) — tool budget reset.` inline in the response stream, so the automatic restart is visible rather than silent.
- **Inline overflow-recovery markers** — when the provider rejects the request, the user sees `⚠ Provider rejected request (context window full). Restarting step with a compacted summary (1/3).` instead of a raw Ollama stack trace.
- **Config display updated** — `mandocode --config show` and the `/config` "View current configuration" panel now include `Request Timeout`, `Tool Result Budget`, and `Auto-Continuation` rows.
- **Wizard** — `ConfigurationWizard` has a new "Per-Request Timeout" step (step 5), and the summary table gained a `Request Timeout` row.

### Changed
- Default per-request timeout raised from 5 min to 15 min, now configurable via `--config set timeout <N>`.
- Task planning routes through the `propose_plan` tool instead of a separate `GetPlanAsync` round-trip. The heuristic `TaskPlannerService.RequiresPlanning` is trimmed to two near-zero-false-positive signals (3+ numbered items or >400 chars) and only exists as a directive nudge for models that won't self-invoke the tool.
- `AIService` constructor now takes `PlanHandoff`. DI registration updated in `Program.cs`.

### Removed
- `AIService.GetPlanAsync` and `SystemPrompts.TaskPlannerPrompt` — replaced by the tool-call path.
- ~300 lines of text-based plan parsers (`TryParseStructuredFormat`, `TryParseJsonFormat`, `TryParseLegacyFormat`, `TryParseNumberedListFormat`, `TryParseGenericStepFormat`) and heuristic verb/scope/question-word lists in `TaskPlannerService`.
- `HandlePlannedExecutionAsync` in `App.razor` — replaced by slimmer `HandleProposedPlanAsync` callback wired through `PlanHandoff`.

### Fixed
- Plan steps arriving with empty descriptions/instructions — `PlanStepProposal` record params now use camelCase (`description`, `instruction`) to match the JSON the model emits. PascalCase caused Semantic Kernel to deserialize the outer array but silently drop every inner string.
- `FromProposals` now filters fully-empty proposals and mirrors single-populated fields so the approval UI never shows a plan of empty steps.

## [0.9.6] - 2026-04-06

### Added
- **Unit test project** with 65 tests covering `InputStateMachine` and `DiffService`
  - Text input, cursor movement, backspace/delete, submit
  - Command autocomplete filtering, dropdown navigation, accept/dismiss
  - Command history navigation with saved input restoration
  - Paste handling with newline-to-space conversion
  - Static helper tests (`IsCommand`, `GetCommandName`) using parameterized `[Theory]` tests
  - Diff computation for new files, modifications, deletions, identical content
  - Line ending normalization (Windows `\r\n`, old Mac `\r`)
  - Large file fallback with sampled diffs
  - Context collapse with configurable context lines
- **Empty response recovery** — when the model returns blank (context overflow), MandoCode now shows a helpful message instead of freezing with a blank screen
- **Rendering timeout guard** — markdown rendering runs with a 30-second timeout and a "Rendering..." spinner after 1 second, preventing permanent freezes on large responses. Falls back to raw text if rendering exceeds the limit.

### Fixed
- **Timeout retry deadlock** — when a request timed out (5-minute limit), `RetryPolicy` was treating the timeout as a transient error and retrying the entire request including all file reads. With 2 retries, this could silently hang for up to 15 minutes. Now the cancellation token is properly passed to `RetryPolicy` so timeouts fail fast.
- **Silent exception suppression** — replaced 6 bare `catch { }` blocks with `catch (Exception ex)` logging to `Debug.WriteLine` across `TerminalThemeService`, `App.razor`, `FileSystemPlugin`, `FunctionInvocationFilter`, and `ShellCommandHandler`
- **Shell command injection surface** — `ShellCommandHandler` and `FileSystemPlugin` now use `ProcessStartInfo.ArgumentList` for proper argument escaping instead of manual string concatenation with `cmd.Replace("\"", "\\\"")`
- **Inconsistent config validation** — `Program.cs` accepted any `maxTokens > 0` while `ValidateAndClamp()` enforced `[256, 131072]`. All validation now uses centralized constants and helpers on `MandoCodeConfig`

### Changed
- Config validation constants (`MinTemperature`, `MaxTemperature`, `MinMaxTokens`, `MaxMaxTokens`) and helpers (`IsValidTemperature`, `IsValidMaxTokens`) are now defined once on `MandoCodeConfig` and referenced by `Program.cs`, `ConfigurationWizard.cs`, and `ValidateAndClamp()`
