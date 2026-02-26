# OSC (Operating System Command) Terminal Escape Codes

Reference guide for terminal escape codes useful in MandoCode development.

## General Syntax

```
ESC ] <code> ; <parameters> <terminator>
```

- **ESC** = `\x1b` (0x1B)
- **Terminator** = BEL (`\x07`) or ST (`\x1b\\`)

In C#: `\u001b` for ESC, `\u0007` for BEL.

---

## OSC 0 — Set Window Title + Icon Name

Sets both the window/tab title and icon name. **We use this for the music visualizer.**

```csharp
Console.Write("\x1b]0;MandoCode - AI Terminal\x07");
```

**Support:** Windows Terminal, iTerm2, kitty, alacritty, WezTerm, GNOME Terminal, xterm — basically everything.

---

## OSC 1 / OSC 2 — Set Icon Name or Window Title Separately

```csharp
Console.Write("\x1b]1;Tab Title Only\x07");   // Icon/tab name
Console.Write("\x1b]2;Window Title Only\x07"); // Window title
```

**Support:** Most terminals. Windows Terminal treats all three (0/1/2) the same.

---

## OSC 8 — Clickable Hyperlinks

Creates clickable links in terminal output — like HTML `<a>` tags.

```csharp
// Open link tag, then text, then close link tag
Console.Write("\x1b]8;;https://github.com/DevMando/MandoCode\x07");
Console.Write("MandoCode Repository");
Console.Write("\x1b]8;;\x07");

// File links (opens in default editor)
Console.Write($"\x1b]8;;file:///path/to/file.cs\x07");
Console.Write("file.cs");
Console.Write("\x1b]8;;\x07");

// Helper method
public static string Hyperlink(string uri, string text)
    => $"\x1b]8;;{uri}\x07{text}\x1b]8;;\x07";
```

**Support:** Windows Terminal (v1.4+), iTerm2, kitty, alacritty (v0.11+), WezTerm, GNOME Terminal, VS Code terminal.

**Used in MandoCode:**
- `FileLink()` in `App.razor` — wraps file paths in operation displays (Read, Write, Update, Delete, Glob, Search, Diff panel) as clickable `file://` links
- `WriteHyperlink()` in `MarkdownRenderer.cs` — renders markdown URLs and bare URLs as clickable links in AI output

---

## OSC 9 — Notifications & Progress Bars

### Desktop Notifications (iTerm2)
```csharp
Console.Write("\x1b]9;Build completed!\x07");
```

### Progress Bar in Taskbar (Windows Terminal)
```csharp
Console.Write("\x1b]9;4;1;75\x07");  // 75% progress, success state
Console.Write("\x1b]9;4;3\x07");     // Indeterminate (spinning)
Console.Write("\x1b]9;4;2;50\x07");  // Error state at 50%
Console.Write("\x1b]9;4;0\x07");     // Clear progress bar
```

Progress states: `0`=clear, `1`=progress, `2`=error, `3`=indeterminate, `4`=warning

### Set CWD (Windows Terminal / ConEmu)
```csharp
Console.Write($"\x1b]9;9;{Directory.GetCurrentDirectory()}\x07");
```

**Used in MandoCode:**
- `SetTaskbarIndeterminate()` — pulses taskbar during AI requests (wired into `StartSpinner()`)
- `SetTaskbarProgress()` — fills taskbar step-by-step during task plan execution
- `SetTaskbarError()` — shows red on step failure
- `ClearTaskbarProgress()` — clears on completion (wired into `StopSpinner()` and plan completion)
- OSC 9;9 — emitted by `HandleShellCommand()` after `cd` to sync Windows Terminal CWD

---

## OSC 10/11/12 — Foreground, Background, Cursor Colors

```csharp
// Set foreground to amber
Console.Write("\x1b]10;rgb:ff/b0/00\x07");

// Set background to dark
Console.Write("\x1b]11;rgb:1e/1e/2e\x07");

// Set cursor color to green
Console.Write("\x1b]12;rgb:00/ff/00\x07");

// Query current background (for dark/light theme detection)
Console.Write("\x1b]11;?\x07");

// Reset to defaults
Console.Write("\x1b]110\x07");  // Reset foreground
Console.Write("\x1b]111\x07");  // Reset background
Console.Write("\x1b]112\x07");  // Reset cursor
```

**Support:** Most terminals. Windows Terminal has partial support.

**Use case:** Detect dark/light theme by querying background color.

---

## OSC 4 — Change Color Palette Entries

Modify the 256-color palette at runtime.

```csharp
// Set color index 1 (red) to custom color
Console.Write("\x1b]4;1;rgb:ff/45/00\x07");

// Set multiple at once
Console.Write("\x1b]4;0;rgb:1e/1e/2e;1;rgb:f3/8b/a8;2;rgb:a6/e3/a1\x07");

// Reset all palette colors
Console.Write("\x1b]104\x07");

// Reset specific index
Console.Write("\x1b]104;1\x07");
```

**Support:** Most terminals including Windows Terminal.

---

## OSC 7 — Set Current Working Directory

Tells the terminal the current directory (enables "open new tab in same dir").

```csharp
// Standard (macOS Terminal, GNOME, iTerm2, kitty)
string host = Environment.MachineName;
string cwd = Directory.GetCurrentDirectory();
Console.Write($"\x1b]7;file://{host}{cwd}\x07");

// Windows Terminal prefers OSC 9;9 (see above)
Console.Write($"\x1b]9;9;{cwd}\x07");
```

---

## OSC 52 — Clipboard Access (Copy/Paste)

**The big one.** Read/write to the system clipboard via escape codes.

### Clipboard Targets

