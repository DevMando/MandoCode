# VDOM Integration: AnsiPassthrough & BuildRenderable Pattern

This document covers all changes made during the two-phase VDOM integration effort. The goal was to bridge MandoCode's existing imperative ANSI rendering into RazorConsole's Blazor-based VDOM system, enabling future component-driven rendering while preserving every visual detail (colors, gradients, OSC 8 hyperlinks, etc.).

---

## Table of Contents

1. [Overview](#overview)
2. [Phase 1: AnsiPassthrough VDOM Infrastructure](#phase-1-ansipassthrough-vdom-infrastructure)
3. [Phase 2: BuildRenderable Pattern](#phase-2-buildrenderable-pattern)
4. [New Files Created](#new-files-created)
5. [Modified Files](#modified-files)
6. [Bug Fixes](#bug-fixes)
7. [Architecture Decisions & Lessons Learned](#architecture-decisions--lessons-learned)
8. [What Stays Imperative](#what-stays-imperative)
9. [Future Work](#future-work)

---

## Overview

MandoCode's `App.razor` is built on RazorConsole (Blazor + Spectre.Console). Prior to this work, all rendering was purely imperative: `Console.Write` with raw ANSI escape codes and `AnsiConsole.Write` with Spectre.Console widgets. This meant:

- **No component isolation** -- prompt, chat, music, operations all wrote to the same stdout stream
- **No `StateHasChanged()`** -- no Blazor lifecycle participation
- **No ability to re-render individual regions independently**

The two-phase approach addresses this by:

1. **Phase 1**: Building VDOM infrastructure (translator, components, capture utility, data model) that can replay ANSI output through the Blazor component tree
2. **Phase 2**: Refactoring all renderers to `BuildRenderable()` methods that return `IRenderable` objects directly, eliminating the need for stdout capture entirely

---

## Phase 1: AnsiPassthrough VDOM Infrastructure

### Goal

Create the plumbing to translate `<div class="ansi-region" data-content="...">` VDOM nodes into raw ANSI output that the terminal renders identically to the original imperative writes.

### Key Discovery: RazorConsole's Real API

The plan originally referenced `IVdomElementTranslator` with a priority-based pattern. Through DLL reflection of `RazorConsole.Core v0.5.0-alpha`, we discovered the actual API is:

```csharp
// The real interface
public interface ITranslationMiddleware
{
    IRenderable Translate(TranslationContext context, TranslationDelegate next, VNode node);
}

// Registration
services.AddSingleton<ITranslationMiddleware, AnsiPassthroughTranslator>();
```

**Key types and their namespaces:**
- `ITranslationMiddleware` -- `RazorConsole.Core.Abstractions.Rendering`
- `TranslationContext` -- `RazorConsole.Core.Rendering.Translation.Contexts`
- `TranslationDelegate` -- `RazorConsole.Core.Abstractions.Rendering`
- `VNode`, `VNodeKind` -- `RazorConsole.Core.Vdom`
- `VdomSpectreTranslator` (static helpers: `GetAttribute()`, `HasClass()`) -- `RazorConsole.Core.Rendering.Vdom`

### What Was Built

1. **`AnsiPassthroughRenderable`** -- Custom `IRenderable` that yields a pre-built ANSI string as a single `Segment`
2. **`AnsiPassthroughTranslator`** -- `ITranslationMiddleware` that intercepts `<div class="ansi-region" data-content="...">` nodes
3. **`AnsiCaptureWriter`** -- Captures both `Console.Out` and `AnsiConsole.Console` output into a string
4. **`ChatMsg`** -- Data model for chat history with raw text + rendered ANSI storage
5. **Four Razor components** -- `ChatMessage.razor`, `OperationDisplay.razor`, `MusicStatus.razor`, `StatusBar.razor`
6. **DI registration** in `Program.cs`
7. **Chat history tracking** in `App.razor` (`_messages`, `_pendingOperations`)

### Phase 1 Outcome

The infrastructure works correctly. However, enabling the VDOM `@foreach` loop over chat history caused **input freezing after 2-3 turns** because RazorConsole's live display repaints the growing chat history, conflicting with the imperative cursor position management in `CommandAutocomplete.ReadLineWithAutocomplete()`.

**Resolution**: The VDOM rendering loop was disabled. Chat history accumulates in `_messages` for `/copy` functionality and future VDOM rendering. Display continues through the imperative path. This led directly to Phase 2.

---

## Phase 2: BuildRenderable Pattern

### Goal

Eliminate the capture/replay approach entirely. Instead, refactor every renderer to construct `IRenderable` objects directly. This gives us composable rendering primitives that work both imperatively (`AnsiConsole.Write(renderable)`) and through the VDOM (`<div class="ansi-region" data-content="...">` via the `Content` property).

### Pattern

Every renderer now follows this dual-method pattern:

```csharp
// Thin wrapper for imperative callers (App.razor interactive loop)
public void Render(OperationDisplayEvent e)
{
    AnsiConsole.Write(BuildRenderable(e));
}

// Returns composable IRenderable -- no console I/O
public IRenderable BuildRenderable(OperationDisplayEvent e)
{
    var sb = new StringBuilder();
    // ... build ANSI string ...
    return new AnsiPassthroughRenderable(sb.ToString());
}
```

### Renderer Refactoring Details

#### MarkdownRenderer.cs (250+ lines changed)

The most complex refactor. Returns a `Rows(...)` composition of mixed renderable types:

| Content Type | Renderable |
|-------------|------------|
| Inline text (bold, italic, colors, OSC 8 links) | `AnsiPassthroughRenderable` built via `StringBuilder` |
| Thematic breaks (`---`) | Native `Rule()` widget |
| Code blocks (fenced/indented) | Native `Panel()` with syntax highlighting |
| Tables | Native `Table()` widget |
| Headings, paragraphs, lists, blockquotes | `AnsiPassthroughRenderable` |

**Key method renames:**
- `RenderBlock()` -> `BuildBlockRenderables(Block, int indent, List<IRenderable>)`
- `RenderHeading()` -> `BuildHeadingRenderables(HeadingBlock, List<IRenderable>)`
- `RenderParagraph()` -> `BuildParagraphRenderable(ParagraphBlock, int indent, List<IRenderable>)`
- `RenderCodeBlock()` -> `BuildCodeBlockRenderable(CodeBlock, List<IRenderable>)`
- `RenderList()` -> `BuildListRenderable(ListBlock, int indent, List<IRenderable>)`
- `RenderQuote()` -> `BuildQuoteRenderable(QuoteBlock, List<IRenderable>)`
- `RenderTable()` -> `BuildTableRenderable(MarkdigTable, List<IRenderable>)`
- `WriteHyperlink()` -> `BuildHyperlink(StringBuilder, string url, string displayText, string color)`
- `WriteLiteralWithLinks()` -> `BuildLiteralWithLinks(StringBuilder, string text)`
- All `RenderInlines()` -> `BuildInlines(StringBuilder, ContainerInline, ...)`

**All `Console.Write()`** calls replaced with `sb.Append()`
**All `AnsiConsole.Write(widget)`** calls replaced with `renderables.Add(widget)`

Final composition:
```csharp
return renderables.Count switch
{
    0 => new Text(""),
    1 => renderables[0],
    _ => new Rows(renderables)
};
```

#### OperationDisplayRenderer.cs (131 lines changed)

All `Console.WriteLine()` calls replaced with `sb.AppendLine()`. Returns `AnsiPassthroughRenderable`.

**Key method renames:**
- `RenderWriteDisplay()` -> `BuildWriteDisplay(StringBuilder sb, OperationDisplayEvent e)`
- `RenderUpdateDisplay()` -> `BuildUpdateDisplay(StringBuilder sb, OperationDisplayEvent e)`
- New `AppendPreviewLines(StringBuilder sb, string contentPreview, int maxLines)` helper that deduplicates preview line logic across Command, WebSearch, and WebFetch cases

All ANSI escape codes, `FileLink()` calls, tree connectors, inline diffs, and token tracking displays are preserved identically.

#### MusicPlayerUI.cs (104 lines changed)

Both `RenderStatus()` and `RenderTrackList()` refactored. Returns `AnsiPassthroughRenderable`.

**New public methods:**
- `BuildStatusRenderable(MusicPlayerService player)` -- builds the synthwave-themed status panel
- `BuildTrackListRenderable(MusicPlayerService player)` -- builds the track listing

**Extracted private methods:**
- `BuildStoppedRenderable()` -- the "no music playing" state
- `BuildErrorRenderable(string error)` -- audio system error display

All ANSI escape codes (synthwave purple palette), box-drawing characters, gradient volume bar, and dynamic width calculations are preserved identically.

### Component Updates

The Razor components were updated to use `BuildRenderable()` directly instead of `AnsiCaptureWriter.Capture()`:

**OperationDisplay.razor:**
```csharp
protected override void OnParametersSet()
{
    var renderable = Renderer.BuildRenderable(Operation);
    _rendered = renderable is AnsiPassthroughRenderable passthrough
        ? passthrough.Content
        : "";
}
```

**MusicStatus.razor:**
```csharp
protected override void OnParametersSet()
{
    var renderable = RenderMode == "list"
        ? MusicPlayerUI.BuildTrackListRenderable(Player)
        : MusicPlayerUI.BuildStatusRenderable(Player);

    _rendered = renderable is AnsiPassthroughRenderable passthrough
        ? passthrough.Content
        : "";
}
```

This eliminates the dependency on `AnsiCaptureWriter` for these components. The `Content` property was added to `AnsiPassthroughRenderable` specifically to support this pattern.

---

## New Files Created

### `Translators/AnsiPassthroughRenderable.cs`

Custom `IRenderable` that emits a pre-built ANSI string verbatim as a single `Segment`. The terminal interprets all escape codes (colors, bold, italic, OSC 8 links, etc.) normally.

```csharp
public sealed class AnsiPassthroughRenderable : IRenderable
{
    private readonly string _ansiContent;

    public AnsiPassthroughRenderable(string ansiContent) => _ansiContent = ansiContent;

    public string Content => _ansiContent ?? "";

    public Measurement Measure(RenderOptions options, int maxWidth) => new(0, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (!string.IsNullOrEmpty(_ansiContent))
            yield return new Segment(_ansiContent);
    }
}
```

**Used by:**
- `MarkdownRenderer.BuildRenderable()` -- for inline text sections
- `OperationDisplayRenderer.BuildRenderable()` -- wraps the entire output
- `MusicPlayerUI.BuildStatusRenderable()` / `BuildTrackListRenderable()` -- wraps the entire output
- `AnsiPassthroughTranslator` -- created from `data-content` attribute in VDOM

### `Translators/AnsiPassthroughTranslator.cs`

`ITranslationMiddleware` implementation that intercepts `<div class="ansi-region" data-content="...">` VDOM nodes and returns an `AnsiPassthroughRenderable`. This bridges ANSI content from Blazor component markup into Spectre.Console's rendering pipeline.

**Guard checks (all must pass):**
1. `node.Kind == VNodeKind.Element`
2. `node.TagName == "div"` (case-insensitive)
3. Node has CSS class `"ansi-region"`
4. `data-content` attribute is non-empty

If any check fails, `next(node)` is called to pass through to the next middleware.

### `Services/AnsiCaptureWriter.cs`

Static utility that captures both `Console.Out` AND `AnsiConsole.Console` output into a string buffer. Has both sync and async variants.

**Critical detail**: Spectre.Console's `AnsiConsole` caches its own `TextWriter` reference at startup. Simply calling `Console.SetOut()` does NOT redirect `AnsiConsole.Write()` calls. This class redirects both:

```csharp
Console.SetOut(buffer);
AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Out = new AnsiConsoleOutput(buffer),
    Ansi = AnsiSupport.Yes,
    ColorSystem = ColorSystemSupport.TrueColor
});
```

**Status**: Infrastructure exists but is no longer used in the rendering pipeline. The `BuildRenderable()` pattern in Phase 2 eliminated the need for capture. Retained for potential future use cases (e.g., third-party renderer integration).

### `Models/ChatMsg.cs`

Data model for chat history messages:

```csharp
public class ChatMsg
{
    public string Role { get; set; } = "";         // "user" or "assistant"
    public string Text { get; set; } = "";         // Raw text (used by /copy)
    public string RenderedAnsi { get; set; } = "";  // Pre-rendered ANSI (future VDOM use)
    public List<string> OperationAnsi { get; set; } = new();  // Operation displays
}
```

**Used by**: `App.razor` accumulates messages for `/copy` command functionality and future VDOM chat rendering.

### `Components/ChatMessage.razor`

Renders a single chat message with operation displays. User messages shown with gold prompt coloring, assistant messages rendered via ANSI passthrough.

```razor
@foreach (var opAnsi in Message.OperationAnsi)
{
    <div class="ansi-region" data-content="@opAnsi" />
}

@if (Message.Role == "user")
{
    <Markup Content="@($"[rgb(255,200,80)]> {Spectre.Console.Markup.Escape(Message.Text)}[/]")" />
}
else
{
    <div class="ansi-region" data-content="@Message.RenderedAnsi" />
}
```

**Status**: Ready for use. Currently not rendered in the VDOM tree (loop disabled).

### `Components/OperationDisplay.razor`

Wraps `OperationDisplayRenderer.BuildRenderable()` as a Blazor component. Extracts the ANSI string directly from the `AnsiPassthroughRenderable.Content` property -- no capture needed.

### `Components/MusicStatus.razor`

Wraps `MusicPlayerUI.BuildStatusRenderable()` / `BuildTrackListRenderable()` as a Blazor component. Supports `RenderMode` parameter (`"status"` or `"list"`).

### `Components/StatusBar.razor`

Builds the right-aligned token count display using ANSI dim styling. Conditionally renders when token tracking is enabled and tokens > 0.

---

## Modified Files

### `Program.cs`

**Changes:**
- Added `using MandoCode.Translators;`
- Added `using RazorConsole.Core.Abstractions.Rendering;`
- Registered `AnsiPassthroughTranslator` in the DI container:
  ```csharp
  services.AddSingleton<ITranslationMiddleware, AnsiPassthroughTranslator>();
  ```

This plugs the translator into RazorConsole's middleware pipeline so that any `<div class="ansi-region">` node in the VDOM tree is handled correctly.

### `Components/App.razor`

**Changes:**
- Added `_messages` (`List<ChatMsg>`) and `_pendingOperations` (`List<string>`) fields
- User messages added to `_messages` before both direct and planned AI requests
- Assistant responses added to `_messages` after rendering (stores raw `Text` for `/copy`)
- VDOM `@foreach` loop is commented out with explanation:
  ```razor
  @* Chat history accumulates in _messages for /copy and future VDOM rendering.
     Display is handled imperatively via Console.Write(ansi) during the interactive loop.
     VDOM rendering of chat history will be enabled once the interactive loop
     is fully decoupled from imperative stdout writes. *@
  ```
- Operation rendering calls `OperationRenderer.Render()` directly (imperative path)

### `Services/MarkdownRenderer.cs`

**250+ lines changed.** Complete refactor to `BuildRenderable()` pattern. See [Phase 2 details](#markdownrenderercs-250-lines-changed) above.

### `Services/OperationDisplayRenderer.cs`

**131 lines changed.** All `Console.WriteLine()` -> `sb.AppendLine()`. New `BuildRenderable()` method. See [Phase 2 details](#operationdisplayrenderercs-131-lines-changed) above.

### `Services/MusicPlayerUI.cs`

**104 lines changed.** Both render methods refactored. New `BuildStatusRenderable()` and `BuildTrackListRenderable()` methods. See [Phase 2 details](#musicplayeruics-104-lines-changed) above.

### `Services/CommandAutocomplete.cs`

**Bug fix** -- see [Bug Fixes](#bug-fixes) section below.

---

## Bug Fixes

### 1. Input Freezing After 2-3 Chat Turns

**Symptom**: After 2-3 AI response turns, the user could not type any input at all. Sometimes the spinner would finish but the prompt would be unresponsive.

**Root Cause**: The VDOM `@foreach` loop rendered the growing chat history through RazorConsole's live display, which periodically repaints the component tree. This repaint cycle fought with the imperative cursor position management in `CommandAutocomplete.ReadLineWithAutocomplete()`, eventually corrupting the console state and blocking input.

**Fix**: Disabled the VDOM `@foreach` rendering loop. Chat history continues to accumulate in `_messages` for data purposes (`/copy`), but display is handled entirely through the imperative `Console.Write`/`AnsiConsole.Write` path.

### 2. Cursor/Edit Broken After Pasting Long Text

**Symptom**: After pasting multi-line text into the prompt, the user could not move the cursor with arrow keys or edit the pasted text.

**Root Cause**: `RedrawInput()` in `CommandAutocomplete.cs` didn't ensure the terminal buffer had enough rows below `cursorTop` before writing long wrapped text. When the text wrapped past the screen bottom, the terminal scrolled, making `cursorTop` stale. Subsequent `SetCursorPosition` calls used the wrong row, breaking cursor navigation.

**Fix**: Added `EnsureBufferSpace(ref cursorTop, linesNeeded)` call at the start of `RedrawInput()`:

```csharp
private static void RedrawInput(StringBuilder input, int cursorLeft, ref int cursorTop, int cursorPos)
{
    var width = Console.WindowWidth;

    // Calculate how many rows the input will occupy when wrapped
    var totalChars = cursorLeft + input.Length;
    var linesNeeded = (totalChars + width - 1) / width;

    // Ensure the terminal buffer has enough rows below cursorTop
    EnsureBufferSpace(ref cursorTop, linesNeeded);

    Console.SetCursorPosition(cursorLeft, cursorTop);
    Console.Write("\x1b[J");
    Console.SetCursorPosition(cursorLeft, cursorTop);
    Console.Write(input.ToString());
    SetCursorToPos(cursorLeft, cursorTop, cursorPos);
}
```

### 3. AI Responses Rendered as Horizontal Lines (Capture Approach)

**Symptom**: When using `AnsiCaptureWriter.Capture()` to capture `MarkdownRenderer.Render()` output and replay it, the response appeared as horizontal lines instead of formatted text.

**Root Cause**: Two issues compounded:
1. **Initial**: `AnsiCaptureWriter` only redirected `Console.Out`, but `AnsiConsole.Write()` (used for `Rule`, `Panel`, `Table` widgets) caches its own `TextWriter` at startup and wasn't captured.
2. **After fixing #1**: Even with both redirected, Spectre.Console's layout engine produces output designed for the *current* console dimensions at render time. Capturing and replaying that string later (potentially at different cursor positions) produces corrupted layout.

**Fix**: Phase 2 -- the `BuildRenderable()` pattern eliminates capture entirely. Renderers construct `IRenderable` objects that Spectre.Console renders directly to the real console, preserving all layout calculations.

---

## Architecture Decisions & Lessons Learned

### Why Not Capture/Replay?

The initial Phase 1 approach was to capture renderer output via `Console.SetOut()` redirection and replay it through VDOM `data-content` attributes. This failed because:

1. **Spectre.Console caches `Console.Out`**: `AnsiConsole` grabs the `TextWriter` reference at startup. `Console.SetOut()` doesn't redirect Spectre widget output.
2. **Even with full redirect, layout breaks**: Spectre.Console's `Rule`, `Panel`, `Table` etc. calculate column widths and padding at render time. Captured output includes these calculations baked in, making it non-replayable at different positions.
3. **The capture approach is fundamentally output-centric**: It treats rendering as a side effect to intercept, rather than as data to compose.

### Why BuildRenderable?

The `BuildRenderable()` pattern treats rendering as **data construction**:

- **Inline ANSI text** (colors, OSC 8 links) built via `StringBuilder` -> wrapped in `AnsiPassthroughRenderable`
- **Block-level widgets** (Rule, Panel, Table) returned as native Spectre.Console `IRenderable` objects
- **Composition** via `Rows(...)` for multi-element responses
- **Dual-use**: Works imperatively (`AnsiConsole.Write(renderable)`) AND can provide VDOM content (`renderable.Content` for `AnsiPassthroughRenderable`)

### Why Keep Imperative Rendering?

The interactive input loop (`CommandAutocomplete.ReadLineWithAutocomplete()`) uses `Console.ReadKey(intercept: true)` with direct cursor manipulation. This fundamentally conflicts with RazorConsole's live display repaint cycle. Until input is also VDOM-aware, the rendering must stay imperative.

The `BuildRenderable()` pattern means we're **architecturally ready** for VDOM rendering -- every renderer can produce `IRenderable` objects that compose cleanly -- but the rendering trigger remains imperative for now.

---

## What Stays Imperative

These components write directly to the console and are NOT part of the VDOM tree:

| Component | Reason |
|-----------|--------|
| `CommandAutocomplete.ReadLineWithAutocomplete()` | Uses `Console.ReadKey(intercept: true)`, cursor manipulation |
| Prompt rendering (`"> "`) | Must write to real stdout before `ReadKey` |
| `SpinnerService` | Animated cursor-position output on current line |
| Approval prompts (`SelectionPrompt`, `TextPrompt`) | Spectre.Console interactive widgets that block for input |
| Music visualizer (OSC 0 title bar) | Terminal control sequence, not scrollback |
| OSC taskbar progress (OSC 9;4) | Terminal control sequence |
| OSC clipboard (`/copy`, `/copy-code`) | Terminal control sequence |
| Task plan interactive prompts | `SelectionPrompt` -- must block for user input |
| `HandlePlannedExecutionAsync` progress output | Mix of interactive prompts and status |
| Learn mode, config wizard | Interactive flows |

---

## Future Work

### Enable VDOM Chat Rendering

When the input system is decoupled from imperative console writes (e.g., input handled through RazorConsole's own input pipeline), the `@foreach` loop over `_messages` can be re-enabled:

```razor
@foreach (var msg in _messages)
{
    <ChatMessage Message="@msg" />
}
```

This would give us:
- Component-isolated chat history
- Independent re-rendering of individual messages
- Proper Blazor lifecycle participation (`StateHasChanged()`)

### Native VDOM Translators for Complex Renderables

For `MarkdownRenderer.BuildRenderable()` which returns `Rows(...)` with mixed widget types, a dedicated translator could map a custom VDOM element directly to the `IRenderable`:

```razor
<MarkdownContent Renderable="@markdownRenderable" />
```

Instead of going through the `data-content` string attribute (which only works for `AnsiPassthroughRenderable`).

### Remove AnsiCaptureWriter

Once all renderers are confirmed working with `BuildRenderable()` and the capture utility is no longer needed, `AnsiCaptureWriter.cs` can be removed to reduce surface area.

---

## File Summary

### New Files (8)

| File | Lines | Purpose |
|------|-------|---------|
| `Translators/AnsiPassthroughRenderable.cs` | 31 | Custom IRenderable for raw ANSI strings |
| `Translators/AnsiPassthroughTranslator.cs` | 32 | VDOM middleware for `<div class="ansi-region">` |
| `Services/AnsiCaptureWriter.cs` | 77 | Console output capture utility (retained, not actively used) |
| `Models/ChatMsg.cs` | 17 | Chat message data model |
| `Components/ChatMessage.razor` | 19 | Chat message Blazor component |
| `Components/OperationDisplay.razor` | 20 | Operation display Blazor component |
| `Components/MusicStatus.razor` | 22 | Music status Blazor component |
| `Components/StatusBar.razor` | 24 | Token count display Blazor component |

### Modified Files (5)

| File | Lines Changed | Purpose |
|------|--------------|---------|
| `Services/MarkdownRenderer.cs` | ~250 | Full BuildRenderable refactor |
| `Services/OperationDisplayRenderer.cs` | ~131 | BuildRenderable refactor |
| `Services/MusicPlayerUI.cs` | ~104 | BuildRenderable refactor |
| `Components/App.razor` | ~23 | Chat history tracking, VDOM prep |
| `Program.cs` | ~5 | AnsiPassthroughTranslator DI registration |
| `Services/CommandAutocomplete.cs` | ~12 | Paste cursor fix (EnsureBufferSpace) |

**Total: ~540 lines changed across 14 files, 0 build errors.**
