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

---

## Planned Features
