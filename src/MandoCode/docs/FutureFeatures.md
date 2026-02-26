# Future Features

## Completed Features

### Markdown-to-Terminal Renderer

**Status:** Shipped

Implemented in `Services/MarkdownRenderer.cs` using Markdig for parsing and Spectre.Console + ANSI escape codes for rendering. Supports headers, bold, italic, inline code, fenced code blocks (with syntax highlighting via `SyntaxHighlighter.cs`), tables, lists, block quotes, and thematic breaks. Responses are buffered and rendered as rich terminal output.

### Syntax Highlighting

**Status:** Shipped

Implemented in `Services/SyntaxHighlighter.cs`. Regex-based highlighter supporting C#, Python, JavaScript/TypeScript, and Bash with keyword (yellow), type (cyan), string (green), comment (dim), and number (magenta) coloring.

### Token Tracking

**Status:** Shipped

Implemented in `Services/TokenTrackingService.cs` and `Models/TokenUsageInfo.cs`. Tracks real token counts from Ollama responses and estimated counts from `@file` references. Displays per-response summaries and session totals. Configurable via `enableTokenTracking`.

### `/learn` Command — LLM Education & Onboarding

**Status:** Shipped

Implemented in `Models/LearnContent.cs`. Displays educational content about open-weight LLMs, model sizes, cloud vs local models, and setup instructions. Automatically shown at startup when Ollama is not detected. Offers interactive AI educator chat mode when a model is available.

### Background Music Player

**Status:** Shipped

Implemented in `Services/MusicPlayerService.cs`, `Services/MusicPlayerUI.cs`, and `Models/MusicModels.cs` + `Models/MusicAsciiArt.cs`. NAudio-powered lofi/synthwave music playback with seamless looping, animated equalizer in the terminal title bar (OSC 0), inline status panels, and full command suite (`/music`, `/music-stop`, `/music-pause`, `/music-next`, `/music-vol`, `/music-lofi`, `/music-synthwave`, `/music-list`). Volume and genre preferences persisted in config.

### `/copy` Command — Clipboard

**Status:** Shipped

Copies the last AI response to the system clipboard using OSC 52 terminal escape codes. No external tools required.

### Clickable File Paths (OSC 8)

**Status:** Shipped

Implemented via `FileLink()` helper in `Components/App.razor`. All file paths in operation displays (Read, Write, Update, Delete, DeleteFolder, CreateFolder, Glob, Search, Diff panel) are wrapped in OSC 8 `file://` hyperlinks. Clicking opens the file in the default editor. URLs in AI markdown output are also rendered as clickable hyperlinks via `Services/MarkdownRenderer.cs`.

### Shell Escape (`!` and `/command`)

**Status:** Shipped

Implemented in `Components/App.razor` via `HandleShellCommand()`. Type `!<cmd>` or `/command <cmd>` to run shell commands inline. `cd` is intercepted in-process to update the project root, refresh file cache, and emit OSC 9;9 for Windows Terminal CWD sync. Cross-platform: uses `cmd.exe /c` on Windows, `/bin/bash -c` on Linux/macOS.

### Taskbar Progress Bar (OSC 9;4)

**Status:** Shipped

Implemented via `SetTaskbarProgress()`, `SetTaskbarIndeterminate()`, `SetTaskbarError()`, and `ClearTaskbarProgress()` helpers in `Components/App.razor`. Windows Terminal taskbar icon pulses during AI requests and fills step-by-step during task plan execution. Shows error state on step failure.

---

## Planned Features
