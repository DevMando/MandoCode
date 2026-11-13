# MandoCode.Acp

A thin, UI-free client for the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), v1,
over stdio (newline-delimited JSON-RPC 2.0).

- No third-party ACP libraries; DTOs are hand-written against the official JSON schema.
- `System.Text.Json` source generation (`AcpJsonContext`), no reflection-based serialization.
- Targets `net8.0` and `net10.0`; depends only on the BCL (no NuGet packages).

## Schema

DTOs follow `schema.json` from `agentclientprotocol/agent-client-protocol`, pinned to tag
**`v1.16.0`** (`schema/v1/schema.json`). The vendored copy lives in
`tests/MandoCode.Tests/Acp/schema/schema.v1.json`; a drift test checks every modelled type
against it. To update: fetch the new tag's `schema/v1/schema.json`, replace the vendored file,
run the tests and update the tag here and in `AcpProtocol.SchemaTag`.

## Compatibility rules

- Unknown JSON fields are ignored.
- Unknown `session/update`, content-block and tool-call-content variants are surfaced as
  `Unknown*` values instead of failing the message.
- "Enum" values (stop reasons, tool kinds, statuses, ...) are plain strings with constant
  classes, so values added by newer agents never break parsing.
- Features are decided from the capabilities the agent returns from `initialize`.

## JSON-RPC layer

`JsonRpc/JsonRpcConnection` runs JSON-RPC 2.0 over a pair of streams (the agent's stdout/stdin):

- one message per line (`
`, a trailing `` is dropped, blank lines are skipped); a line over
  32 MB, or several consecutive non-JSON lines, break the connection with an explanation;
- outgoing ids are numbers; incoming ids (number or string) are echoed back unchanged;
- the reader never waits on a handler: each incoming request runs on its own task, so a
  permission prompt can be pending while `session/prompt` is still in flight;
- incoming notifications are handled by one consumer, in order. A response is dispatched as soon
  as it is read, so callers that must see every `session/update` that preceded it await
  `DrainNotificationsAsync()` first;
- unknown methods get `-32601`, bad params `-32602`, handler failures `-32603` (without details).

## Process transport

`Transport/AcpProcessTransport` runs the agent as a child process.

- Started with `UseShellExecute = false` and `ArgumentList`. No shell (`cmd.exe`, `powershell`,
  `sh -c`) and no `npx` is ever involved.
- `AcpCommandResolver` turns the configured command into a real native executable or refuses:
  Windows accepts only an `.exe` (PE header), Unix only ELF/Mach-O binaries. `.cmd`/`.bat`/`.ps1`
  shims and `#!` scripts are rejected with an explanation. Package downloaders (`npx`, `pnpm`,
  `yarn`, ...) and shells are forbidden. For `node`, the first argument must be the absolute
  path of an existing script. The resolved absolute path is what is executed.
- `AcpEnvironment` removes `ANTHROPIC_API_KEY` from the inherited environment unless the agent
  entry opts in; the entry's own `env` is applied on top.
- stdout is the protocol channel only. stderr is drained into `StderrLog` (ring buffer plus an
  optional rotated file) and never reaches the console.
- Shutdown closes stdin, waits a grace period, then kills the whole process tree. A
  `ProcessExit` hook kills any still-running agents if MandoCode itself exits abruptly.

The integration tests run the real transport against `tests/MandoCode.Acp.MockAgent`.
