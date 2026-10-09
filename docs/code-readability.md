# Readable code and comments

Code should make its intent and execution order visible to the next maintainer. Prefer a few clear statements over a long expression that mixes navigation, state changes, and output.

## Comments are part of the design

Keep comments that explain invariants, ownership, ordering, compatibility, and failure recovery. Examples include why a cancellation callback runs outside a lock, why a rendering failure still completes a waiting request, and why failed model calls retain completed tool evidence.

Update comments when implementation changes. Describe the current agent, history, and tool behavior rather than referencing an obsolete kernel, a private memory file, or a migration phase. Historical explanations remain useful when they describe a supported file format or compatibility requirement.

Use XML documentation for API contracts and focused comments near the constraint they explain. Avoid narrating obvious assignments or asserting that a method has no callers without a durable reason to keep that claim current.

## Structure and naming

- Separate state decisions, side effects, rendering, and completion handling into visible steps.
- Give nested mode decisions named methods or switch expressions. Preserve the precedence of cancellation, confirmation, and nested-picker behavior.
- Name intermediate values by their meaning, such as rowStart, cellOffset, and succeeded.
- Wrap substantial Razor declarations with one parameter per line. Group lifecycle setup by service resolution, callback ownership, and subscriptions.
- Keep extracted methods cohesive. Formatting alone does not resolve an oversized component's responsibilities, and a helper for every statement adds more navigation than clarity.

Readability edits should preserve behavior. Use the existing component and service regressions to verify that focus, draft state, output ordering, and cancellation still work.