| Target | Meaning | Platform |
|--------|---------|----------|
| `c` | System clipboard | All |
| `p` | PRIMARY selection | Linux/X11 |
| `s` | SELECT | xterm |

### Copy to Clipboard (Write)

```csharp
public static void CopyToClipboard(string text)
{
    string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    Console.Write($"\x1b]52;c;{base64}\x07");
}

// Usage
CopyToClipboard("Hello from MandoCode!");
CopyToClipboard("multi-line\ncode snippet\nworks too");
```

### Query Clipboard (Read) — Most terminals block this

```csharp
Console.Write("\x1b]52;c;?\x07");
// Terminal responds with: ESC ] 52 ; c ; <base64-data> ST
```

### Clear Clipboard

```csharp
Console.Write("\x1b]52;c;!\x07");
```

### Terminal Support

| Terminal | Write (Copy) | Read (Paste) | Notes |
|----------|-------------|--------------|-------|
| **Windows Terminal** | **Yes (default on)** | No | Most relevant for us |
| iTerm2 | Yes | Yes (opt-in) | Requires enable in prefs |
| kitty | Yes | Yes (opt-in) | `clipboard_control` setting |
| alacritty | Yes | No | Write only |
| WezTerm | Yes | Yes (opt-in) | Configurable |
| tmux | Yes | Yes | `set-clipboard` option |
| GNOME Terminal | No | No | Not supported |

### Max payload: ~74,994 bytes of text (base64 encoded within 100KB limit)

### Security Notes

- **Write is generally safe** — just puts text on the clipboard
- **Read is a security risk** — could exfiltrate passwords/tokens from clipboard
- Windows Terminal intentionally blocks read for security
- Over SSH, OSC 52 traverses the connection (useful but be aware)

---

## OSC 99 — Desktop Notifications (kitty protocol)

Richer notification protocol than OSC 9, but kitty-specific.

```csharp
// Simple notification
Console.Write("\x1b]99;;Build complete!\x1b\\");

// With title and body
Console.Write("\x1b]99;i=build1:d=0;Build Status\x1b\\");
Console.Write("\x1b]99;i=build1:p=body;All 42 tests passed.\x1b\\");

// Critical urgency
Console.Write("\x1b]99;i=err1:d=0:u=2;ERROR\x1b\\");
Console.Write("\x1b]99;i=err1:p=body;3 compilation errors\x1b\\");
```

**Support:** kitty only (Ghostty partial).

---

## OSC 1337 — iTerm2 Inline Images

Display images directly in the terminal. **Does NOT work on Windows Terminal.**

```csharp
byte[] imageBytes = File.ReadAllBytes("logo.png");
string base64 = Convert.ToBase64String(imageBytes);
string name = Convert.ToBase64String(Encoding.UTF8.GetBytes("logo.png"));
Console.Write($"\x1b]1337;File=name={name};size={imageBytes.Length};inline=1:{base64}\x07");

// With specific width
Console.Write($"\x1b]1337;File=inline=1;width=10:{base64}\x07");
```

**Support:** iTerm2, WezTerm, mintty. NOT Windows Terminal, kitty, alacritty.

---

## C# Helper Class

```csharp
using System.Text;

public static class Osc
{
    private const string BEL = "\x07";
    private const string PREFIX = "\x1b]";

    public static string SetTitle(string title)
        => $"{PREFIX}0;{title}{BEL}";

    public static string Hyperlink(string uri, string text)
        => $"{PREFIX}8;;{uri}{BEL}{text}{PREFIX}8;;{BEL}";

    public static string CopyToClipboard(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return $"{PREFIX}52;c;{b64}{BEL}";
    }

    public static string Notify(string message)
        => $"{PREFIX}9;{message}{BEL}";

    public static string ProgressBar(int state, int percent = 0)
        => $"{PREFIX}9;4;{state};{percent}{BEL}";

    public static string SetForeground(string rgb) => $"{PREFIX}10;rgb:{rgb}{BEL}";
    public static string SetBackground(string rgb) => $"{PREFIX}11;rgb:{rgb}{BEL}";
    public static string SetCursorColor(string rgb) => $"{PREFIX}12;rgb:{rgb}{BEL}";

    public static string ResetForeground() => $"{PREFIX}110{BEL}";
    public static string ResetBackground() => $"{PREFIX}111{BEL}";
    public static string ResetCursorColor() => $"{PREFIX}112{BEL}";
}
```

---

## What Works on Windows Terminal (Our Primary Target)

| Feature | Works? | Notes |
|---------|--------|-------|
| OSC 0/1/2 — Title | Yes | Already using for music visualizer |
| OSC 4 — Palette | Yes | Set colors at runtime |
| OSC 7 — CWD | Yes | Use OSC 9;9 variant preferred |
| OSC 8 — Hyperlinks | Yes | Since v1.4 (2020) |
| OSC 9 — Notifications | No | But progress bar (9;4) works |
| OSC 9;4 — Progress | Yes | Taskbar progress indicator |
| OSC 9;9 — CWD | Yes | Preferred over OSC 7 on Windows |
| OSC 10/11 — Colors | Partial | Set works, query partial |
| OSC 52 — Clipboard | Write only | Read blocked for security |
| OSC 1337 — Images | No | iTerm2/WezTerm only |

---

## Sources

- [XTerm Control Sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
- [Hyperlinks in Terminals](https://gist.github.com/egmontkob/eb114294efbcd5adb1944c9f3cb5feda)
- [Windows Terminal Virtual Terminal Sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)
- [iTerm2 Escape Codes](https://iterm2.com/documentation-escape-codes.html)
- [kitty Desktop Notifications Protocol](https://sw.kovidgoyal.net/kitty/desktop-notifications/)
