# Codex in an ACP pane

MandoCode can use the maintained [Codex ACP adapter](https://github.com/agentclientprotocol/codex-acp)
through the existing ACP client. Codex owns model execution, its tools, and authentication.
MandoCode displays the conversation and relays permissions through the same pane integration used for other ACP agents.

## Setup on Windows

Install Node.js and sign in to Codex separately with `codex login`. MandoCode does not perform
the adapter's authentication flow or collect credentials. An existing compatible Codex login
can be reused; if authentication is required, finish it with Codex before reconnecting.

Install a pinned adapter into a folder you control. For example, in PowerShell:

```powershell
$adapterFolder = Join-Path $env:USERPROFILE '.mandocode\acp\codex'
npm install --prefix "$adapterFolder" --save-exact @agentclientprotocol/codex-acp@2.6.0
Join-Path $adapterFolder 'node_modules\@agentclientprotocol\codex-acp\dist\index.js'
```

Copy the absolute path printed by the last command. In MandoCode, register it with:

```text
/agent-acp add codex node "C:\Users\YOUR_NAME\.mandocode\acp\codex\node_modules\@agentclientprotocol\codex-acp\dist\index.js"
/agent-acp test codex
/agent-acp codex --new-panel
```

Replace `YOUR_NAME` with your Windows user folder. If Node is not on PATH, use the absolute
path to `node.exe`. The adapter's package includes a compatible Codex dependency; a separately
installed Codex executable can be selected using `CODEX_PATH` in the entry's environment.
Keep the installation's lock file to preserve the resolved dependency versions.

`/agent-acp add codex` prints this setup guidance. It does not install an adapter or save a
configuration automatically. The normal launch confirmation still applies. MandoCode runs
Node directly, without `npx`, an npm shim, or a shell at launch time.

Other operating systems can use the same npm package and register its absolute JavaScript
entry path with `node`. The live validation below was performed on Windows only.

## Commands and boundaries

- `/model` uses the adapter's advertised model configuration. Available models depend on the account and adapter.
- `/compact` appears when the adapter advertises that command.
- `/clear` starts another ACP conversation and preserves the native MandoCode conversation.
- `/copy` and `/copy-code` read the ACP conversation's last reply.
- `/agent-acp stop` returns to the native conversation.
- Folder changes follow the normal ACP restart and confirmation flow.

Capability handling remains shared with other ACP agents. This setup does not add native
agent mentions, image paste, context handoff, or the proposed MandoCode MCP bridge.
The generic ACP restrictions still apply even if Codex itself advertises additional features.

The adapter also supports API-key authentication. Existing `OPENAI_API_KEY` and `CODEX_API_KEY`
environment variables are not filtered by MandoCode. Check your Codex authentication setup
if you specifically intend to use an existing ChatGPT login instead of API billing.

## Validation

Validated on Windows with `@agentclientprotocol/codex-acp` 2.6.0:

- ACP v1 initialization and session creation using existing login state, with API-key variables removed from the test process.
- MandoCode's actual `AcpClient` and process transport connected to the adapter and parsed its configuration options.
- Selected read-only mode, received a short streamed text reply, and closed the session and client successfully.
- Command tests cover setup guidance without saving an entry and explicit executable registration.

This verifies the core connection and text path. Interactive permission menus, cancellation,
model changes, compaction, session restoration, and folder restarts need manual validation
with Codex before claiming those flows have been tested against this adapter.
