# Autocomplete Skill: RazorConsole Focus Management

## The Problem

Building autocomplete in a RazorConsole terminal app requires two conflicting capabilities:
- **Typing to filter** — requires `TextInput` to have keyboard focus
- **Arrow key navigation** — requires `Select` to have keyboard focus

RazorConsole's focus model: **only one component receives keyboard input at a time**, determined by the `FocusManager`.

## Lessons Learned (What Doesn't Work)

### 1. Dynamic FocusOrder Swapping
**Attempted:** Change `FocusOrder` values at runtime to shift focus between TextInput and Select.
**Result:** Unreliable. RazorConsole doesn't consistently re-evaluate focus when FocusOrder changes during a render cycle. Leads to one component "winning" unpredictably.

### 2. Disabling TextInput to Force Select Focus
**Attempted:** Set `Disabled="true"` on TextInput when Select should have focus.
**Result:** Works for giving Select focus, but the user can't see what they typed (disabled styling dims/hides text). Re-enabling doesn't reliably restore focus.

### 3. Removing TextInput from VDOM Tree
**Attempted:** Conditionally render `@if (!_showSelect) { <TextInput .../> }` to remove TextInput and let Select be the only focusable component.
**Result:** Removing a component during its own `OnInput` event handler causes rendering issues. The component that triggered the event disappears mid-callback.

### 4. Debounce Timer Auto-Switch
**Attempted:** After N ms of no typing, automatically switch from TextInput to Select.
**Result:** Timer fires while user is still typing (just paused briefly), locking them out of TextInput. Race conditions between timer callbacks and user input.

### 5. Multiple Select Components at Different FocusOrders
**Attempted:** Render a "preview" Select at `FocusOrder="1"` alongside TextInput at `FocusOrder="0"`, hoping Tab would cycle focus.
**Result:** TextInput always wins focus. The preview Select renders visually but never receives keyboard input.

## What Works: The FocusManager Approach

### Key Discovery
RazorConsole has an **injectable `FocusManager`** service with programmatic focus control:

```csharp
// Injectable in any Razor component
@inject RazorConsole.Core.Focus.FocusManager FocusManager

// Programmatically move focus to a specific component
await FocusManager.FocusAsync(key, CancellationToken.None);

// Navigate focus order
await FocusManager.FocusNextAsync(CancellationToken.None);
await FocusManager.FocusPreviousAsync(CancellationToken.None);

// Query focus state
bool isFocused = FocusManager.IsFocused(key);
string currentKey = FocusManager.CurrentFocusKey;
```

### The Two-Mode Architecture

Instead of trying to have both components active simultaneously, use two explicit modes with `FocusManager` as the bridge:

**Mode 1: Typing (TextInput focused)**
- TextInput has focus, user types freely
- Filtered results show as non-interactive `Markup` components (visual only)
- First match highlighted in green, others in grey
- Enter key triggers mode switch (if multiple matches) or direct selection (if single match)

**Mode 2: Navigation (Select focused)**
- Select renders with filtered options + ❌ Cancel + 🔙 Back
- `FocusManager.FocusAsync()` guarantees Select gets focus
- TextInput disabled (visual only, shows current input path)
- Arrow keys navigate, Enter commits selection
- Cancel returns to Mode 1

### Select Component API Surface

```razor
<Select Options="@_options.ToArray()"
        Value="@_selectedValue"
        ValueChanged="@((string v) => HandleSelection(v))"
        FocusedValue="@_focusedValue"
        FocusedValueChanged="@((string v) => _focusedValue = v)"
        IsFocused="@_selectHasFocus"
        IsFocusedChanged="@((bool f) => _selectHasFocus = f)"
        SelectedOptionForeground="Color.Green"
        SelectedOptionDecoration="Decoration.Bold"
        OptionForeground="Color.Grey"
        SelectedIndicator="@('>')"
        UnselectedIndicator="@(' ')"
        FocusOrder="0"
        Expand="true" />
```

Key distinction:
- `FocusedValue` = currently highlighted item (arrow navigation)
- `Value` = committed selection (Enter pressed)
- `IsFocused` = whether Select has keyboard focus

### Markup Component for Preview

```razor
<Markup Content="@text" Foreground="Color.Green" Decoration="Decoration.Bold" />
```

**Important:** The parameter is `Content`, NOT `Text`. The component does NOT parse `[green]...[/]` Spectre markup syntax — use `Foreground` and `Decoration` parameters for styling.

## RazorConsole Component Reference

### Focus-Aware Components
| Component | FocusOrder | Keyboard Events |
|-----------|-----------|-----------------|
| `TextInput` | Yes | `OnInput`, `OnSubmit`, `OnFocus`, `OnBlur` |
| `Select<T>` | Yes | Arrow Up/Down, Enter, Escape, type-ahead |
| `TextButton` | Yes | Enter/Space to activate |
| `Scrollable<T>` | Built-in | Up/Down/PageUp/PageDown for scrolling |

### Overlay System
- `ModalWindow` — `IsOpened` (bool), `ChildContent` (RenderFragment only)
- No border/title styling on ModalWindow (only `AdditionalAttributes`)
- Overlay rendering via `TranslationContext.CollectedOverlays` middleware

### VDOM Timing Constraint
After `InvokeAsync(StateHasChanged)` that changes the component tree, wait **100ms** before any imperative `Console.Write` calls. The VDOM repaint is async and will overwrite imperative output without this delay.

## Architecture Pattern

```
┌─────────────────────────────────────────┐
│          InputStateMachine              │
│  Pure logic — zero Console calls        │
│  UpdateText(string) → InputAction       │
│  AcceptSelection(string) → string?      │
│  State: InputRenderState snapshot       │
└──────────┬──────────────────────────────┘
           │
┌──────────▼──────────────────────────────┐
│        PromptInput.razor                │
│                                         │
│  Mode 1: Typing                         │
│    TextInput (focused) + Markup preview │
│                                         │
│  Mode 2: Navigation                     │
│    Select (focused via FocusManager)    │
│    TextInput (disabled, visual only)    │
│                                         │
│  Bridge: FocusManager.FocusAsync()      │
└─────────────────────────────────────────┘
```
