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
`/agent-acp <name>` (opening a pane) is not implemented yet.

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
