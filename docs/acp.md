# ACP agents (Agent Client Protocol)

MandoCode can run an external coding agent as a child process and talk to it over the
[Agent Client Protocol](https://agentclientprotocol.com) (ACP v1, JSON-RPC 2.0 over stdio, NDJSON).
Authentication is the agent's own business; MandoCode never handles agent tokens.

The protocol layer lives in `src/MandoCode.Acp` (no UI dependencies, no third-party ACP libraries).
Schema: pinned to the release tag `v1.16.0` of `agentclientprotocol/agent-client-protocol` (checked to exist), vendored in
`tests/MandoCode.Tests/Acp/schema/schema.v1.json`. The tag is the schema release; the protocol version is still 1
(`protocolVersion: 1` in `initialize`).

## Quick start

For Codex, see [Codex adapter setup and validation](acp-codex.md), or run `/agent-acp add codex` for setup guidance.

1. Install an ACP adapter yourself, at a pinned version, for example for Claude Code the adapter validated below. No `npx`.
2. Add it (your own paths): `/agent-acp add claude node "C:\path\to\adapter\dist\index.js"`, or edit `acpAgents` in
   `~/.mandocode/config.json`.
3. Check it: `/agent-acp test claude` (or `mandocode --acp-probe claude`). You are asked to confirm the command first.
4. Use it: `/agent-acp claude` switches the current pane to the agent (`--new-panel` opens it in a new pane instead).
   Sign in to the agent with its own tooling beforehand (`claude auth login`). `/agent-acp stop` returns the pane to
   MandoCode's own agent.

Status of this first version: phases 1–2, plus switching a pane in place (originally planned for a later phase, and
without passing context: see "Deviations"). Not included: `terminal/*`, images, resuming sessions, handing a
conversation between the native agent and an ACP agent, asking an ACP agent with `@`, a settings tab for agents.
Open questions are marked **pending** below.

**Security model in one paragraph.** MandoCode does not sandbox the agent. It decides what program runs (no shell,
real executables only, confirmed by hash, configured only in the user's own config) and what environment it gets
(no `ANTHROPIC_API_KEY` by default), and it relays the agent's permission questions; the agent applies the answers
itself. Treat an ACP agent as a program with your user's rights.

## Launch rules

An agent command is code that runs with your permissions, so launching is deliberately strict:

- No shell: `ProcessStartInfo` with `UseShellExecute = false` and `ArgumentList`.
  `cmd.exe`, `powershell`, `sh -c` and friends are refused.
- The command must resolve (through `PATH`, or as an absolute path) to a real executable:
  `.exe` on Windows, ELF/Mach-O on Unix. `.cmd`, `.bat`, `.ps1` and `#!` scripts are refused:
  *specify the path to an .exe or run the adapter as `node <script>`*.
- Node adapters: `command = "node"`, `args = ["<absolute path>/dist/index.js", ...]`.
  Install the adapter yourself with a pinned version (`npm install -g <pkg>@<version>`).
- No `npx`, `npm exec`, `pnpm dlx`, `bunx`, `uvx`, `pipx`: nothing is downloaded at launch time.
- The resolved absolute path is what gets executed (and shown), so `PATH` cannot change it afterwards.
- `ANTHROPIC_API_KEY` is removed from the agent's environment unless explicitly allowed.
- The agent must write only protocol to stdout. stderr is captured (never printed) and shown as a tail on failure.
- Shutdown closes stdin, waits two seconds, then kills the whole process tree.

## Configuring agents

Agents live under `acpAgents` in `~/.mandocode/config.json` and are read **only** from that file (a config
loaded from any other path has its `acpAgents` dropped with a warning), so a project cannot add a program to run.

```json
{
  "acpAgents": {
    "myagent": {
      "command": "node",
      "args": ["C:/tools/adapter/dist/index.js"],
      "env": { "SOME_VAR": "value" },
      "disabled": false,
      "passAnthropicApiKey": false,
      "startupTimeoutSeconds": 30,
      "authHint": "run the agent's own login command"
    }
  }
}
```

Names are case-insensitive. Text commands (no settings tab):

```
/agent-acp [list]
/agent-acp add <name> <command> [args...]     (double quotes group an argument; unsafe commands are refused)
/agent-acp test <name>                         (initialize + session/new, then shut down)
/agent-acp remove <name>
/agent-acp enable <name> | disable <name>
```

`/agent-acp add claude` (no command) only prints how to set it up: there is no Claude Code preset yet because the
adapter, its pinned version and the way it finds the Claude Code binary have not been validated (see below).
`startMode` (optional, per agent) is the session mode to switch to right after `session/new`; see "The ACP pane".

## Trust

Before an agent runs for the first time MandoCode shows exactly what will start: the resolved absolute program,
every argument on its own line, the working directory, the *names* (not values) of extra environment variables, and
whether `ANTHROPIC_API_KEY` is passed. After you approve, a SHA-256 of `{resolved path, args, env, passAnthropicApiKey}`
is stored under the agent's name in `~/.mandocode/acp-trust.json` (separate from config.json, so editing the config
cannot grant trust). Any change to those values, or a different program resolving from `PATH`, asks again.
The working directory, `disabled`, timeout and hint are not part of the hash. The contents of an adapter script are
not hashed. `remove` and `add` clear the stored trust for that name.

Every way of starting an agent (`/agent-acp test`, `mandocode --acp-probe`) goes through the same resolver and the
same trust check; there is no flag or environment variable that skips the question.

## Probing an agent

```
mandocode --acp-probe [--cwd <dir>] <name>
```

Runs the configured agent `<name>` through the trust question (a terminal y/N), then `initialize` and `session/new`
in the working directory, and reports the agent, protocol version, capabilities, auth methods and startup time. On
failure it prints the exit code and the stderr tail. Exit codes: `0` ok, `1` failed, `2` usage or unknown agent,
`3` launch not approved. In a non-interactive shell an unapproved agent is simply declined.

## The ACP pane

Use `/agent-acp mode` to pick from the connected agent's advertised permission and behavior modes, or `/agent-acp mode <name>` to select one directly. The menu marks the current mode and shows descriptions when supplied. It prefers the agent's mode configuration option and falls back to `session/set_mode` for agents exposing session modes only. Changing modes keeps the conversation and is refused during active work. Unsupported agents receive an explanation; MandoCode does not invent modes or enforce the external agent's permissions.

`/agent-acp <name>` switches the current pane to the agent; `/agent-acp <name> --new-panel` opens it in a new pane
instead (the other panes are unchanged; at most four panes exist, and a fifth is refused with a message). A pane that
runs an ACP agent does not touch Ollama, models or MCP: it runs the trust question, starts the agent, runs `initialize`
and `session/new`, and then every message you type goes to the agent as a prompt. The agent lives in the pane's service
scope, so closing the pane ends its whole process tree. `/agent-acp restart` (inside the pane) starts it again.

### Switching a pane

- The switch happens only when the pane is idle (no request running, no plan or approval menu open); otherwise it is
  refused with an explanation (Escape cancels a running request, or use `--new-panel`).
- It is a fresh start. The pane's native session (conversation, model, MCP servers) stays alive underneath and is not
  touched, and nothing is passed to the agent or back. A marker line in the transcript shows each switch.
- `/agent-acp stop` ends the ACP session (the agent's process tree is killed) and gives the pane back to the native
  agent; in a native pane it just says so. `/agent-acp <other>` inside an ACP pane closes the current agent first.
- ACP turns are never saved to the agent history, so `/history` cannot restore one as a native agent; the native
  conversation of a switched pane is still archived as usual. The header, the command list (native-only commands are
  hidden) and the input loop follow the pane's current mode.
- A pane opened with `--new-panel` has never started its native side; `/agent-acp stop` starts it then (connection
  check, model validation, MCP), exactly as a new pane would.
- A new pane starts in the folder MandoCode was launched in, not in the folder the current pane was moved to. This is
  the same for `/agent-new` (native) and `--new-panel`; a pane's folder is changed per pane afterwards (see
  "Changing the folder").

- Header: `ACP · <adapter name and version> · mode: <current mode> · <project folder>` (the pane name is added when
  there is more than one pane).
- Streaming: the reply shows as a live preview; it is rendered as markdown when the agent starts a tool call and when
  the turn ends. Thoughts show only as a dim "Thinking…" activity line. Unknown updates and fields are ignored.
- Escape cancels the turn (`session/cancel`). If the agent has not ended the turn 10 seconds later it is shut down
  (process tree killed) and the pane tells you to `/agent-acp restart`.
- `@path` in a message adds a `resource_link` for an existing file inside the project (images are not sent yet).
  Mentions are read with the same parser as the native pane, so `@"src/My Component.cs"` works. A mention is only
  linked when the file is inside the project folder: `..` escapes, absolute paths outside it, and links or junctions
  that lead out stay plain text.
- The `@` picker lists files only. MandoCode's own agents are not reachable from an external agent (that needs a
  bridge, which is a separate phase together with passing context). A hand-typed `@Runner` is not blocked, it is just text.
