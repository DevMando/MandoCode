# Changelog

All notable changes to MandoCode will be documented in this file.

## [Unreleased]

## [0.9.9] - 2026-04-26

### Onboarding overhaul

First-run is now a guided wizard instead of a static "install Ollama, run serve, pull a model, then come back" panel. Every step that *can* be auto-detected, auto-installed, or auto-recovered now is.

### Added
- **Guided first-run wizard** — auto-fires when no config exists and on `/setup`. Walks users through Ollama install → daemon start → cloud sign-in → model pick → context size, all from inside the terminal.
- **Auto-install Ollama** — wizard runs `winget install Ollama.Ollama` (Windows), `brew install ollama` (macOS), or `curl -fsSL https://ollama.com/install.sh | sh` (Linux) as a child process with inherited stdio so users see the installer's own progress and can answer UAC/license/sudo prompts directly. Auto-falls back to opening ollama.com/download in the browser if the install fails for any reason (winget missing, UAC denied, no curl on minimal Linux, etc.).
- **Auto-launch `ollama signin`** — the cloud sign-in walkthrough spawns the CLI command for the user. Browser opens to confirm, the CLI writes the local token. Closes the "browser sign-in alone isn't enough" trap that was returning 401 on first chat.
- **Auto-launch `ollama serve`** — when the daemon isn't running, the wizard offers to start it. Spectre `Status` spinner shows `Starting ollama serve... → Waiting for Ollama at <url>...` so users see live progress.
- **401 auto-recovery on chat** — if a chat response surfaces a 401, the cloud sign-in walkthrough fires immediately after the error message renders. No need to type `/setup` and re-pick the model.
- **Trailing-slash heal** — `http://localhost:11434/` is detected and silently corrected to `http://localhost:11434`. Persisted to config so the heal is permanent.
- **Wrong-port auto-fallback** — when "Start Ollama for me" succeeds but the configured URL doesn't reach the daemon (e.g. config points at `:1313` but `ollama serve` binds to `:11434`), the wizard auto-probes `http://localhost:11434` and switches if reachable. No retype required.
- **Status-aware model-list fetch** — `/api/tags` now distinguishes "user has zero models" (genuine empty) from "request itself failed" (transient daemon hiccup). Auto-retries once with delay; if the second attempt fails, surfaces a clear `/setup` recovery message instead of misrouting users with pulled models into the no-models flow.
- **Picked-model validation** — wizard runs `/api/show` against the picked model before declaring success, surfacing models listed in `/api/tags` but not actually loadable.
- **Post-pick cloud auth check** — `TestCloudAuthAsync` does a 1-token `/api/generate` call after the user picks a cloud model. Catches the case where the model is in `/api/tags` (so the heuristic says "signed in") but the daemon's actual auth token is gone.
- **Tiered local-model picker** with hardware guidance: `qwen3.5:0.8b` (CPU-only), `2b` (4 GB+ GPU), `4b` (mid-range), `9b` (8+ GB VRAM) with size/VRAM expectations spelled out. Auto-pulls the user's selection.
- **Cloud auto-pull** — when the user picks Cloud in the empty-models flow, `minimax-m2.7:cloud` is auto-pulled with a streamed progress spinner.
- **Cloud upsell** — after a successful local pull, surfaces a dim tip recommending `minimax-m2.7:cloud` as the more capable alternative (free with `ollama signin`).
- **Combined cloud/local model picker** — single screen with `(cloud)` / `(local)` badges, cloud bubbles to top, alphabetical otherwise. Replaces the old two-step "Cloud or Local? → list" flow.
- **`/model` slash command** — quick switch model + context size. Skips the rest of `/config`. Tip at the end points users to `/config` for temperature, timeout, and other settings.
- **`/setup` slash command** — guided wizard, always interactive (skips the silent fast path that `/config` would fall into when the user is trying to fix a broken state).
- **`--doctor` CLI flag** — non-interactive preflight that prints .NET runtime version, OS, dotnet tool path, Ollama CLI status, daemon reachability, models pulled, and cloud sign-in state. Exits 0 when everything's green, 1 otherwise. README now points to it as the troubleshooting target.
- **PATH-refresh detection on Windows** — `IsOllamaCliInstalled` falls back to checking canonical install paths (`%LOCALAPPDATA%\Programs\Ollama\ollama.exe`, `/opt/homebrew/bin/ollama`, `/usr/local/bin/ollama`, etc.) when `where`/`which` fails. The wizard detects a fresh Ollama install without users having to relaunch mandocode.
- **Inline color tags** — new `<red>` / `<green>` / `<yellow>` / `<cyan>` HTML tag handlers in `MarkdownHtmlRenderer` for chat error responses. The 401 error now renders the `Error:` header in red and the recommended action in green; backticked `ollama signin` keeps its existing purple inline-code styling.
- **Educational intros** on temperature and max-tokens picker steps — explain what each setting actually does, the relationship between max-tokens and the model's context window, and per-tier usage examples.

