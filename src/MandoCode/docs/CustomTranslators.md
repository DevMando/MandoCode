# Custom VDOM Translators — MandoCode Integration

## Overview

MandoCode's rendering is currently a monolith: `App.razor` drives an imperative loop where services like `MarkdownRenderer`, `OperationDisplayRenderer`, `DiffApprovalHandler`, and `MusicPlayerUI` write directly to stdout with ANSI escape codes. This works, but it means we can't leverage RazorConsole's VDOM lifecycle — components can't re-render independently, the prompt and chat output are tangled, and there's no layout isolation.

The upgrade uses a **two-phase bridge strategy**:

- **Phase 1 (AnsiPassthrough)** — Wrap existing renderers in Blazor components via a custom translator that replays captured ANSI output. No renderer rewrites. Existing colors, gradients, OSC links — all preserved. We get component decomposition, `StateHasChanged()`, layout isolation, and the full Blazor lifecycle.

- **Phase 2 (Native Translators)** — Incrementally replace passthrough wrappers with translators that produce real `IRenderable` objects. This gives surgical VDOM diffing (only changed nodes re-render). Each component converts independently — no big bang.

This document covers Phase 1 implementation and the Phase 2 upgrade path.

---

## Phase 1 — AnsiPassthrough Architecture

### The Core Idea

Instead of rewriting renderers, we **capture their output** into a string and wrap it in a custom `IRenderable` that replays the ANSI verbatim. The VDOM owns the layout; the existing code owns the content.

```
Existing Renderer                    VDOM Pipeline
─────────────────                    ─────────────
MarkdownRenderer.Render()  ──┐
  Console.Write(ANSI...)     │    Captured     AnsiPassthrough     Spectre.Console
  AnsiConsole.Write(...)     ├──▶ string   ──▶ Translator      ──▶ IRenderable
  Console.Write(ANSI...)     │    (ANSI)       (Priority 200)      (Segment)
                           ──┘
```

ANSI escape codes are just characters in a string. When the `AnsiPassthroughRenderable` emits them as a `Segment`, the terminal interprets them identically — colors, bold, RGB, OSC 8 links, all preserved.

### Why Option 1 (Capture) Over Option 2 (Layout-Only)

| | Option 1: AnsiPassthrough | Option 2: Layout boundaries only |
|---|---|---|
| VDOM owns content | Yes — can re-render, diff, swap | No — just positions cursor |
| Colors/ANSI preserved | Yes — captured verbatim | Yes |
| Composability | Yes — components are real VDOM nodes | No — manual region management |
| StateHasChanged() | Works — re-captures and swaps region | Would need manual clear + redraw |
| Autocomplete/Prompt | Unaffected — stays on real stdout | Unaffected |
| Path to Phase 2 | Swap passthrough for native translator | Rewrite from scratch |
| Complexity | Minimal — one translator + capture helper | Same work, less benefit |

---

### Infrastructure: 3 Pieces to Build

#### 1. AnsiPassthroughRenderable

Custom `IRenderable` that replays a pre-built ANSI string through Spectre.Console's rendering pipeline.

```csharp
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

/// <summary>
/// An IRenderable that outputs a pre-built ANSI string verbatim.
/// The terminal interprets all escape codes (colors, OSC links, etc.) as normal.
/// </summary>
public sealed class AnsiPassthroughRenderable : IRenderable
{
    private readonly string _ansiContent;

    public AnsiPassthroughRenderable(string ansiContent)
    {
        _ansiContent = ansiContent;
    }

    public Measurement Measure(RenderOptions options, int maxWidth)
        => new(0, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (!string.IsNullOrEmpty(_ansiContent))
            yield return new Segment(_ansiContent);
    }
}
```

#### 2. AnsiPassthroughTranslator

Custom translator that handles `<div class="ansi-region">` nodes. Reads the ANSI content from `data-content` attribute and produces an `AnsiPassthroughRenderable`.

```csharp
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

/// <summary>
/// Translates <div class="ansi-region" data-content="..."> nodes into
/// AnsiPassthroughRenderable instances that replay captured ANSI output.
/// </summary>
public sealed class AnsiPassthroughTranslator : IVdomElementTranslator
{
    public int Priority => 200;

    public bool TryTranslate(VNode node, TranslationContext context, out IRenderable? renderable)
    {
        renderable = null;

        if (node.Kind != VNodeKind.Element)
            return false;

        if (!string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!VdomSpectreTranslator.HasClass(node, "ansi-region"))
            return false;

        var content = VdomSpectreTranslator.GetAttribute(node, "data-content");
        if (string.IsNullOrEmpty(content))
            return false;

        renderable = new AnsiPassthroughRenderable(content);
        return true;
    }
}
```