- Connecting happens in the background: the pane prints "Connecting to <name>...", shows `connecting…` in the header and
  takes input at once. A message typed meanwhile waits until the agent is ready (Escape cancels the wait). If the
  start fails, whatever the reason, the pane shows the message and stays usable: `/agent-acp restart` tries again.
- `terminal/*` is never offered.

### What an ACP pane does not offer

One set in the code (`AcpPaneRestrictions`) feeds autocomplete, the command dispatcher, the Alt shortcuts and the
`@` picker, so a restriction cannot be hidden in one place and reachable in another.

| Unavailable | Why |
|---|---|
| `/setup`, `/retry`, `/config`, `/agent-settings` (Alt+S), `/force-skill`, `/plan`, `/plan-resume`, `/plan-discard`, `/context-snap-create`, `/context-snap-import` (Alt+C), `/ollama-*` | The external agent runs the conversation; MandoCode's own model loop, plans and context snapshots do not apply. |
| `/skills` (Alt+K), `/mcp` (Alt+M), `/mcp tools`, `/mcp-reload` | The agent does not use MandoCode's skills or MCP servers (`mcpServers` is empty). |
| `/history` (Alt+H) | Saved history restores native agents, and ACP panes are not saved to it. |
| Alt+V (paste image) | Images are not sent to ACP agents yet. |
| Agents group of the `@` picker | See above. |