### UI
- **k-notation labels** in the max-tokens picker (`32k`, `64k`, `128k`, `200k`) replacing raw token numbers (`32768`, etc).
- **Current-value highlighting** — picker reorders so the user's existing setting (or 32k for fresh installs) appears at the top with a `← current` marker. Spectre's `SelectionPrompt` doesn't support default-selection natively, so the workaround is positional reordering.
- **VDOM-aware text input for URL entry** in `/setup` and `/config` step 1 — uses RazorConsole's `<TextInput>` instead of Spectre's `TextPrompt`, which was dropping keystrokes alongside the live VDOM render loop. Pre-filled with the current URL (`press Enter to keep current`) so users don't have to retype.
- **`HomeView` Razor component** — extracted the dynamic info block (static info + connection state + ready/help) into its own component so it can be cleanly hidden via `@if (!_setupActive)` during `/setup` and the 401 auto-recovery walkthrough, preventing VDOM redraws from stomping on the wizard's imperative output.
- **Runtime version on banner** — startup line now includes `Runtime: .NET 8.0.x` so users always know the runtime they're on (also reported by `--doctor`).
- **Help table reorganized** — `/setup` and `/model` rows added next to `/config`, with descriptions disambiguated so users can tell which command does what.

### Changed
- **Default cloud model** bumped from `minimax-m2.5:cloud` → `minimax-m2.7:cloud` everywhere (config defaults, `CreateDefault`, `GetEffectiveModelName` fallback, README, `/learn` content, system prompt, `/config` examples, error messages).
- **Default `MaxTokens`** bumped from 4k → 32k for new installs. Existing configs with explicit values are preserved.
- **`MaxMaxTokens`** lowered from 256k → 200k after testing showed many cloud models hit a practical limit closer to 200k once system prompts, tool definitions, and response budget are accounted for. Existing configs with 256k saved are clamped to 200k on next load.
- **README install section** rewritten — compact Prerequisites block (.NET 8 SDK + Ollama links), single install command (`dotnet tool install -g MandoCode`), new Troubleshooting section pointing at `--doctor`. Per-OS install matrix removed in favor of letting the wizard handle install.
- **16+ failure messages** across the app now consistently surface `/setup` and `/retry` as recovery paths.
- **`/setup` always runs the wizard interactively** — skips the silent fast path that `/config` would take when everything looks superficially fine, so users explicitly running `/setup` always get the wizard (rather than a no-op that lands them back at the same broken state).
- **Cloud sign-in walkthrough** — dropped the misleading "Open the sign-in page for me" button (sent users to website-only sign-in, which doesn't authenticate the local daemon). Replaced with explicit messaging that `ollama signin` is a CLI command, plus the new "Sign me in now" auto-run option.
- **Cloud sign-in heuristic** — `CheckCloudSignInAsync` now returns `Unknown` (instead of `NotSignedIn`) when the daemon is reachable but no `:cloud` tags are visible, since pulled cloud models stick around in `/api/tags` after sign-out and the heuristic can't distinguish "no cloud models yet" from "signed out". Wizard treats `Unknown` and `NotSignedIn` the same way (offer sign-in walkthrough) but with softer copy.

### Fixed
- **Trailing-slash bug on Ollama URL** — config like `http://localhost:11434/` no longer breaks model detection. Probe heals it silently; persisted to config.
- **Misleading "Couldn't start Ollama" error** — when `Process.Start("ollama", "serve")` succeeded but the configured URL didn't reach the daemon (port mismatch), the wizard previously falsely blamed the start. Now distinguishes "process didn't launch" from "process launched but URL unreachable" with an accurate hint.
- **`<Markup>` Razor component bracket markup** — the inline VDOM render block was using `[yellow]…[/]` style inside `<Markup Content="...">`, which renders as literal text (RazorConsole's `<Markup>` uses `Foreground`/`Background` props for color, not bracket parsing). Replaced with proper Foreground props so failure-state lines actually render in color.
- **`LearnContent.Display()` double-render** — was being called inside the `else if (!_isConnected)` render block, which fired on every `StateHasChanged`. Removed from the render path; `/learn` slash command still works as the explicit way to see the content.

### New & changed slash commands
| Command | Status | Description |
|---|---|---|
| `/setup` | New | Guided wizard — reconnect to Ollama or pick a different model. Always interactive. |
| `/model` | New | Quick switch — pick a different model + context size. |
| `/config` | Updated | Adjust settings — model, temperature, max tokens, timeout, ignore dirs. Description disambiguated from `/setup`. |
| `/help` | Updated | New `/setup` and `/model` rows; reorganized to group setup/model commands. |

### Files
- **New**: `Components/HomeView.razor`, `Services/OllamaSetupHelper.cs`, `Services/OnboardingFlow.cs`, `tests/OllamaSetupHelperTests.cs`, `tests/MandoCodeConfigOnboardingTests.cs`
- **Modified**: `Components/App.razor`, `Components/HelpDisplay.razor`, `Models/MandoCodeConfig.cs`, `Models/SlashCommands.cs`, `Models/LearnContent.cs`, `Models/SystemPrompts.cs`, `Services/AIService.cs`, `Services/ConfigurationWizard.cs`, `Services/MarkdownHtmlRenderer.cs`, `Program.cs`, `README.md`

### Test coverage
183/183 tests passing. New tests cover URL probe slash-heal, `BuildUrl` permutations, no-daemon failure modes, `HasCompletedOnboarding` defaults, endpoint-preservation under `ValidateAndClamp`, and `DefaultCloudModel` constant pinning.

### Breaking changes
- Configs with `MaxTokens > 200k` (i.e. saved 256k) get clamped to 200,800 on next load. Picker shows 32k highlighted next time `/config` or `/model` opens.
- Configs without an explicit `maxTokens` key now default to 32k instead of 4k. Configs with `"maxTokens": 4096` are unaffected.

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