#### 3. AnsiCaptureWriter

Helper that redirects `Console.Out` to a `StringWriter`, runs an existing renderer, captures the output, and restores stdout. This is the bridge between imperative renderers and the VDOM.

```csharp
using System.IO;
using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Captures Console.Write/WriteLine output from existing renderers into a string.
/// Used to bridge imperative ANSI rendering into the VDOM via AnsiPassthroughTranslator.
///
/// IMPORTANT: This captures ALL console output while active. Only use during
/// synchronous render calls — never during interactive input or concurrent operations.
/// </summary>
public static class AnsiCaptureWriter
{
    /// <summary>
    /// Executes an action while capturing all Console output, returns the captured string.
    /// </summary>
    public static string Capture(Action renderAction)
    {
        var buffer = new StringWriter(new StringBuilder(4096));
        var original = Console.Out;

        try
        {
            Console.SetOut(buffer);
            renderAction();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Async version for renderers that use AnsiConsole or await internally.
    /// </summary>
    public static async Task<string> CaptureAsync(Func<Task> renderAction)
    {
        var buffer = new StringWriter(new StringBuilder(4096));
        var original = Console.Out;

        try
        {
            Console.SetOut(buffer);
            await renderAction();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }
}
```

**Concurrency safety note:** `Console.SetOut` is global. The capture must only run during synchronous render calls — never while the spinner, music visualizer, or prompt is active. In practice this is safe because:
- The spinner is stopped before operation displays and diff panels render
- The music visualizer writes to the title bar (OSC 0), which is a separate call
- The prompt (`ReadLineWithAutocomplete`) runs in the interactive loop, which is separate from render calls

If we later need more isolation, we refactor renderers to accept a `TextWriter` parameter (one-line signature change per method).

---

### Component Decomposition

Phase 1 breaks the `App.razor` monolith into real Blazor components. Each component wraps its existing renderer via `AnsiCaptureWriter`.

#### Target Component Tree

```
App.razor (thin layout shell)
├── <Banner />                          ← already a component, stays as-is
├── <Rows>                              ← RazorConsole layout
│   ├── <Markup ... />                  ← project root, endpoint, model info
│   ├── <HelpDisplay />                 ← already a component
│   └── <ChatRegion>                    ← NEW: scrollable chat history
│       ├── <ChatMessage />             ← NEW: one per user/assistant turn
│       │   ├── user prompt: plain text
│       │   └── assistant: <div class="ansi-region"> (passthrough)
│       ├── <OperationDisplay />        ← NEW: wraps OperationDisplayRenderer
│       ├── <DiffPanel />              ← NEW: wraps DiffApprovalHandler rendering
│       └── ...
│   ├── <MusicPanel />                  ← NEW: wraps MusicPlayerUI
│   └── <StatusBar />                   ← NEW: token count display
├── (prompt stays imperative — see below)
```

#### What Each Component Looks Like

**ChatMessage.razor** — wraps a single AI response:

```razor
@using MandoCode.Services

@if (Message.Role == "user")
{
    <Markup Content="@($"[rgb(255,200,80)]> {Message.Text}[/]")" />
}
else
{
    <div class="ansi-region" data-content="@Message.RenderedAnsi" />
}

@code {
    [Parameter] public ChatMsg Message { get; set; } = default!;
}
```

Where the calling code captures:

```csharp
// In the interactive loop, when AI responds:
var renderedAnsi = AnsiCaptureWriter.Capture(() => MarkdownRenderer.Render(responseText));
_messages.Add(new ChatMsg { Role = "assistant", Text = responseText, RenderedAnsi = renderedAnsi });
StateHasChanged();
```

**OperationDisplay.razor** — wraps operation events:

```razor
@using MandoCode.Services

<div class="ansi-region" data-content="@_rendered" />

@code {
    [Parameter] public OperationDisplayEvent Operation { get; set; } = default!;
    [Parameter] public OperationDisplayRenderer Renderer { get; set; } = default!;

    private string _rendered = "";

    protected override void OnParametersSet()
    {
        _rendered = AnsiCaptureWriter.Capture(() => Renderer.Render(Operation));
    }
}
```

