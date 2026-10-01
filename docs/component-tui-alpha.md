# Component TUI alpha: RazorConsole 0.6

The CLI engine is based on v0.15.2, labeled 0.16.0-alpha, and uses RazorConsole.Core 0.6.0.

## Current layout

- ChatShell owns the full terminal widget layout.
- ConversationView scrolls the banner, home information, messages, code snippets, and tool output.
- UserPrompt is a stable keyed transcript child created on submission, ahead of response and tool entries.
- AgentStatus renders bounded activity, spinner, and preview rows above the composer. Its transient children disappear when the operation stops.
- PromptInput uses TextArea and component-managed slash/file suggestions.
- /model opens a bounded popup above the same prompt: type to filter, Up/Down to navigate, Enter or Tab to select, Escape to cancel. The prompt keeps keyboard focus; choosing a model does not submit a chat turn.
- Page Up / Page Down scroll the conversation while the prompt has focus. Ctrl+End returns to the bottom.
- Mouse wheel scrolling is supported in the conversation. Following output pauses above the bottom.
- Escape cancels a running direct request through the prompt keyboard path.
- Approvals and instruction requests take the input region while active.
- Exiting through /exit stops the host so RazorConsole can restore the terminal.

TuiSession stores ordered rich renderables. TuiConsole routes legacy Spectre output into that state, and TuiTranscriptWriter collects remaining plain stdout. Tool ANSI is parsed into styled text instead of replaying cursor-control sequences. This bridge lets the primary chat flow use one canvas while smaller features are migrated.

## Automated checks

TuiLayoutTests render the real Razor shell, then translate it through RazorConsole's widget engine. Checks cover two turns, response ordering, code blocks, tool results, transient previews, long conversations, and 100x32, 50x16, 32x12, and 20x10 viewports. Multi-turn checks assert the composer border remains on the bottom row during long and shrinking previews and after completion. A continuously mounted shell is checked across multiple state updates.

PromptComposerStateTests cover accepting commands without accidental submission, Escape, and file completion in the middle of a message.

TuiOutputRoutingTests also exercise the compiled App response method for two turns. They assert rich responses join session state, render in order above the composer, and never write to the physical console. Razor needs a namespace-local AnsiConsole alias in _Imports.razor: a Spectre namespace import can shadow the compilation-unit global alias and bypass transcript routing.

These are component and output-routing checks, not a substitute for a live terminal/PTY session.

## Manual test pass

Stop any running CLI/debug session and rebuild before testing.

1. Exchange several greetings; confirm old replies stay ordered above the input.
2. Request a code snippet and a file read in a disposable project; inspect formatting and tool-result order.
3. Watch a longer response: the status preview should clear at completion, with one final response in the conversation.
4. Scroll up during a response; it should retain your position. Return to the bottom with Ctrl+End.
5. Resize the terminal while idle and while processing.
6. Type /he, navigate suggestions, accept with Tab or Enter, then press Enter again to execute.
7. Type an @file fragment in the middle of a message; accepting it should preserve the following text. Test directory drill-down and Escape.
8. Exercise write/command approval in a disposable project, including the instruction option.
9. Cancel a running request with Escape; start another turn.
10. Exit with /exit; confirm the original terminal screen and mouse behavior return.

## Remaining migrations

Some settings, onboarding, learning, and recovery pickers still use blocking Spectre prompts/status displays. Those are not yet compatible with the full component canvas and need their own component migration. Music playback remains available, but the old cursor-positioned music visualizer is suppressed in component mode. Other cursor-driven easter eggs and subprocess interactive input also need review.

Autocomplete is currently an inline component popup above the input. An anchored overlay can follow after the base layout and keyboard behavior are stable.
