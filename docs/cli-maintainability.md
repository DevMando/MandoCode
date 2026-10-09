# CLI maintenance boundaries

The CLI is composed from Razor components. `App.razor` retains layout and coordinating state; its partial class files group commands, planning, attachments, integrations, request processing, lifecycle, and terminal effects. Partial files share component state and are not independent services. Extract reusable operations into services rather than adding unrelated responsibilities to these files.

## State and input

`AgentPresentationState` owns panel visibility policies. Multiple panels may remain mounted while a nested picker owns input. Explorer and Git regions coexist with the prompt; modal panels hide it. Request execution and pending input remain separate from presentation state.

`AgentKeyboardRouter` forwards Alt/Meta shortcuts to the selected workspace before menu-specific keys. Prompt cursor navigation and local selection stay with their components.

## Agent lifetime

`App.Lifecycle.cs` resolves per-agent services from the pane scope, subscribes to events, and releases pending requests on shutdown. `CallbackRegistrations` releases a callback only if it is still owned by that registration. Event delegates must also be detached on disposal. Shared workspace services are resolved from the host; cross-agent operations resolve the target agent's scope.

## Shared engine

`AIService` continues to own mutable conversation history, tool execution, and response orchestration. `OllamaModelInspector`, `PendingImageEvidence`, `ConversationHistoryCodec`, and `ResponseLimitNotice` isolate focused operations without changing the public API used by Desktop. Model identity/version checks remain in AIService so a late inspection cannot overwrite a newer selection.

## Output

`SpinnerService` is the public activity facade. A current `TuiSession` receives widget state updates; the terminal backend owns cursor-based rendering when there is no component session. Components should use the session-aware output path. Avoid introducing direct terminal writes in component flows.

`CommandAutocomplete` remains a compatibility adapter; the active CLI selectors use component input state. Check external consumers before removing public APIs.

## Verification

Run the CLI test project on both net10.0 and net8.0. Live-renderer tests cover multiple panes, focus, model menus, agent closure, and workspace switching; boundary tests cover nested panel policies, callback replacement, and verbatim command arguments. Verify the Desktop build when shared engine interfaces change. Interactive terminal testing is still valuable for resize, cursor behavior, and platform-specific clipboard integration.