**MusicPanel.razor** — wraps music status:

```razor
@using MandoCode.Services

<div class="ansi-region" data-content="@_rendered" />

@code {
    [Parameter] public MusicPlayerService Player { get; set; } = default!;

    private string _rendered = "";

    protected override void OnParametersSet()
    {
        _rendered = AnsiCaptureWriter.Capture(() => MusicPlayerUI.RenderStatus(Player));
    }
}
```

**DiffPanel.razor** — wraps diff display (approval prompt stays imperative):

```razor
@using MandoCode.Models
@using MandoCode.Services

<div class="ansi-region" data-content="@_rendered" />

@code {
    [Parameter] public string FilePath { get; set; } = "";
    [Parameter] public List<DiffLine> DisplayLines { get; set; } = new();
    [Parameter] public int Additions { get; set; }
    [Parameter] public int Deletions { get; set; }
    [Parameter] public bool IsNewFile { get; set; }

    private string _rendered = "";

    protected override void OnParametersSet()
    {
        // Capture the diff panel rendering
        // The approval prompt (SelectionPrompt) stays outside this component
        _rendered = AnsiCaptureWriter.Capture(() =>
            RenderDiffPanelStatic(FilePath, DisplayLines, Additions, Deletions, IsNewFile));
    }

    // Static rendering extracted from DiffApprovalHandler.RenderDiffPanel
    private static void RenderDiffPanelStatic(string filePath, List<DiffLine> lines,
        int additions, int deletions, bool isNewFile)
    {
        // Same rendering logic as DiffApprovalHandler.RenderDiffPanel
        // just extracted as a static method
    }
}
```

#### What Stays Imperative (Not Wrapped)

| Component | Why |
|-----------|-----|
| **Prompt / ReadLineWithAutocomplete** | Uses `Console.ReadKey(intercept: true)` and direct cursor manipulation. This is inherently interactive and must stay on real stdout. It runs in `RunInteractiveLoopAsync`, separate from VDOM rendering. |
| **Approval prompts** (SelectionPrompt, TextPrompt) | Spectre.Console interactive widgets that block for user input. They stop the spinner, render, collect input, then resume. They can't be VDOM nodes. |
| **OSC title bar** (music visualizer) | Writes to terminal title via OSC 0, not to scrollback. This is a terminal control sequence, not visual content. |
| **OSC taskbar progress** | Terminal control sequence (OSC 9;4), not renderable content. |
| **OSC clipboard** (`/copy`) | Terminal control sequence (OSC 52), not renderable content. |
| **Spinner** | Writes animated dots to current line, needs real cursor position. Stays imperative. |

---

### Registration in Program.cs

```csharp
using MandoCode.Translators;

// In the host builder configuration:
IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args)
    .UseRazorConsole<App>(configure: config =>
    {
        config.ConfigureServices(services =>
        {
            // Phase 1: AnsiPassthrough translator
            services.AddVdomTranslator<AnsiPassthroughTranslator>();

            // Existing service registrations...
        });
    });
```

Phase 1 only needs **one translator** registered. All components use `<div class="ansi-region">` — the translator handles them all.

---

### Data Model Addition

```csharp
namespace MandoCode.Models;

/// <summary>
/// Represents a single message in the chat history.
/// Stores both the raw text and the pre-rendered ANSI output.
/// </summary>
public class ChatMsg
{
    public string Role { get; set; } = "";       // "user" or "assistant"
    public string Text { get; set; } = "";       // raw text (for /copy, re-rendering)
    public string RenderedAnsi { get; set; } = ""; // captured ANSI output (for display)

    /// <summary>
    /// Operation displays that occurred during this response.
    /// </summary>
    public List<string> OperationAnsi { get; set; } = new();
}
```

---

### Interactive Loop Changes

The `RunInteractiveLoopAsync` method in `App.razor` changes from imperatively writing output to building up a `_messages` list and calling `StateHasChanged()`:

```csharp
// BEFORE (imperative):
MarkdownRenderer.Render(response);

// AFTER (VDOM-driven):
var renderedAnsi = AnsiCaptureWriter.Capture(() => MarkdownRenderer.Render(response));
_messages.Add(new ChatMsg { Role = "assistant", Text = response, RenderedAnsi = renderedAnsi });
StateHasChanged();  // VDOM diffs and renders only the new message
```

For operation displays during AI execution:

