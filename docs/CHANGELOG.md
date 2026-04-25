# Changelog

All notable changes to MandoCode will be documented in this file.

## [Unreleased]

## [0.9.8] - 2026-04-25

### Added
- **MCP (Model Context Protocol) support** — MandoCode can now connect to any MCP server (stdio or remote HTTP/SSE) at startup and expose its tools to the model alongside the built-in plugins. Config shape mirrors Claude Desktop's `mcpServers` block, so snippets from any MCP server's README drop in unchanged. Static bearer tokens via `headers` work; OAuth-only servers can be reached through the `npx mcp-remote` wrapper. First call of each `(server, tool)` pair prompts for approval (reuses the existing diff-approval UX); servers list their tools under an `mcp_<server>` plugin. New commands: `/mcp` (status + tool counts per server), `/mcp add` (interactive wizard that adds a server without hand-editing JSON — uses RazorConsole's native `TextInput`/`Select` so it stays compatible with the VDOM render loop), `/mcp remove <name>`, `/mcp tools [server]`, `/mcp-reload` (restart and re-register). New config keys: `enableMcp` (master switch, default true) and `mcpServers` (empty by default).
- **Skills** — user-defined skill packs that the model can auto-invoke based on name and short description. A skill is a folder containing a `SKILL.md` file (instructions, optional scripts, optional examples). `SkillLoader` scans two directories at startup: a user-global `~/.mandocode/skills/` and a project-local `./.mandocode/skills/`, with project skills overriding user skills of the same name. Loaded skill names + one-line descriptions are injected into the system prompt as a "skill index" so the model knows what's available; when relevant, the model calls the new `load_skill(name)` Semantic Kernel function (in the `Skills` plugin) to retrieve the full instructions on demand — keeps the system prompt small even with dozens of installed skills. New `/skills` slash command lists installed skills and surfaces setup instructions when none are present. Config: `userSkillsDirectory` and `projectSkillsDirectory` (both override the defaults; project dir is auto-discovered by walking up from the project root looking for an existing `.mandocode/skills/`).
- **Configurable markdown render timeout** — new `markdownRenderTimeoutSeconds` config key (default 60, range 5–300). Large tool-grounded responses (MCP output, many tables, many code blocks) can legitimately take 30–60 seconds to render; the previous hardcoded 30s guard fell back to raw text too eagerly. Raise via `mandocode --config set renderTimeout 120`.
- **Reusable VDOM wizard primitives** — `WizardPromptTextAsync`, `WizardPromptSelectAsync`, `WizardConfirmAsync` in `App.razor` are native RazorConsole flows that future wizards (config, diff approvals) can adopt instead of Spectre's `TextPrompt`/`SelectionPrompt`, which conflict with the VDOM render loop on Windows.
- **Propose-plan tool** — models self-classify multi-step requests via a `propose_plan` Semantic Kernel function. Strongly-typed `PlanStepProposal[]` args replace text parsing; the filter intercepts the call and hands off to the approval UI via `PlanHandoff`.
- **Per-turn tool-call circuit breakers** — `InvocationScope` tracks duplicate-read detection (with write-invalidation) and a result-char budget, catching runaway tool loops before they overflow the provider's context window.
- **Auto-continuation** — when the tool-result budget is exhausted, the assistant's text response acts as implicit compaction and the turn auto-continues with a fresh scope. Config: `enableAutoContinuation`, `maxAutoContinuations`.
- **Synthetic-summary recovery** — on provider `context window exceeds` errors, both plan steps and direct chats compact their history via `SynthesizeHistorySummary` / `CompactChatHistoryAsync` and retry automatically, instead of dying mid-step.
- **Shell-aware system prompt** — `ShellEnvironment` detects cmd.exe vs bash at startup and appends OS-specific rules so the model stops emitting `head`/`grep`/`cat` on Windows. Prefers MandoCode's own `read_file_contents` / `search_text_in_files` / `list_files_match_glob_pattern` over shelling out.
- **Config keys**: `requestTimeoutMinutes` (default 15, range 1–60), `toolResultCharBudget` (default 100k, range 50k–4M), `enableAutoContinuation` (default true), `maxAutoContinuations` (default 3, range 0–10).
- **Clearer overflow error** — when recovery can't rescue the turn (e.g. max continuations hit), `FormatErrorMessage` now detects context-overflow patterns and returns an actionable message (`/clear`, lower budget, switch model) instead of "make sure Ollama is running".
- **Streaming `execute_command` with idle-based timeout** — the `execute_command` tool no longer buffers stdout/stderr until the process exits. Output is now streamed line-by-line via `OutputDataReceived`/`ErrorDataReceived` handlers, with each line piped to the spinner's live activity row (`$ <cmd> → <latest line>`). The hardcoded 30s wall-clock timeout has been replaced by a 30-second **idle** timeout (kills only after 30s of *no new output*) plus a 10-minute hard ceiling. A long but progressing build (`dotnet build`, `npm install`, full test runs) now runs to completion as long as it keeps emitting output; a genuinely hung process still dies at 30s of silence. On kill, the LLM gets a richer message — `Killed: idle 30s with no output. Elapsed: 47s. Last line: …` plus the partial output captured so far — instead of `"Error: Command timed out after 30 seconds."`.

### UI
- **Spinner shows elapsed time** — long waits now display `Defragging drives... · 2m 15s` so it's clear the model is still working vs. frozen.
- **Rotating spinner messages** — the random "fun" status message now rotates every 15s during a single wait; previously it was picked once and never changed.
- **Line-clear before redraw** — spinner uses `\x1b[2K` before each frame so variable-length elapsed text (`9s` → `10s`, `59s` → `1m 0s`) doesn't leave stale chars behind.
- **New plan approval UI** — `HandleProposedPlanAsync` replaces `HandlePlannedExecutionAsync`. Triggered mid-conversation when the model calls `propose_plan`; no more separate "Analyzing request and creating plan..." spinner phase. Three choices: *Execute plan* / *Reject (answer without a plan)* / *Cancel request*.
- **Inline auto-continuation markers** — when the tool-result budget fires, the user sees `⟳ Auto-continuing (1/3) — tool budget reset.` inline in the response stream, so the automatic restart is visible rather than silent.
- **Inline overflow-recovery markers** — when the provider rejects the request, the user sees `⚠ Provider rejected request (context window full). Restarting step with a compacted summary (1/3).` instead of a raw Ollama stack trace.
- **Config display updated** — `mandocode --config show` and the `/config` "View current configuration" panel now include `Request Timeout`, `Tool Result Budget`, and `Auto-Continuation` rows.
- **Wizard** — `ConfigurationWizard` has a new "Per-Request Timeout" step (step 5), and the summary table gained a `Request Timeout` row.
- **Live shell-command activity** — `SpinnerService` gained `UpdateActivity(string?)` so callers can refresh the dim line above the spinner mid-spin. `ExecuteCommand` calls it on every streamed line, giving the same "kinda like web search" status feedback for shell commands. On `Stop()`, if the activity was ever live-updated during the spin, the final line is preserved in scrollback (`$ dotnet run → Build succeeded.`) instead of being cleared along with the spinner — so you can scroll back and see what the last meaningful output was.
- **Tighter markdown / HTML rendering** — the markdown renderer and HTML parser were reworked to produce noticeably tighter output. Old style was too broad in the line spacing it emitted, which made dense responses (tables, nested bullets, multi-section technical answers) hard to scan. Headings, bullet points, and section breaks now render with consistent compact spacing, plus a refreshed color palette for the UI. Affects every assistant response that uses markdown — i.e., almost all of them.

### Changed
- Default per-request timeout raised from 5 min to 15 min, now configurable via `--config set timeout <N>`.
- Task planning routes through the `propose_plan` tool instead of a separate `GetPlanAsync` round-trip. The heuristic `TaskPlannerService.RequiresPlanning` is trimmed to two near-zero-false-positive signals (3+ numbered items or >400 chars) and only exists as a directive nudge for models that won't self-invoke the tool.
- `AIService` constructor now takes `PlanHandoff`. DI registration updated in `Program.cs`.
- `FileSystemPlugin` constructor now takes an optional `SpinnerService` (passed by `AIService` so streamed shell output can update the live activity row). `AIService` constructor signature gained `SpinnerService spinner` as its final parameter; DI registration in `Program.cs` resolves the singleton and threads it through.

### Removed
- `AIService.GetPlanAsync` and `SystemPrompts.TaskPlannerPrompt` — replaced by the tool-call path.
- ~300 lines of text-based plan parsers (`TryParseStructuredFormat`, `TryParseJsonFormat`, `TryParseLegacyFormat`, `TryParseNumberedListFormat`, `TryParseGenericStepFormat`) and heuristic verb/scope/question-word lists in `TaskPlannerService`.
- `HandlePlannedExecutionAsync` in `App.razor` — replaced by slimmer `HandleProposedPlanAsync` callback wired through `PlanHandoff`.

### Fixed
- Plan steps arriving with empty descriptions/instructions — `PlanStepProposal` record params now use camelCase (`description`, `instruction`) to match the JSON the model emits. PascalCase caused Semantic Kernel to deserialize the outer array but silently drop every inner string.
- `FromProposals` now filters fully-empty proposals and mirrors single-populated fields so the approval UI never shows a plan of empty steps.
- **Paste with newlines no longer auto-submits** — pasting multi-line content (or any string containing `\r`/`\n`) into the prompt was triggering a premature Enter/submit before the user could finish editing. Paste now preserves the buffer for explicit submission.
- **Keystrokes during diff approval no longer get dropped** — when the approval flow asked for additional info/instructions before approving a file write, individual keystrokes were being silently swallowed by the parent input loop. The approval prompt now owns its keystrokes for the duration of the prompt.

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
