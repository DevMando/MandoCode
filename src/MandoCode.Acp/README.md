# MandoCode.Acp

A thin, UI-free client for the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), v1,
over stdio (newline-delimited JSON-RPC 2.0).

- No third-party ACP libraries; DTOs are hand-written against the official JSON schema.
- `System.Text.Json` source generation (`AcpJsonContext`), no reflection-based serialization.
- Targets `net8.0` and `net10.0`.

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