```csharp
// BEFORE:
private void OnFunctionCompleted(FunctionExecutionResult result)
{
    OperationRenderer.Render(result.OperationDisplay);
}

// AFTER:
private void OnFunctionCompleted(FunctionExecutionResult result)
{
    if (result.OperationDisplay != null)
    {
        var ansi = AnsiCaptureWriter.Capture(() => OperationRenderer.Render(result.OperationDisplay));
        _currentOperations.Add(ansi);
        StateHasChanged();
    }
}
```

---

### Phase 1 Checklist

| # | Task | Files Affected |
|---|------|----------------|
| 1 | Create `Translators/AnsiPassthroughRenderable.cs` | New file |
| 2 | Create `Translators/AnsiPassthroughTranslator.cs` | New file |
| 3 | Create `Services/AnsiCaptureWriter.cs` | New file |
| 4 | Create `Models/ChatMsg.cs` | New file |
| 5 | Register translator in `Program.cs` | Modify |
| 6 | Create `Components/ChatMessage.razor` | New file |
| 7 | Create `Components/OperationDisplay.razor` | New file |
| 8 | Create `Components/MusicPanel.razor` | New file |
| 9 | Create `Components/StatusBar.razor` | New file |
| 10 | Refactor `App.razor` — extract interactive loop to use `_messages` + `StateHasChanged()` | Modify |
| 11 | Refactor `App.razor` — replace imperative `OperationRenderer.Render()` calls with component state | Modify |
| 12 | Refactor `App.razor` — replace imperative `MusicPlayerUI.RenderStatus()` calls with component | Modify |
| 13 | Test: colors, OSC links, autocomplete, diff approvals, music all work as before | Manual QA |

**Files NOT modified:** `MarkdownRenderer.cs`, `OperationDisplayRenderer.cs`, `SyntaxHighlighter.cs`, `MusicPlayerUI.cs`, `DiffService.cs`, `DiffApprovalHandler.cs` (rendering methods), `CommandAutocomplete.cs`, `FileAutocompleteProvider.cs`.

---

## Phase 2 — Native Translators (Future Upgrade Path)

Phase 2 converts individual passthrough wrappers into translators that produce real `IRenderable` objects. Each conversion is independent — do them one at a time when ready.

### What Changes

```
Phase 1 (Passthrough)              Phase 2 (Native)
─────────────────────              ────────────────
<div class="ansi-region"      →    <div class="op-display"
  data-content="@captured"/>        data-op="Read" data-file="..."/>
                                   ↓
AnsiPassthroughTranslator     →    OperationDisplayTranslator
  → AnsiPassthroughRenderable       → Composed Markup/Rows (real IRenderable)
```

The Razor component markup changes. The translator changes. The existing renderer can be retired. But the component's `[Parameter]` interface stays the same, so nothing upstream breaks.

### Benefits Over Phase 1

| Capability | Phase 1 (Passthrough) | Phase 2 (Native) |
|---|---|---|
| Layout isolation | Yes | Yes |
| Blazor lifecycle | Yes | Yes |
| StateHasChanged() | Re-captures whole region | Surgical VDOM diff |
| Re-render cost | Full repaint of region | Only changed nodes |
| ANSI code dependency | Still uses ANSI strings | Pure Spectre.Console |

### Proposed Translator Priority Map

| Priority | Translator | Handles | Replaces |
|----------|------------|---------|----------|
| 200 | `AnsiPassthroughTranslator` | `<div class="ansi-region">` | (Phase 1 — keeps working alongside native translators) |
| 210 | `OperationDisplayTranslator` | `<div class="op-display">` | `OperationDisplayRenderer.cs` |
| 220 | `DiffPanelTranslator` | `<div class="diff-panel">` | `DiffApprovalHandler.RenderDiffPanel` |
| 230 | `DiffLineTranslator` | `<div class="diff-line">` | (child of DiffPanel) |
| 240 | `CommandPanelTranslator` | `<div class="command-panel">` | `DiffApprovalHandler.HandleCommandApproval` rendering |
| 300 | `MusicPanelTranslator` | `<div class="music-panel">` | `MusicPlayerUI.cs` |
| 310 | `VolumeBarTranslator` | `<div class="volume-bar">` | `MusicPlayerUI.BuildGradientVolumeBar` |
| 400 | `MarkdownBlockTranslator` | `<div data-markdown="true">` | `MarkdownRenderer.cs` |
| 500 | `HyperlinkTranslator` | `<a data-osc8="true">` | OSC 8 link generation |
| 510 | `FileLinkTranslator` | `<a data-file-link="true">` | `FileLinkHelper.cs` |

