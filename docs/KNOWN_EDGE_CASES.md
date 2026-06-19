# Known Edge Cases & Future Hardening

A living backlog of edge cases, rough corners, and hardening ideas surfaced through
dogfooding — things worth looking into in future releases but not blocking the current one.
Each entry records what was *observed* (so it's reproducible) before what to *do* about it.

> Most of these were found while building real projects with MandoCode (local-model testing +
> a Three.js / Pac-Man → Batman reskin session, 2026-06-18). Real use keeps finding what tests don't.

---

## Model & tool-call compatibility

The recurring theme: **MandoCode always sends `FunctionChoiceBehavior.Auto` (all built-in + MCP
tools) to every model.** Models with strict or absent tool-schema handling break on that
assumption in different ways. A broader "tool compatibility" pass may be worth it.

### EC-1 — Tool-less models can't be used at all
- **Observed:** `gemma3:1b` → `ModelDoesNotSupportToolsException: registry.ollama.ai/library/gemma3:1b does not support tools` on every turn.
- **Cause:** the model's Ollama template has no tool support, but MandoCode always advertises tools via `Auto`. The call is rejected before the model ever runs — streaming or not.
- **Impact:** an entire class of small/older/vision models is unusable, even for plain chat.
- **Direction:** catch the exception and degrade gracefully — e.g. retry without the tools API and fall back to prompt-described tools + `FallbackFunctionCallExecutor` (which already parses text-emitted calls), or at minimum surface a clear "this model doesn't support tools" message instead of erroring every turn.

### EC-2 — Union / empty-typed tool schemas break strict-grammar models
- **Observed:** `minicpm-v4.6:latest` → `400 "Unable to generate parser for this template. Automatic parser generation failed: JSON schema conversion failed: Unrecognized schema: {"type":"","description":"Source id (e.g. \"anchor-docs\") or section id (e.g. \"frameworks\"). Single string or array."}"`.
- **Cause:** the **Solana MCP** server (`https://mcp.solana.com/mcp`) exposes a docs tool with a parameter that accepts *"a single string or an array"* — a union type that serializes to `"type": ""`. minicpm-v4.6's template makes Ollama generate a constrained-decoding grammar from all tool schemas, and the converter rejects the union/empty type. Models like qwen2.5 / llama3.1 / glm-5.2 tolerate it; minicpm-v4.6 does not.
- **Impact:** a single malformed param in *one* MCP tool blocks the whole session on stricter models.
- **Direction:** **sanitize/normalize tool schemas before sending** — coerce an empty/union `type` to a concrete type (or a proper `anyOf` / `type: ["string","array"]`). This would make far more local models compatible with MCP tools. (Note: `minicpm-v` is a *vision* model, a poor fit for coding regardless — but the schema issue is the general lesson.)

---

## Error messaging

### EC-3 — Tool-schema rejection misdiagnosed as "Ollama not running / pull the model"
- **Observed:** the EC-2 `400` is surfaced as `"Make sure Ollama is running and the model is installed. Run: ollama pull …"`.
- **Cause:** the generic HTTP-failure handler doesn't recognize this 400; it falls back to the install/connection hint.
- **Impact:** sends the user down the wrong path — Ollama *is* running and the model *is* installed.
- **Direction:** detect this specific 400 (`"Unable to generate parser"` / schema-conversion failures) and message accurately: *"Model X couldn't build a tool-call parser for the current tools — likely an incompatible tool schema (often from an MCP server). Try another model or `/config set mcp false`."* Same class as the already-fixed stall misdiagnosis — a faithful error is worth as much as the fix.

---

## Agent-loop hardening

### EC-4 — No functional verification step (textual ≠ functional)
- **Observed:** after a reskin, an exhaustive grep sweep reported *"all mechanics intact"* and the game was still **unplayable** — a pre-existing movement bug (`isNearPerpCenter` computed tile boundaries instead of centers) meant Batman couldn't turn. Only *playing it* caught it.
- **Cause:** grep/diff verification can confirm *"did I change what I meant to, and nothing else?"* — it is structurally blind to *"does the thing actually work?"* and to pre-existing defects.
- **Direction:** an optional capstone that **exercises the artifact** (launch it, load the page, run the tests) rather than only grepping for stale terms. The verification lever the model already *improvises* (the grep sweep) is the textual half; the functional half is missing.

### EC-5 — Bulk full-file rewrites can introduce subtle semantic breakage
- **Observed:** during a ~1,300-line full-file rewrite, the model emitted `ctxCtx.createGain()` instead of `ctxRef.createGain()` — valid syntax, undefined identifier, would have broken all audio. It self-caught and fixed it this time.
- **Cause:** a full rewrite is a huge surface for a single wrong identifier; a diff can't flag it (it's syntactically valid).
- **Direction:** lightweight post-write checks on large writes — a syntax/lint pass for the language, or a cheap "this identifier appears exactly once in the file" heuristic — to catch the cases the model *doesn't* notice on its own.

---

## Streaming roadmap (deferred — not bugs)

Context: v0.13.0 ships buffered streaming with a per-chunk watchdog heartbeat. These are the
deliberately-deferred follow-ups.

### EC-6 — Live token rendering
- Streaming is currently **buffered** (render-at-end), which is what keeps partial tool-call JSON off the screen. Live token-by-token rendering is the bigger UX win but needs a lookahead that **suppresses partial JSON** for text-emitting models before it can ship safely.

### EC-7 — Local multi-round tool chains under streaming
- Single-call local streaming is verified (qwen2.5:1.5b structured, gemma3:1b text). Cloud *multi-round* is proven by dogfooding (the Batman session). **Local multi-round** (read → edit → read → edit in one streamed turn) hasn't been directly watched — verify when the streaming tri-state ships, ideally via a real multi-step task in-app.

### EC-8 — Bracketed edge: `Auto` + a text-emitting tool-*supporting* model
- The one combination not directly observed: a model that accepts the tools API but emits the call as text anyway. Hard to force on demand (tool-supporting models tend to use the structured channel). Bracketed by qwen2.5 (Auto + structured) and gemma3 (text buffering); the hybrid double-invoke is backstopped by the per-scope dedup circuit. Low residual risk — revisit only if a real model exhibits it.

---

## Dev environment (minor)

### EC-9 — Running via Visual Studio F5 sets the working directory to `bin/Debug/net8.0`
- **Observed:** during testing, `@StarFox/` resolved to `…/bin/Debug/net8.0/StarFox`, and `**/*` searches picked up `MandoCode.dll` and unrelated build-output files (e.g. a leftover "SOLANA GAME" stylesheet).
- **Impact:** file ops land in the build output (wiped by Clean), and searches are polluted by binaries/other artifacts.
- **Direction:** none needed in product — just a testing note. To test against a real project dir, pass a project root arg or `cd` before launching rather than F5-ing from the project.
