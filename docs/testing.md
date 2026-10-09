# Testing MandoCode

Run the full CLI suite before submitting changes:

```powershell
dotnet test tests/MandoCode.Tests/MandoCode.Tests.csproj -c Release
```

This runs both net10.0 and net8.0. A green result does not replace an interactive terminal check for resize, native clipboard access, or terminal-specific keyboard sequences.

## Test tiers

Every test class has a `Category` trait. Mixed classes are assigned to the tier needed by their most involved cases; these categories are routing aids, not a coverage measurement.

- **Unit:** deterministic service, parsing, validation, and orchestration checks with controlled dependencies.
- **Component:** component handlers, rendered output, keyboard focus, and live widget layout checks. Some cases still use private-member reflection; migrate those assertions toward observable behavior when modifying their area.
- **Integration:** tests that exercise real filesystem, process, Git, or local server behavior without a live model.
- **Compatibility:** checks for the retained terminal autocomplete adapter. Legacy paste and unused planning heuristics within otherwise active unit classes additionally carry `Behavior=Compatibility`.

Examples:

```powershell
dotnet test tests/MandoCode.Tests/MandoCode.Tests.csproj -c Release -f net10.0 --filter Category=Unit
dotnet test tests/MandoCode.Tests/MandoCode.Tests.csproj -c Release -f net10.0 --filter Category=Component
dotnet test tests/MandoCode.Tests/MandoCode.Tests.csproj -c Release -f net10.0 --filter Category=Integration
dotnet test tests/MandoCode.Tests/MandoCode.Tests.csproj -c Release -f net10.0 --filter "Category=Compatibility|Behavior=Compatibility"
```

## Renderer and timing guidance

`WidgetRendererHarness` encapsulates RazorConsole's internal renderer construction, mounting, dispatcher, and snapshot access. The explorer and agent-survival scenarios use it; other renderer suites can migrate incrementally. Keep version-sensitive framework reflection here rather than duplicating it in new tests.

Use the renderer dispatcher to inspect or change component state. Wait for an observable condition with a bounded timeout, not a fixed sleep that assumes rendering has completed. A polling timeout is a failure bound, not a performance expectation.

Git refresh feedback uses an internal delay delegate and completion task. Tests hold that delay, verify the visible message, release it, and await completion without sleeping through the production two-second timeout. Production still uses Task.Delay and the existing cancellation token.

Global Console.Out and Spectre console replacements belong to the nonparallel `TUI console routing` collection. Restore them in finally blocks. Do not disable parallelism across the entire suite to hide an isolation problem.

## Keep useful coverage

Preserve multi-turn layout, pane isolation, input ownership, cancellation, image delivery, archive recovery, and checkpoint compatibility regressions. Test repetition across service and component layers can be intentional: a correct service does not guarantee correct focus or rendering.

Prefer exact behavior assertions over checks that merely find a node name or a nonempty result. The workflow topology suite now checks the actual triage destinations and compares graph shape across different plan sizes; separate build-only cases were redundant.

Platform tests must assert the current platform's behavior or explicitly report a skip. Never return early and count an unexecuted assertion as a pass. Retained compatibility coverage should be removed together with its supported production API, after checking consumers.

## Stability and targeted mutation checks

Run repeated component checks locally:

```powershell
./scripts/verify-component-stability.ps1 -Framework net10.0 -Iterations 5
./scripts/verify-component-stability.ps1 -Framework net8.0 -Iterations 5
```

Each iteration runs in a new test process and preserves a separate TRX result under ignored `artifacts/`. A failure stops the script; hang diagnostics bound stalled runs. Passing repeated runs reduces uncertainty but does not prove the absence of intermittent failures.

The focused mutation probe requires Python and .NET:

```powershell
python scripts/verify-targeted-mutations.py
```

It temporarily breaks four behaviors: prompt visibility, replacement callback ownership, argument casing, and queued approval cancellation. It requires an actual failing test result, rather than treating a compiler failure as a detected mutation. Every source file is restored byte-for-byte in a finally block. Do not run concurrent builds, tests, or edits while this probe is operating; forcibly terminating the process can interrupt restoration. The final report is written to `artifacts/mutation-results.json`. Rebuild after running the probe because the last compiled assembly contains a mutation. These four probes are a focused confidence check, not a suite-wide mutation score.

The GitHub workflow runs the full suite and three component repetitions for each combination of Windows/Linux and net10.0/net8.0. It retains TRX results even after failure. Workflow execution requires pushing these changes; local Windows results do not establish Linux compatibility.

## Interactive terminal smoke checklist

Run against the built CLI in a real terminal, with an available Ollama model. Record the terminal application, OS, framework, model, and result. These checks remain manual:

- Launch: immediately type a prompt without clicking; verify focus and submission.
- Paste a multiline code snippet with Ctrl+V: preserve indentation and newlines, do not submit until Enter; use arrows to navigate before reaching chat-scroll boundaries.
- Open `/model`, filter, select, and cancel: ensure the prompt remains visible and usable. Try an unavailable model and confirm actionable feedback.
- Create multiple agents/workspaces; start a request or snapshot in one and switch with Alt+arrows. Background output must stay with its originating agent.
- Open a menu and press Alt+W: the highlighted agent closes; other agents keep their drafts, focus, and pending operations.
- Generate several turns with code and tool output; resize narrow and wide. Preserve turn order, keep the prompt at the bottom, and keep temporary progress outside conversation history.
- Exercise `/`, `@`, file explorer, approval menus, and Escape/Tab navigation. Ensure keyboard focus returns to the selected agent's prompt.
- Copy an image and use Alt+V with a vision model; verify actual image submission. With text-only clipboard content, report that no image is available.

Do not mark this checklist passed based only on component test results.
