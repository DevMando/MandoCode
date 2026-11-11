# ACP agents (Agent Client Protocol)

MandoCode can run an external coding agent as a child process and talk to it over the
[Agent Client Protocol](https://agentclientprotocol.com) (ACP v1, JSON-RPC 2.0 over stdio, NDJSON).
Authentication is the agent's own business; MandoCode never handles agent tokens.

The protocol layer lives in `src/MandoCode.Acp` (no UI dependencies, no third-party ACP libraries).
Schema: pinned to tag `v1.16.0`, vendored in `tests/MandoCode.Tests/Acp/schema/schema.v1.json`.

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

`/agent-acp <name>` opens a new pane (the other panes are unchanged; at most four panes exist). The pane does not
touch Ollama, models or MCP: it runs the trust question, starts the agent, runs `initialize` and `session/new`, and
then every message you type goes to the agent as a prompt. The agent lives in the pane's service scope, so closing the
pane ends its whole process tree. `/agent-acp restart` (inside the pane) starts it again.

- Header: `ACP · <adapter name and version> · mode: <current mode> · <project folder>` (the pane name is added when
  there is more than one pane).
- Streaming: the reply shows as a live preview; it is rendered as markdown when the agent starts a tool call and when
  the turn ends. Thoughts show only as a dim "Thinking…" activity line. Unknown updates and fields are ignored.
- Escape cancels the turn (`session/cancel`). If the agent has not ended the turn 10 seconds later it is shut down
  (process tree killed) and the pane tells you to `/agent-acp restart`.
- `@path` in a message adds a `resource_link` for an existing file inside the project (images are not sent yet).
- Commands that drive MandoCode's own model loop (`/model`, `/plan*`, `/compact`, `/context-snap-*`, `/force-skill`,
  `/setup`, `/retry`, `/config`, `/agent-settings`, `/ollama-*`) are hidden there and answer with an explanation.
- ACP panes are never saved to the agent history, so `/history` cannot bring one back as a native agent.
- Connecting happens in the background: the pane prints "Connecting to <name>...", shows `connecting…` in the header and
  takes input at once. A message typed meanwhile waits until the agent is ready (Escape cancels the wait). If the
  start fails, whatever the reason, the pane shows the message and stays usable: `/agent-acp restart` tries again.
- `terminal/*` is never offered.

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
Whether the real Claude Code adapter calls `fs/*` at all, or edits files itself, is **pending**: it has not been
observed (the validation above ran without a handler). Turn the trace on (below) to find out. If it does not, its edits arrive only as tool calls with `diff`
content, which are rendered the same way.

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

Pending / unverified
- Whether the adapter uses the client's fs/read_text_file and
  fs/write_text_file or accesses files itself (needs a real prompt).
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
