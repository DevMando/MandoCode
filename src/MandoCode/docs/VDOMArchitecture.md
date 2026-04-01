# VDOM Architecture

MandoCode's rendering bridge between imperative ANSI output and RazorConsole's Blazor-based VDOM system. Covers the completed two-phase integration, the translator API, and the roadmap for native translators.

---

## Table of Contents

1. [Overview](#overview)
2. [Phase 1: AnsiPassthrough Infrastructure](#phase-1-ansipassthrough-infrastructure)
3. [Phase 2: BuildRenderable Pattern](#phase-2-buildrenderable-pattern)
4. [Files Created & Modified](#files-created--modified)
5. [Bug Fixes](#bug-fixes)
6. [Architecture Decisions](#architecture-decisions)
7. [What Stays Imperative](#what-stays-imperative)
8. [RazorConsole Translator API Reference](#razorconsole-translator-api-reference)
9. [Rules for MandoCode Translators](#rules-for-mandocode-translators)
10. [Future Work: Native Translators](#future-work-native-translators)

---

## Overview

MandoCode's `App.razor` is built on RazorConsole (Blazor + Spectre.Console). Prior to the VDOM integration, all rendering was purely imperative: `Console.Write` with raw ANSI escape codes and `AnsiConsole.Write` with Spectre.Console widgets. This meant no component isolation, no `StateHasChanged()`, and no ability to re-render regions independently.

The two-phase approach:

1. **Phase 1** (Completed): VDOM infrastructure — translator, components, capture utility, data model — that can replay ANSI output through the Blazor component tree
2. **Phase 2** (Completed): Refactored all renderers to `BuildRenderable()` methods returning `IRenderable` objects directly, eliminating stdout capture

---

## Phase 1: AnsiPassthrough Infrastructure

### RazorConsole's Real API

Through DLL reflection of `RazorConsole.Core v0.5.0-alpha`, we discovered the actual API uses `ITranslationMiddleware` (not the originally planned `IVdomElementTranslator`):

```csharp
public interface ITranslationMiddleware
{
    IRenderable Translate(TranslationContext context, TranslationDelegate next, VNode node);
}

// Registration
services.AddSingleton<ITranslationMiddleware, AnsiPassthroughTranslator>();
```

**Key types and namespaces:**
- `ITranslationMiddleware` — `RazorConsole.Core.Abstractions.Rendering`
- `TranslationContext` — `RazorConsole.Core.Rendering.Translation.Contexts`
- `TranslationDelegate` — `RazorConsole.Core.Abstractions.Rendering`
- `VNode`, `VNodeKind` — `RazorConsole.Core.Vdom`
- `VdomSpectreTranslator` (static helpers) — `RazorConsole.Core.Rendering.Vdom`

### What Was Built

1. **`AnsiPassthroughRenderable`** — Custom `IRenderable` that yields a pre-built ANSI string as a single `Segment`
2. **`AnsiPassthroughTranslator`** — `ITranslationMiddleware` intercepting `<div class="ansi-region" data-content="...">` nodes
3. **`AnsiCaptureWriter`** — Captures both `Console.Out` and `AnsiConsole.Console` output into a string
4. **`ChatMsg`** — Data model for chat history (raw text + rendered ANSI)
5. **Four Razor components** — `ChatMessage.razor`, `OperationDisplay.razor`, `MusicStatus.razor`, `StatusBar.razor`

### Phase 1 Outcome

The VDOM `@foreach` loop over chat history caused **input freezing after 2-3 turns** because RazorConsole's live display repaints conflicted with `CommandAutocomplete`'s cursor management. The loop was disabled — chat history accumulates in `_messages` for `/copy` and future use. This led to Phase 2.

---

## Phase 2: BuildRenderable Pattern

### Pattern

Every renderer follows a dual-method pattern:

```csharp
// Thin wrapper for imperative callers
public void Render(OperationDisplayEvent e)
{
    AnsiConsole.Write(BuildRenderable(e));
}

// Returns composable IRenderable — no console I/O
public IRenderable BuildRenderable(OperationDisplayEvent e)
{
    var sb = new StringBuilder();
    // ... build ANSI string ...
    return new AnsiPassthroughRenderable(sb.ToString());
}
```

### Renderer Refactoring

**MarkdownRenderer.cs** (~250 lines) — Returns `Rows(...)` composition of mixed types:
- Inline text → `AnsiPassthroughRenderable` via `StringBuilder`
- Thematic breaks → Native `Rule()` widget
- Code blocks → Native `Panel()` with syntax highlighting
- Tables → Native `Table()` widget

**OperationDisplayRenderer.cs** (~131 lines) — All `Console.WriteLine()` → `sb.AppendLine()`, returns `AnsiPassthroughRenderable`.

**MusicPlayerUI.cs** (~104 lines) — `BuildStatusRenderable()` and `BuildTrackListRenderable()` return `AnsiPassthroughRenderable`.

### Component Updates

Components use `BuildRenderable()` directly instead of `AnsiCaptureWriter.Capture()`:

```csharp
// OperationDisplay.razor
protected override void OnParametersSet()
{
    var renderable = Renderer.BuildRenderable(Operation);
    _rendered = renderable is AnsiPassthroughRenderable passthrough
        ? passthrough.Content
        : "";
}
```

---

## Files Created & Modified

### New Files (8)

| File | Purpose |
|------|---------|
| `Translators/AnsiPassthroughRenderable.cs` | Custom `IRenderable` for raw ANSI strings |
| `Translators/AnsiPassthroughTranslator.cs` | VDOM middleware for `<div class="ansi-region">` |
| `Services/AnsiCaptureWriter.cs` | Console output capture (retained, no longer used in rendering) |
| `Models/ChatMsg.cs` | Chat message data model |
| `Components/ChatMessage.razor` | Chat message Blazor component |
| `Components/OperationDisplay.razor` | Operation display Blazor component |
| `Components/MusicStatus.razor` | Music status Blazor component |
| `Components/StatusBar.razor` | Token count display Blazor component |

### Modified Files

| File | Changes |
|------|---------|
| `Services/MarkdownRenderer.cs` | Full `BuildRenderable` refactor |
| `Services/OperationDisplayRenderer.cs` | `BuildRenderable` refactor |
| `Services/MusicPlayerUI.cs` | `BuildRenderable` refactor |
| `Components/App.razor` | Chat history tracking, VDOM prep |
| `Program.cs` | `AnsiPassthroughTranslator` DI registration |
| `Services/CommandAutocomplete.cs` | Paste cursor fix (`EnsureBufferSpace`) |

---

## Bug Fixes

### 1. Input Freezing After 2-3 Chat Turns

VDOM `@foreach` repaint cycle conflicted with `CommandAutocomplete`'s cursor management. **Fix**: Disabled VDOM rendering loop; display stays imperative.

### 2. Cursor/Edit Broken After Pasting Long Text

`RedrawInput()` didn't ensure enough terminal buffer rows below `cursorTop`. **Fix**: Added `EnsureBufferSpace(ref cursorTop, linesNeeded)`.

### 3. AI Responses Rendered as Horizontal Lines

`AnsiCaptureWriter` couldn't properly capture Spectre.Console widget output (cached `TextWriter`), and captured layout broke at different cursor positions. **Fix**: Phase 2's `BuildRenderable()` pattern eliminates capture entirely.

---

## Architecture Decisions

### Why Not Capture/Replay?

1. Spectre.Console caches `Console.Out` at startup — `Console.SetOut()` doesn't redirect widget output
2. Even with full redirect, layout calculations are baked into the captured string — non-replayable at different positions
3. Capture is fundamentally output-centric, not data-centric

### Why BuildRenderable?

Treats rendering as **data construction**: inline text via `StringBuilder` → `AnsiPassthroughRenderable`, block-level widgets as native `IRenderable` objects, composed via `Rows(...)`. Works both imperatively and through VDOM.

### Why Keep Imperative Rendering?

The interactive input loop uses `Console.ReadKey(intercept: true)` with direct cursor manipulation, which conflicts with RazorConsole's live display repaint. The `BuildRenderable()` pattern means we're architecturally ready for VDOM rendering, but the trigger stays imperative until input is also VDOM-aware.

---

## What Stays Imperative

| Component | Reason |
|-----------|--------|
| `CommandAutocomplete.ReadLineWithAutocomplete()` | `Console.ReadKey(intercept: true)`, cursor manipulation |
| Prompt rendering (`"> "`) | Must write to real stdout before `ReadKey` |
| `SpinnerService` | Animated cursor-position output on current line |
| Approval prompts (`SelectionPrompt`, `TextPrompt`) | Spectre.Console interactive widgets that block for input |
| Music visualizer (OSC 0 title bar) | Terminal control sequence, not scrollback |
| OSC taskbar progress (OSC 9;4) | Terminal control sequence |
| OSC clipboard (`/copy`, `/copy-code`) | Terminal control sequence |
| Task plan interactive prompts | `SelectionPrompt` — must block for user input |
| Learn mode, config wizard | Interactive flows |

---

## RazorConsole Translator API Reference

### Translation Pipeline

```
Razor Component → VNode Tree → Translators (by priority) → IRenderable → Terminal
```

Translators are tried in ascending priority order. First one that returns `true` wins. Priority 1000 is the built-in fallback.

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
VdomSpectreTranslator.ComposeChildContent(children);
```

---

## Rules for MandoCode Translators

### Do

- **Fail fast** — check `node.Kind`, `node.TagName`, then attributes/classes. Return `false` immediately on mismatch.
- **Use case-insensitive comparison** — `string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase)`.
- **Use utility methods** — `GetAttribute`, `HasClass`, `TryParsePositiveInt`, `TryConvertChildrenToRenderables`.
- **Be stateless** — no mutable instance fields. All data comes from the VNode.
- **Translate children via context** — always use `context.TryTranslate()` or `TryConvertChildrenToRenderables()`.
- **Create new renderable instances** — don't mutate or reuse.
- **Use DI for dependencies** — inject via constructor, register with `AddVdomTranslator<T>()`.

### Don't

- **Don't call `Console.Write`** in native translators — produce `IRenderable` objects instead.
- **Don't use raw ANSI escape codes** — use Spectre.Console `Style`, `Color`, `Markup`.
- **Don't assume child translation succeeds** — check the `bool` return.
- **Don't use priorities 10-190** — reserved for RazorConsole built-ins.
- **Don't store state between translations** — translators must be thread-safe.

---

## Phase 3: Input State Machine & VDOM Input (Completed)

### Problem

`CommandAutocomplete.cs` (852 lines) welded three systems together: console I/O (`Console.ReadKey`), autocomplete state machine, and input editing. The blocking `Console.ReadKey` loop fundamentally conflicted with Blazor's render cycle, causing input freezing when VDOM components were active.

### Solution: Extract → Decouple → Integrate

**Step 1: Pure State Machine** — `InputStateMachine.cs` (~700 lines) processes key events via `ProcessKey(ConsoleKeyInfo)` → returns `InputAction` enum. Zero console calls. Handles all autocomplete modes, history navigation, cursor tracking, text editing, paste processing, directory drill-down.

**Step 2: Action Enum Protocol** — `InputAction` (11 variants) tells the orchestrator what visual update is needed. Clean seam between logic and I/O.

**Step 3: Console Abstraction** — `ConsoleInputReader` wraps `Console.ReadKey` with virtual methods for test doubles.

**Step 4: Thin Orchestrator** — `CommandAutocomplete.cs` rewritten to ~290 lines. Loop: read key → detect paste → call state machine → switch on action → render.

**Step 5: VDOM Text API** — `UpdateText(string)`, `SubmitInput(string)`, `AcceptSelection(string)` enable Blazor components to drive the state machine via text-level changes (no `ConsoleKeyInfo` needed).

**Step 6: VDOM Integration** — `PromptInput.razor` uses shared `InputStateMachine` (DI singleton) via `TextInput` + `Select` components. `App.razor` uses `TaskCompletionSource<string>` to bridge: sets `_waitingForInput = true` → Blazor renders PromptInput → user submits → TCS completes → loop continues. Non-blocking async, no `Console.ReadKey` conflict.

### New Files (4)

| File | Purpose |
|------|---------|
| `Models/InputAction.cs` | Action enum — state machine → orchestrator protocol |
| `Models/InputRenderState.cs` | Render state snapshot + `AutocompleteMode` enum |
| `Services/ConsoleInputReader.cs` | Thin `Console.ReadKey` wrapper for testability |
| `Services/InputStateMachine.cs` | Pure logic state machine (~700 lines) |

### Modified Files

| File | Changes |
|------|---------|
| `Services/CommandAutocomplete.cs` | Rewritten as thin orchestrator (852→290 lines) |
| `Components/PromptInput.razor` | Refactored to use shared `InputStateMachine` |
| `Components/App.razor` | VDOM input via `WaitForVdomInputAsync()` + `TaskCompletionSource` |
| `Program.cs` | `InputStateMachine` DI registration |

### Architecture

```
┌─────────────────────────────────────────────────┐
│                InputStateMachine                 │
│  Pure logic — no Console calls                   │
│  ProcessKey(ConsoleKeyInfo) → InputAction        │
│  UpdateText(string) → InputAction   (VDOM path)  │
│  State: InputRenderState snapshot                │
└────────────┬──────────────────┬──────────────────┘
             │                  │
    ┌────────▼────────┐  ┌─────▼──────────────┐
    │ CommandAuto-     │  │ PromptInput.razor   │
    │ complete         │  │ (VDOM path)         │
    │ (imperative)     │  │ TextInput + Select  │
    │ Console.ReadKey  │  │ StateHasChanged()   │
    │ Direct cursor    │  │ TaskCompletion-     │
    │ manipulation     │  │ Source bridge        │
    └─────────────────┘  └────────────────────┘
```

---

## Future Work: Native Translators

### Enable VDOM Chat Rendering

With the input system now decoupled via `InputStateMachine` and `PromptInput.razor`, the `@foreach` loop over `_messages` can be re-enabled for component-isolated chat history with independent re-rendering.

### Native Translator Roadmap

Replace `AnsiPassthroughRenderable` wrappers with translators producing real `IRenderable` objects for surgical VDOM diffing.

**Proposed priority map:**

| Priority | Translator | Replaces |
|----------|------------|----------|
| 200 | `AnsiPassthroughTranslator` | (keeps working alongside native translators) |
| 210 | `OperationDisplayTranslator` | `OperationDisplayRenderer.cs` |
| 220 | `DiffPanelTranslator` | `DiffApprovalHandler.RenderDiffPanel` |
| 300 | `MusicPanelTranslator` | `MusicPlayerUI.cs` |
| 400 | `MarkdownBlockTranslator` | `MarkdownRenderer.cs` |
| 500 | `HyperlinkTranslator` | OSC 8 link generation |

**Recommended conversion order:**
1. OperationDisplay — stateless, self-contained, high frequency
2. DiffPanel — visual complexity but well-defined structure
3. MusicPanel — RGB gradients map to `Color.FromRgb()`
4. MarkdownBlock — most complex (Markdig parsing, nested rendering, OSC 8). Do last.

### Native Translator Template

```csharp
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

public sealed class MyTranslator : IVdomElementTranslator
{
    public int Priority => 210;

    public bool TryTranslate(VNode node, TranslationContext context, out IRenderable? renderable)
    {
        renderable = null;

        // 1. GUARD
        if (node.Kind != VNodeKind.Element) return false;
        if (!string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase)) return false;
        if (!VdomSpectreTranslator.HasClass(node, "my-component")) return false;

        // 2. EXTRACT
        var title = VdomSpectreTranslator.GetAttribute(node, "data-title") ?? "Untitled";

        // 3. CHILDREN
        if (!VdomSpectreTranslator.TryConvertChildrenToRenderables(
            node.Children, context, out var children))
            return false;

        var content = VdomSpectreTranslator.ComposeChildContent(children);

        // 4. BUILD
        renderable = new Panel(content)
            .Header($"[cyan]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded);

        return true;
    }
}
```

### Remove AnsiCaptureWriter

Once all renderers are confirmed working with `BuildRenderable()` and the capture utility is no longer needed, `AnsiCaptureWriter.cs` can be removed.
