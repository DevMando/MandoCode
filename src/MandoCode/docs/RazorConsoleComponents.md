# RazorConsole Component Catalog

Reference for all components available in RazorConsole. These wrap Spectre.Console constructs and are usable in `.razor` files.

---

## Layout Components

| Component | Purpose |
|-----------|---------|
| **Align** | Position child content horizontally and vertically within a fixed box |
| **Columns** | Arrange items side-by-side, optionally stretching to fill the console width |
| **FlexBox** | CSS-like flexbox layout with configurable direction, justification, alignment, wrapping, and gap |
| **Grid** | Multi-row, multi-column layouts with precise cell control |
| **Padder** | Add outer padding around child content without altering the child itself |
| **Rows** | Stack child content vertically with optional expansion behavior |

## Container Components

| Component | Purpose |
|-----------|---------|
| **Border** | Draw Spectre borders with customizable style, color, and padding |
| **Panel** | Frame content inside a titled container with border and padding options |
| **ModalWindow** | Display modal overlay windows with automatic centering using z-index positioning |

## Input Components

| Component | Purpose |
|-----------|---------|
| **TextInput** | Capture user input with optional masking and change handlers |
| **TextButton** | Display clickable text with focus and pressed-state styling |
| **Select** | Present a focusable option list with keyboard navigation |

## Display Components

| Component | Purpose |
|-----------|---------|
| **Markup** | Emit styled text with Spectre markup tags |
| **Markdown** | Render markdown string |
| **Figlet** | Render big ASCII art text using FIGlet fonts |
| **SyntaxHighlighter** | Colorize code snippets using ColorCode themes |
| **Table** | Display structured data in formatted tables with headers, borders, and rich cell content |
| **Spinner** | Show animated progress indicators using Spectre spinner presets |
| **Newline** | Insert intentional spacing between renderables |

## Chart Components

| Component | Purpose |
|-----------|---------|
| **BarChart** | Horizontal bar chart with optional label, colors, and value display |
| **BreakdownChart** | Colorful breakdown (pie-style) chart showing proportions with optional legend and values |
| **StepChart** | Terminal step chart using Unicode box-drawing characters for discrete value changes |

## Scrolling Components

| Component | Purpose |
|-----------|---------|
| **Scrollable** | Keyboard-based vertical scrolling through content, including nested components or HTML markup |
| **ViewHeightScrollable** | Scroll any content line-by-line with keyboard navigation and optional embedded scrollbar support for Panel, Border, and Table |

## Canvas

| Component | Purpose |
|-----------|---------|
| **SpectreCanvas** | Draw an array of pixels with different colors |

---

## Components Currently Used in MandoCode

| Component | Where Used |
|-----------|------------|
| `Markup` | `App.razor` — project info, status messages |
| `Panel` | `App.razor` — model warning display |
| `Rows` | `App.razor` — vertical layout for startup info |
| `Select` | `PromptInput.razor` — autocomplete dropdown |
| `TextInput` | `PromptInput.razor` — user input capture |

## Components Worth Exploring

| Component | Potential Use |
|-----------|---------------|
| `Markdown` | Render AI responses natively in VDOM instead of imperative `MarkdownRenderer` |
| `Scrollable` / `ViewHeightScrollable` | Long AI responses, chat history scrollback |
| `Spinner` | Replace imperative `SpinnerService` with VDOM spinner |
| `Table` | `/help` command output via VDOM instead of imperative |
| `BarChart` | Token usage visualization |
| `FlexBox` | Side-by-side diff display |
| `ModalWindow` | Config wizard, confirmation dialogs |
