# CLI agent panes (experimental)

## Workspaces

Run `/keybindings` for keyboard shortcuts grouped by workspace, agent, conversation, menus/input, and plan-step editing. The same shortcut labels appear in command hints and `/help`.

The top bar shows workspace names. Click a tab to switch, **+ New** to create a workspace, or **× Close** to close the
current idle workspace. Each workspace supports four agents, so two workspaces can
hold eight agents. Agents in hidden workspaces continue working and their pending
approvals remain pending until you return and choose an option.

The selected tab uses mint green with black text. All, New, and Close stay on the right
when the tab list grows. Use **‹ / ›** to browse tab pages without switching agents;
selecting a workspace brings its tab into view. Long names are shortened with **…**.

**All** opens a searchable workspace picker: type a name or tab number, use Up/Down,
and press Enter to switch or Escape to return. Click All again to close the picker.
`/workspace-all` opens the same picker; Alt+Shift+A toggles it.
Narrow terminals use **WS**, **≡** (All),
**+** (New), and **×** (Close) to leave more room for tabs.

- `/workspace-new [name]` opens a workspace with one fresh agent, inheriting the selected agent's model settings.
- `/workspace` cycles tabs; `/workspace <name or number>` selects one directly.
- `/workspace-all` opens the searchable All workspaces picker (Alt+Shift+A toggles it).
- `/workspace-rename <name>` changes the current tab's name.
- `/workspace-close` closes the current workspace only when every agent is idle (Alt+Shift+W). One workspace stays open.
- Alt+Shift+N creates a workspace; Alt+Shift+Left/Right switches workspaces.

Switching preserves the selected agent, conversations, unfinished prompt and plan-edit
text, scroll position, and approval selection. Workspace tabs and extra agent conversations
are kept in memory for this application run; they are not restored after restarting.
All workspaces initially share the same project folder and saved configuration file.

## Agent panes

New agents default to Desktop-style callsigns. Use `/config` → **Agent naming** or `/config set agentNaming numbers` to use numbered labels; `/config set agentNaming names` restores callsigns. This saved preference applies to new agents across workspaces; existing agents keep their names. Numbered labels reuse the lowest free number, independently of internal agent IDs.

The **AGENTS** bar directly below **WORKSPACES** shows the current workspace's agents and their model names.
Click an agent tab to focus its pane; **+ New** opens another agent and **× Close** closes
the selected idle agent. The selected tab uses a dark purple (#503765) highlight with light text, distinct from workspace tabs using dark green (#325039) with a lighter hover.
New and Close stay on the right, with compact controls and tab arrows in narrow terminals.
Switching workspaces updates this bar to that workspace's agents while preserving the pane layout.

Use `/agent-rename <name>` to rename the selected idle agent. Names may contain spaces, retain their casing, and must be unique across open workspaces. Future replies and the system prompt use the new identity; existing conversation history is preserved.

Run `/agent-new` or choose **+ New** in the AGENTS bar to open another agent.
The new agent starts with the selected agent's model settings and an empty conversation.
Both agents can work concurrently; each has separate prompts, token statistics, approvals,
plans, and cancellation. They initially share the same project folder.

Select an agent's header or use `/agent-focus` to switch agents. The active pane has a
green border. `/agent-focus left` and `/agent-focus right` choose a direction.
Use **Close** or `/agent-close` to close an idle pane. Cancel a running request with
Escape first. The last pane stays open; `/exit` exits the application.

One agent uses the full window. Two agents sit side by side. Opening a third keeps
the first agent full height on the left and stacks Agents 2 and 3 on the right.
A fourth agent fills the lower left, producing a 2×2 grid. When a grid pane closes,
the lone agent in its column expands to full height without moving live components.
Closing a pane leaves its slot available for the next agent, preserving the remaining
agents' conversations. Two remaining agents return to a side-by-side view regardless
of their original slots. A lone remaining agent expands to the full window.
Alt+Up/Down moves within a column; Alt+Left/Right moves to the nearest pane in the other column.
`/agent-focus` cycles through all agents, and `/agent-focus up` or `down` moves vertically.

Alt+N opens an agent, Alt+Arrow Keys switches focus, and Alt+W closes the selected
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
