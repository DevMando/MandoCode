# Plan execution modes

The workflow runner is used in both modes. The default, including for existing
configuration files, is straightforward execution with recovery and a final quality
phase on newly proposed implementation plans.

| Behavior | Straightforward (default) | Strict (opt-in) |
| --- | --- | --- |
| Ordinary step completion | Executor returns without a reported failure | Independent model verdict required |
| Executor acceptance checks | Yes | Yes |
| Checkpoints and targeted repair | Yes | Yes |
| Final test-and-repair phase | Yes | Yes |
| Extra review for explicitly generated/revised proposals | No | Yes |
| Evidence-only verifier follow-up | No | One bounded follow-up when applicable |

Use `/config set strictPlanVerification true` to enable strict mode, or
`/config set strictPlanVerification false` to return to straightforward mode.
The setting is saved as `strictPlanVerification` and read for subsequent attempts.
Changing it does not interrupt a model call already running. The `planner` and
`plannerEngine` keys do not switch back to the old execution engine.

Strict mode supports tool, schema, and plain JSON verdict formats. Truncated or
ambiguous responses cannot pass a step. Verification failures preserve execution
evidence rather than rerunning implementation merely to obtain a verdict.

After relaunching, `/plan-resume` can continue a saved plan. Matching execution evidence
pending only verification can advance in straightforward mode without repeating the
implementation. Explicit executor failures still require repair. Final quality phases
also require an explicit successful outcome and no recorded unresolved test failures.

Neither mode guarantees correctness. Test coverage and accurate executor reports
still matter. See [Task Planner](TaskPlanner.md) for the final phase and recovery rules.