The `AnsiPassthroughTranslator` at priority 200 stays registered — components that haven't been converted yet continue to work. As each native translator is added, its component switches from `<div class="ansi-region">` to the native element, and the new translator (at a higher priority for that specific class) handles it.

### Conversion Order (Recommended)

1. **OperationDisplay** — stateless, self-contained, high frequency. Best candidate.
2. **DiffPanel + DiffLine** — visual complexity but well-defined structure.
3. **CommandPanel** — simple bordered box.
4. **MusicPanel + VolumeBar** — RGB gradients map to `Color.FromRgb()`.
5. **MarkdownBlock** — most complex (Markdig parsing, nested rendering, OSC 8). Do last.

### What Stays Imperative Forever

- **Prompt / ReadLineWithAutocomplete** — interactive input with cursor manipulation
- **Approval prompts** (SelectionPrompt, TextPrompt) — Spectre.Console interactive widgets
- **OSC terminal control** (title, taskbar progress, clipboard, CWD)
- **Spinner** — animated cursor-position output

---

## RazorConsole Translator API Reference

### The Interface

```csharp
public interface IVdomElementTranslator
{
    int Priority { get; }  // Lower = processed first (1-1000+)
    bool TryTranslate(VNode node, TranslationContext context, out IRenderable? renderable);
}
```

### Registration

```csharp
services.AddVdomTranslator<MyTranslator>();          // by type (DI-resolved)
services.AddVdomTranslator(new MyTranslator());      // by instance
services.AddVdomTranslator(sp => new MyTranslator(   // by factory
    sp.GetRequiredService<ISomeDependency>()));
```

### Translation Pipeline

```
Razor Component → VNode Tree → Translators (by priority) → IRenderable → Terminal
```

Translators are tried in ascending priority order. First one that returns `true` wins. Priority 1000 is the built-in fallback (diagnostic renderer).

### Built-in Translator Priorities

| Priority | Handles |
|----------|---------|
| 10 | `<span data-text="true">` → Markup |
| 20 | `<strong>`, `<em>`, `<code>` → Markup |
| 30 | `<p>` → Markup |
| 40 | `<div data-spacer="true">` → Padder |
| 50 | `<br>` → Text (newline) |
| 60 | `<div class="spinner">` → Spinner |
| 70-80 | `<button>` → Button renderables |
| 90 | `<div class="syntax-highlighter">` → SyntaxRenderable |
| 100-190 | Panels, rows, columns, grids, padding, alignment |
| 1000 | Fallback (unhandled nodes) |

### Key Utility Methods

```csharp
// Node inspection
VdomSpectreTranslator.GetAttribute(node, "data-style");
VdomSpectreTranslator.HasClass(node, "my-class");
VdomSpectreTranslator.CollectInnerText(node);

// Attribute parsing
VdomSpectreTranslator.TryGetBoolAttribute(node, "data-enabled", out bool val);
VdomSpectreTranslator.TryParsePositiveInt(rawValue, out int result);
VdomSpectreTranslator.TryParsePadding(rawValue, out Padding padding);
VdomSpectreTranslator.ParseHorizontalAlignment(value);

// Child translation (recursive)
VdomSpectreTranslator.TryConvertChildrenToRenderables(node.Children, context, out var children);
VdomSpectreTranslator.ComposeChildContent(children);  // many → Rows, one → as-is, zero → empty Markup
```

---

## Rules for MandoCode Translators

### Do

- **Fail fast** — check `node.Kind`, `node.TagName`, then attributes/classes. Return `false` immediately on mismatch.
- **Use case-insensitive comparison** — `string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase)`.
- **Use utility methods** — `GetAttribute`, `HasClass`, `TryParsePositiveInt`, `TryConvertChildrenToRenderables`.
- **Be stateless** — no mutable instance fields. All data comes from the VNode.
- **Translate children via context** — always use `context.TryTranslate()` or `TryConvertChildrenToRenderables()` for recursive child translation.
- **Create new renderable instances** — don't mutate or reuse existing renderables.
- **Use DI for dependencies** — inject services via constructor, register with `AddVdomTranslator<T>()`.

### Don't

