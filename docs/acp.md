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

## Probing an agent

```
mandocode --acp-probe [--yes] [--cwd <dir>] [--pass-anthropic-api-key] [--] <command> [args...]
```

Prints the full resolved command and asks for confirmation (`--yes` skips the question for scripts), then runs
`initialize` and `session/new` in the working directory and reports the agent, protocol version, capabilities,
auth methods and startup time. On failure it prints the exit code and the stderr tail. Exit codes:
`0` ok, `1` failed, `2` usage, `3` declined. Nothing is stored or trusted by the probe.

Examples:

```
mandocode --acp-probe C:\tools\some-agent.exe --acp
mandocode --acp-probe node C:\Users\me\agents\adapter\dist\index.js
```

## Client behaviour

- Advertised client capabilities: `fs.readTextFile` and `fs.writeTextFile` (when the host provides a handler);
  `terminal` is not advertised and `terminal/*` requests get `-32601`.
- `session/new` is sent with `mcpServers: []`.
- Unknown fields, unknown `session/update` kinds, unknown content types and unknown stop reasons are ignored, never fatal.
- Cancelling answers pending permission requests with `cancelled`, sends `session/cancel`, and waits for the turn
  to end (default 10 s). An agent that ignores the cancel is reported as unresponsive so the caller can kill it.
- "Not signed in" (JSON-RPC `-32000` on `session/new` or `session/prompt`) is reported as such; `authenticate`
  is not called. Sign in with the agent's own tooling.

## Validation with Claude Code

Status: the protocol layer is covered by unit tests, in-memory peer tests and end-to-end tests against a mock agent
(`tests/MandoCode.Acp.MockAgent`). **A run against a real Claude Code adapter has not been done yet.** Checklist to fill in:

| Question | Result |
|---|---|
| Adapter package name and the pinned version tested | _pending_ |
| Can the adapter use a native `claude.exe` (no Node)? | _pending_ |
| Does it route file access through client `fs/*` or write files itself? | _pending_ |
| How is a missing login signalled (error code, `authMethods`)? | _pending_ |
| Agent capabilities reported by `initialize` | _pending_ |
| Startup time | _pending_ |
