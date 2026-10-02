# CLI agent panes (experimental)

Run `/new-agent` or choose **+ New** in the agent header to open another agent.
The new agent starts with the selected agent's model settings and an empty conversation.
Both agents can work concurrently; each has separate prompts, token statistics, approvals,
plans, and cancellation. They initially share the same project folder.

Select an agent's header or use `/focus-agent` to switch agents. The active pane has a
green border. `/focus-agent left` and `/focus-agent right` choose a direction.
Use **Close** or `/close-agent` to close an idle pane. Cancel a running request with
Escape first. The last pane stays open; `/exit` exits the application.

One agent uses the full window. Two agents sit side by side. Opening a third keeps
the first agent full height on the left and stacks Agents 2 and 3 on the right.
A fourth agent fills the lower left, producing a 2×2 grid. When a grid pane closes,
the lone agent in its column expands to full height without moving live components.
Closing a pane leaves its slot available for the next agent, preserving the remaining
agents' conversations. Two remaining agents return to a side-by-side view regardless
of their original slots. A lone remaining agent expands to the full window.
Alt+Up/Down moves within a column; Alt+Left/Right moves to the nearest pane in the other column.
`/focus-agent` cycles through all agents, and `/focus-agent up` or `down` moves vertically.

Alt+N opens an agent, Alt+arrows switches focus, and Alt+W closes the selected
pane. Windows/Command variants are also accepted when a terminal forwards them,
but Windows+N normally opens notifications and Windows+arrows controls windows.
Operating-system and terminal shortcuts can intercept keys; the commands and headers
remain available.

This first version supports four panes. A wide terminal is recommended. Each agent's
in-memory model settings are independent; settings commands still save to the shared
configuration file. The primary agent retains the existing conversation resume behavior;
extra agents start fresh and do not overwrite its saved conversation. Extra agents use
separate plan checkpoint identities. Restart recovery of those extra panes is not yet exposed.
File changes affect the shared project, so coordinate agents editing the same files.

Validation covers scoped service lifetimes, concurrent output routing (including stdout
and spinners), unique transcript identifiers, pane commands and shortcut dispatch,
busy-pane protection, terminal rendering for one through four agents, and preserving
existing agents when the grid expands or a closed slot is reused. Incremental-renderer
checks exercise closing Agent 1 with two, three, or four agents, including App initialization
and disposal. Live terminal testing is still needed for focus, mouse controls, terminal
shortcuts, resizing, and concurrent tool approvals.
