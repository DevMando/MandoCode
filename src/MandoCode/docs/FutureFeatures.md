# Future Features

## Markdown-to-Terminal Renderer

**Priority:** Next up
**Status:** Completed

### Summary

Convert LLM markdown output into rich Spectre.Console terminal widgets instead of printing raw markdown text.

### Problem

AI responses are streamed as raw `Console.Write(chunk)` — markdown syntax like `**bold**`, `# Headers`, and `` `code blocks` `` displays as plain text with visible markup characters.

### Approach

1. **Add Markdig NuGet package** for markdown parsing
2. **Build a buffered streaming renderer** that accumulates tokens, detects markdown block boundaries, and flushes rendered output
3. **Map markdown nodes to Spectre.Console widgets:**

| Markdown | Spectre.Console |
|---|---|
| `**bold**` | `[bold]bold[/]` |
| `*italic*` | `[italic]italic[/]` |
| `` `inline code` `` | `[cyan on grey]code[/]` |
| ```` ```code blocks``` ```` | `Panel` with syntax coloring |
| `\| table \|` | `Table` widget |
| `# Headers` | `[bold yellow]Header[/]` + `Rule` |
| `- lists` | Indented bullet points |
| `> quotes` | `Panel` with dim border |

### Challenges

- **Streaming tokenization**: Can't parse markdown until a complete block is received (e.g., need both ``` delimiters before rendering a code panel)
- **Buffer management**: Need to detect when a block is complete and flush it, while still showing inline text in real-time
- **Bracket escaping**: Spectre.Console interprets `[` and `]` as markup tags — AI output containing these (code, JSON) must be escaped properly

### Available Infrastructure

- Spectre.Console widgets already in use: `Panel`, `Table`, `Rule`, `Markup`, `Columns`
- Additional available widgets not yet used: `Tree`, `Text`, `Padder`
- RazorConsole `<Markup>` component available for Razor-level rendering
