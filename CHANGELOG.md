# Changelog

All notable changes to MandoCode (the CLI and the shared engine that also powers
MandoCode Desktop) are documented here.

## [Unreleased]

### Fixed
- **`contextLength` now actually reaches the model.** It was exported as
  `OLLAMA_CONTEXT_LENGTH` only when MandoCode launched the Ollama daemon itself — anyone whose
  daemon was already running (the Ollama desktop app's tray daemon, most commonly) silently got
  the daemon's own default instead, making the config value, the "context window sized to Nk
  tokens" message, and the per-model auto-sizing all cosmetic. The window now rides on every
  chat request as `num_ctx`, which outranks the tray app's slider, the env var, and Ollama's
  ~4k default. `/config set contextLength` is live from the next message (its apply scope moved
  from daemon-restart to immediate), and `0` still means "let Ollama decide."
- **Context-window guidance pointed at the wrong knob.** The in-chat "context window filled"
  notice steered users to restart the daemon via `/setup`, and `/learn` and the README taught
  that the Ollama desktop app's slider "overrides everything" — both were only true of the old
  env-var mechanism. All three now describe the per-request behavior: raise `contextLength`
  (or `/clear`), no restarts involved.

### Changed
- **The context-window floor is now 16k** (default and the auto-sizing tier for local models
  under 7B; 7B+ stays at 32k). Live testing showed 8k is unusable in practice: MandoCode's
  system prompt and tool definitions consume most of it before the conversation starts, so
  small models lived in a permanent compaction cycle — summarizing cost more room than it
  freed — and filled the gaps by making things up. Sub-3B models have small KV caches, so
  16k costs them well under 1 GB of memory. Users who need a smaller window on very tight
  hardware can still set one explicitly.

### Added
- **Pre-flight context compaction.** Local Ollama never rejects an oversized prompt — it
  silently drops the oldest tokens, system prompt first, which surfaced as an empty response at
  the end of a tool-heavy turn on a small model, and the existing overflow recovery (built for
  provider *rejections*) could never fire. Before each send, the engine now estimates the
  outgoing prompt — history plus every tool schema riding along, MCP servers included — and when
  it comes within a safety reserve of the window, folds older history into a recap first and
  says so in the reply. The reserve (a quarter of the window, clamped to 1024–4096 tokens) has
  to cover two things the turn-start estimate cannot see: tool results appended mid-turn — a
  single web search re-sends the prompt with ~1k+ tokens added — and the output tokens thinking
  models spend reasoning before any visible text; a prompt that technically "fits" with no
  headroom still yields an empty answer.