Typing a blocked command, or pressing its shortcut, prints `/<command> is not available in an ACP agent pane: <why>`.

The `/` popup lists MandoCode's own commands only (minus the blocked ones). The agent's own commands (the
`available_commands_update` list, for example `/init` or `/review`) are **not** listed and are **not forwarded** to
the agent as prompts: typing one answers `Unknown command`. The only agent command MandoCode uses is `compact`, behind
`/compact`. So `/model` and `/compact` appear in the popup only when the agent offers a model option or a `compact`
command, and `/clear`, `/model` and `/compact` are described for the ACP pane ("Start a new ACP conversation in this
agent…"), not with the native text. Forwarding the agent's other commands would be a separate change.

In the popup, the first Enter on a `/command` accepts the highlighted suggestion and a second Enter sends it. This is
how the input works in every pane, native or ACP.
Everything else (`/help`, `/copy`, `/copy-code`, `/transcript-save`, workspace and agent-pane commands, the file
explorer, Git changes, `/command` and `!cmd`) works as in a native pane.

### `/clear`

A new ACP conversation in the same pane, agent process and folder: if the agent offers `session/close` the current
session is closed, then `session/new` runs in the same process. The pane's ACP messages and their transcript are
cleared, the agent's start mode applies again, and the permissions the agent kept for the old session are gone with
it. The message says it is the ACP conversation that was cleared; **the native conversation and its history are not
touched**. While a request is running `/clear` is refused (press Escape first). The agent's own commands are not forwarded.

### `/model` and `/compact`

- `/model` reads the agent's `configOptions` (category `model`) from `session/new` and lets you pick a value, or
  `/model <value or name>` sets it directly (`session/set_config_option`). An agent that offers no model option gets a
  plain message. Nothing is adapter specific.
- `/compact` sends `/compact` to the agent as a prompt when it announced a `compact` command
  (`available_commands_update`), otherwise it says the agent does not support it. No other agent command is forwarded.

### Changing the folder

The folder belongs to the pane (each pane has its own `ProjectRootAccessor`), not to the application. The agent was
started in the old folder, so `/change-directory`, Alt+D, the Change Directory button and `!cd` / `! cd` ask first:
"the ACP agent restarts there and the ACP conversation is lost", then restart the agent (new process, new session,
file access rooted in the new folder). The trust hash does not contain the folder, so there is no new trust question.
`chdir`, `pushd`, `Set-Location` and `sl` only run in a child process and never changed the folder.

### Sessions

When the agent advertises the capabilities, MandoCode remembers the last session of each agent for each folder in
`~/.mandocode/acp-sessions.json` (ids, folder and a timestamp only, never conversation content; only when persistence is on).

- `/agent-acp <name> --resume` (also with `--new-panel`) continues that session: `session/resume` when the agent
  offers it (nothing is replayed, the agent keeps its context), otherwise `session/load`, whose replayed history is
  shown and kept in the pane's messages. If nothing is remembered, the session is unknown to the agent, or its folder
  differs, a new session starts and the pane says so.
- `/agent-acp sessions` lists the agent's sessions for this folder (`session/list`; sessions of other folders are never shown).
- `/clear` replaces the remembered id with the new session's id. `/history` and Alt+H stay blocked.

### Tool calls, plans and diffs

- `tool_call` / `tool_call_update`: one line per call (`<icon by kind> <title> · <path>:<line>`) and one line for the
  final status (`completed`, or `failed` with the first line of the agent's message). State is kept by `toolCallId`, so
  an update that carries only a status works. `terminal` content is shown as "terminal output not supported".
- `plan`: the whole checklist is replaced on every update; the current one is printed when the turn ends, and the
  activity line shows `Plan done/total` meanwhile.
- Diffs (`diff` content of a tool call, and files written through `fs/write_text_file`) go through the same renderer as
  MandoCode's own edits: new files as a write with a preview, existing files as an inline diff. The same change is
  shown once even if the agent reports it both ways.
- Unknown update kinds and content types are ignored.

### Writes that are not decided yet

A call that changes things (edit, delete, move, execute) shows `Awaiting permission…` while it is `pending` and
`Writing…` / `Running…` while it is `in_progress`. The proposed diff is shown only when the call completes; a call that
fails, or that you refuse, never shows a write. A refusal is shown at once as `permission denied`. The detail after
`failed:` is the first real line of the agent's message (code fences around it are dropped); with none, only `failed`.

### Permissions

`session/request_permission` opens a menu with the agent's options exactly as it sent them (allow options green, reject
options red; equal names get a number). The text above it names the tool call, its locations and the agent's current
mode, and says that **the agent applies the permission itself**: MandoCode only passes the answer on. The answer
is the chosen `optionId`. Escape, closing the menu or cancelling the turn answers `cancelled`. A proposed diff is shown
as a scrollable preview (PgUp/PgDn). MandoCode's own approval settings (`EnableDiffApprovals`, write bypass) do not
apply to external agents.

### File access (`fs/*`)

The ACP pane advertises `fs.readTextFile` and `fs.writeTextFile` and answers them itself, confined to the project folder:

- the path must be absolute; it is checked with `FileSystemPlugin.ResolvePath` and refused when outside the folder;
- the path is then canonicalised: a symlink or junction on the final target **or on any parent directory** that leads
  outside the folder is refused (a link that stays inside is fine);
- reads accept `line` (1-based) and `limit`; a whole file over 5 MB is refused, a range is capped at 5 MB of text;
- writes are atomic (temp file + replace), directories are created only inside the folder, and the change is shown as a diff.

The check and the use are separate steps, so a link swapped in between by another process is not covered.

**Observed with `@agentclientprotocol/claude-agent-acp` 0.103.0** (message trace, 2026-11-11; scenarios: create a
file, read the README, mode `default`): the adapter does **not** call `fs/read_text_file`, `fs/write_text_file` or
`terminal/*`. It changes files itself, so this guard does not apply to the agent's own writes: what protects the
project is the `default` mode and the permission questions, not the project-folder boundary. The handlers stay for
agents that do call `fs/*`; `initialize` advertises them (218 bytes with them, 220 without, as in the trace). Other
scenarios (for example other modes) were not checked.

### Session mode

Some agents start in a permissive mode (the Claude Code adapter started in `auto` during validation). After
`session/new`, if the current mode is not `default` and the agent offers `default`, MandoCode calls
`session/set_mode("default")`. Set `startMode` on the agent entry to choose another mode. A refused or failed switch is
only a warning in the transcript; the session keeps working in the mode it has. This is a deviation from the first
plan, which did not call `session/set_mode` in this phase.

### Errors

| Situation | What you see |
|---|---|
| Command not found / refused (`.cmd`, `npx`, shell) | `Cannot start ACP agent '<name>': <reason>` and where to fix it |
| Launch not approved, or the entry changed | not started; `/agent-acp restart` asks again |
| Agent exits during startup | exit code, last 20 lines of stderr, path of the full stderr log |
| `initialize` / `session/new` timeout | "did not complete '<step>' within N seconds", stderr tail, "may not speak ACP" hint; process tree killed |
| Not signed in | the entry's `authHint` (default: `claude auth login`) |
| Process dies mid-turn | partial text is kept, exit code and stderr tail shown; `/agent-acp restart` |
| Agent ignores a cancel | message after 10 s, process tree killed |
| Non-protocol output on stdout | "wrote non-protocol output to stdout" |
| Stop reason `refusal`, `max_tokens`, `max_turn_requests` | a sentence for each; unknown stop reasons are reported, not fatal |

Recognising "not signed in" is **pending**: with a valid Claude Code login `session/new` succeeds, so the real error
for a missing login has not been observed. JSON-RPC `-32000` is treated as an auth error; any other error from
`session/new` or `session/prompt` whose text reads like authentication (auth, login, sign in, credentials, API key,
401, ...) is shown with the sign-in hint worded as a possibility ("This looks like a sign-in problem"). Stderr of every
agent goes to `~/.mandocode/logs/acp/<agent>-<date>.log` (rotated), never to the console.

## Debugging: message trace and status

`/agent-acp status [name]` lists each agent with its stderr log and trace log paths (and whether they exist yet).

To see what a real agent calls (for example whether it uses `fs/read_text_file` / `fs/write_text_file`), set
`"trace": true` on the agent entry in `~/.mandocode/config.json`, or set `MANDOCODE_ACP_TRACE=1` for all agents, and
restart the agent. One line per message goes to `~/.mandocode/logs/acp/<agent>-<date>.trace.log`:

```
2026-11-11T10:00:00.123Z <- request fs/read_text_file id=7 bytes=161
2026-11-11T10:00:00.130Z -> response fs/read_text_file id=7 bytes=420
```

Only direction, kind, method, id, time and size are written, never paths, prompts, file contents or any other content.
The trace is off by default, is not part of the trust hash, and the file is created by the first traced message.

## API notes

`AcpSession.Prompt(...)` keeps its name instead of `PromptAsync`: it returns an `AcpTurn` immediately (updates stream
from `turn.Updates`, the end arrives on `turn.Completion`), not a `Task`, so an `Async` suffix would mislead.

## Client behaviour

- Advertised client capabilities: `fs.readTextFile` and `fs.writeTextFile` (when the host provides a handler);
  `terminal` is not advertised and `terminal/*` requests get `-32601`.
- `session/new` is sent with `mcpServers: []`.
- Unknown fields, unknown `session/update` kinds, unknown content types and unknown stop reasons are ignored, never fatal.
- Cancelling answers pending permission requests with `cancelled`, sends `session/cancel`, and waits for the turn
  to end (default 10 s). An agent that ignores the cancel is reported as unresponsive so the caller can kill it.
- "Not signed in" (JSON-RPC `-32000` on `session/new` or `session/prompt`) is reported as such; `authenticate`
  is not called. Sign in with the agent's own tooling.

## Validation with Claude Code (manual, 2026-11-10)

Environment: Windows, MandoCode feat/acp-client, .NET SDK 10.x.

Findings
- The native `claude.exe` (installed under the user's profile) has no
  ACP mode: `claude --help` lists no ACP flag. Starting it directly
  as an ACP agent makes `initialize` time out (the CLI waits for
  interactive input).
- An adapter is required. Used: @agentclientprotocol/claude-agent-acp
  0.103.0 (installed into a separate folder with a pinned version and
  started as `node <path>\dist\index.js`; no npx).
  Older names (@zed-industries/claude-code-acp,
  @zed-industries/claude-agent-acp) are superseded.
- initialize: ok (~450 ms), ACP protocol v1.
- Agent capabilities: loadSession=true; prompt: image=true,
  audio=false, embeddedContext=true.
- authMethods: none advertised. session/new succeeded without an
  explicit authenticate step using the existing Claude Code login
  (stderr reported apiType=native, baseUrl=native).
- session/new (~1.5 s) reports modes: default, acceptEdits, plan,
  auto, bypassPermissions; the initial mode was `auto`.
- stderr is verbose (session/create phase timings), so it goes to a
  log file; only the tail is shown on failures.
- ANTHROPIC_API_KEY was removed from the agent's environment by
  default.

Decisions
- MandoCode sets the session mode to `default` after session/new
  unless the agent entry specifies `startMode` (deviation from the
  initial plan: session/set_mode is called in phase 2).
- Agents are started without a shell (no cmd/npx), with a resolved
  absolute path; the launch is confirmed through the trust dialog.

Observed in later manual runs (adapter 0.103.0, 2026-11-11)
- Methods seen in the trace: initialize, session/new, session/set_mode, session/prompt, session/update,
  session/request_permission, and the extension notification `_auth/status_update`. No fs/*, no terminal/*.
- `_auth/status_update` is a vendor extension (method starts with `_`). It is ignored. Its content is only an
  observation, with no commitment to its format: `MANDOCODE_ACP_DEBUG_EXT=1` shows it in the terminal for a look (it may
  carry account details, so it is never written to a log or to the trace).
- Permission questions, diffs, refusal, cancel, closing the pane and the repeat-stability script were checked by hand.

Pending / unverified
- The exact error returned when Claude Code is not signed in.
- stopReason values observed on cancel.
- Whether usage through the adapter counts against the Claude
  subscription limits or the separate Agent SDK credit: not verified;
  check in the account usage page after real prompts.
- Windows behavior of tree-kill for node -> claude after cancel/exit.

Deviations from the plan
- Prompt (not PromptAsync): the method returns an AcpTurn directly.
- --acp-probe takes a configured agent name and goes through the same
  resolver and trust check as /agent-acp test.
- session/set_mode is called after session/new (see "Session mode").
- enable/disable are implemented although the first plan deferred them.
- Switching the current pane to an ACP agent was planned for a later phase (with handoff of context); it is here,
  as a "start fresh" switch without passing any context in either direction.
- `/agent-acp status`, the message trace and the extension dump are extras for debugging an agent.

Known limitations
- Markdown links in an agent's reply are shown as raw text (the transcript renderer does not turn them into links).
- Images are not sent; `terminal/*` is not offered.
- Agents that cannot resume or load sessions always start fresh, and `/model` needs the agent to expose a model option.
- Switching the folder (or `--resume`) affects one pane; restarting all ACP panes at once is a separate change.
- MandoCode's agents are not reachable from an ACP agent, and no context is handed over between the two (a later phase).
- Agent-initiated file changes are not confined to the project folder (see "File access").