- **Don't call `Console.Write` or `Console.WriteLine`** in native translators — produce `IRenderable` objects instead. (AnsiPassthrough is the exception — it replays captured ANSI by design.)
- **Don't use raw ANSI escape codes** in native translators — use Spectre.Console `Style`, `Color`, `Markup` instead.
- **Don't assume child translation succeeds** — always check the `bool` return of `TryConvertChildrenToRenderables`.
- **Don't use priorities 10-190** — reserved for RazorConsole built-ins unless intentionally overriding.
- **Don't store state between translations** — translators must be thread-safe.

---

## Writing a Native Translator — Template (Phase 2)

```csharp
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

public sealed class MyTranslator : IVdomElementTranslator
{
    // Slot into 200-999 range for MandoCode custom translators
    public int Priority => 210;

    public bool TryTranslate(VNode node, TranslationContext context, out IRenderable? renderable)
    {
        renderable = null;

        // 1. GUARD — fail fast if this isn't our node
        if (node.Kind != VNodeKind.Element)
            return false;

        if (!string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!VdomSpectreTranslator.HasClass(node, "my-component"))
            return false;

        // 2. EXTRACT — read attributes with safe parsing + defaults
        var title = VdomSpectreTranslator.GetAttribute(node, "data-title") ?? "Untitled";
        var width = VdomSpectreTranslator.TryParsePositiveInt(
            VdomSpectreTranslator.GetAttribute(node, "data-width"),
            out var w) ? w : 50;

        // 3. CHILDREN — translate child nodes recursively
        if (!VdomSpectreTranslator.TryConvertChildrenToRenderables(
            node.Children, context, out var children))
        {
            return false;
        }

        var content = VdomSpectreTranslator.ComposeChildContent(children);

        // 4. BUILD — create the Spectre.Console renderable
        renderable = new Panel(content)
            .Header($"[cyan]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);

        return true;
    }
}
```

---

## Testing

### Phase 1 Manual QA Checklist

| Test | Expected |
|------|----------|
| AI response renders with colors, bold, code blocks | Identical to current |
| OSC 8 file links are clickable | Identical to current |
| Operation displays (Read, Write, Update, Search, etc.) | Identical to current |
| Diff approval panels with colored lines | Identical to current |
| Music panel with gradient volume bar | Identical to current |
| `@` file autocomplete dropdown | Works — stays on real stdout |
| `/` command autocomplete dropdown | Works — stays on real stdout |
| Paste detection | Works — stays on real stdout |
| Arrow key cursor navigation | Works — stays on real stdout |
| Spinner animation | Works — stays imperative |
| `/copy` and `/copy-code` | Works — reads from `ChatMsg.Text` |
| Token tracking display | Identical to current |
| Ctrl+C cancellation | Works — stays imperative |

### Phase 2 Unit Test Template

```csharp
using Xunit;
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;

public class OperationDisplayTranslatorTests
{
    [Fact]
    public void Handles_ReadOperation()
    {
        var translator = new OperationDisplayTranslator();
        var node = VNode.CreateElement("div");
        node.SetAttribute("class", "op-display");
        node.SetAttribute("data-op", "Read");
        node.SetAttribute("data-file", "src/Program.cs");
        node.SetAttribute("data-lines", "42");

        var context = new TranslationContext(new VdomSpectreTranslator());
        var success = translator.TryTranslate(node, context, out var renderable);

        Assert.True(success);
        Assert.NotNull(renderable);
    }

    [Fact]
    public void Ignores_NonOperationDiv()
    {
        var translator = new OperationDisplayTranslator();
        var node = VNode.CreateElement("div");

        var context = new TranslationContext(new VdomSpectreTranslator());
        var success = translator.TryTranslate(node, context, out var renderable);

        Assert.False(success);
        Assert.Null(renderable);
    }
}
```

---

## Reference Links

- **RazorConsole Custom Translators docs:** `design-doc/custom-translators.md` in the RazorConsole repo
- **RazorConsole built-in component reference:** `design-doc/builtin-components.md` in the RazorConsole repo
- **Built-in translator priorities:** 10 (text) through 1000 (fallback)
- **Spectre.Console docs:** https://spectreconsole.net
- **MandoCode rendering services:** `Services/MarkdownRenderer.cs`, `Services/OperationDisplayRenderer.cs`, `Services/DiffApprovalHandler.cs`, `Services/MusicPlayerUI.cs`, `Services/SyntaxHighlighter.cs`
