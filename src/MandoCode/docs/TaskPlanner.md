# Task Planner

The model proposes ordered steps with descriptions, executable instructions, and
acceptance checks. `/plan <request>` explicitly generates a proposal; the agent may
also propose one during a conversation. The host shows the plan for review before
execution. Both Desktop and CLI use the workflow runner.

## Default flow

1. Generate a proposal grounded in a bounded repository snapshot.
2. Add or consolidate a final **Test and repair the finished result** phase for
   plans identified as implementation work.
3. Review and approve the plan.
4. Execute each step, save its result, and advance. The executor owns its checks.
5. Run the final quality phase before completing the plan.

Straightforward execution is the default. Ordinary steps do not wait for a second
model verdict. Reported failures and execution exceptions enter recovery; completed
steps and saved evidence are retained. A normal return is not independent proof of
correctness, so the final quality phase supplies reusable tests and an honest report.

## Final quality phase

The executor reuses existing tests, adds small smoke tests where needed, and repairs
concrete defects without expanding scope. It reruns affected checks and retains test
files. Non-code deliverables receive appropriate content/consistency checks.

The phase must explicitly report success. An incomplete response gets one continuation
within that execution attempt; a second incomplete response enters recovery with work
saved. Recognized test commands with nonzero exit codes stay unresolved across attempts
until the same command returns zero. This is conservative command matching, not a
general-purpose test framework: scripts must return meaningful exit codes. Renaming a
failed check does not resolve it.

The executor reports actual commands/results, browser observations, repairs, and
untested outcomes separately. Preview opening, syntax checks, and mocked-browser tests
do not establish a full browser playthrough or absence of browser console errors.

Implementation detection is a wording heuristic, with common read-only and negated
instructions excluded. Matching model-proposed final testing steps are consolidated,
preserving their checks. The host phase uses explicit metadata so editing its text
does not remove its completion safeguards. Older checkpoints retain compatibility.

## Recovery and resume

Failures receive a targeted repair instruction with prior evidence. Existing tool
budgets, continuation limits, and bounded workflow recovery apply; there is no separate
hard limit on individual test-harness edits. Repeated unsuccessful recovery pauses the
plan. `/plan-resume` resumes saved work; `/plan-discard` discards the saved plan.

Interrupted attempts without a finished result inspect existing work on retry.
Completed steps are skipped. Arbitrary shell operations are not guaranteed exactly-once.
Previously saved plans do not automatically receive a new final phase.

See [execution modes](PlannerExecution.md) for optional strict verification.
