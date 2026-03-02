# Code Improvements & Technical Debt

Comprehensive audit of the MandoCode codebase with prioritized improvements.
Covers architecture, security, thread safety, performance, and code quality.

---

## Priority Legend

| Level | Meaning |
|-------|---------|
| **High** | Bugs, security risks, or data corruption potential — fix soon |
| **Medium** | Performance issues, inconsistencies, or maintainability concerns |
| **Low** | Code cleanliness, minor optimizations, edge cases |

---

## Medium Priority

### 1. Blocking Async Calls

**File:** `Components/App.razor`

`.GetAwaiter().GetResult()` blocks the UI thread during initialization. `task?.Wait(1000)` in `StopSpinner()` and `StopMusicVisualizer()` blocks the calling thread instead of awaiting.

**Fix:** Move connection check to `OnAfterRenderAsync` (already async). For spinner/visualizer, use `await` with a timeout or fire-and-forget with cancellation.

---

### 2. No Cancellation Support for User Aborting AI Requests

**File:** `Components/App.razor` — `ProcessDirectRequestAsync`

There is no way for the user to cancel a long-running AI request (e.g., Ctrl+C). The 5-minute timeout in `ChatStreamAsync` is the only safety net.

**Recommendation:** Listen for `Console.CancelKeyPress` and wire it to a `CancellationTokenSource` passed through to the AI service. Display a "Request cancelled" message on Ctrl+C.

---

## Low Priority

### 3. Duplicate Ignore Directory Lists (3 Places)

The default ignore directory list appears in three separate locations:

| File | Line | Context |
|------|------|---------|
| `Plugins/FileSystemPlugin.cs` | 13 | Hardcoded `HashSet` in field initializer |
| `Program.cs` | 108 | Hardcoded `HashSet` in DI registration |
| `Models/MandoCodeConfig.cs` | `CreateDefault()` | Hardcoded `List` |

**Fix:** Define once in `MandoCodeConfig`:
```csharp
public static readonly IReadOnlyList<string> DefaultIgnoreDirectories = new[]
{
    ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
    "packages", "dist", "build", "__pycache__", ".idea", ".claude"
};
```

Then reference `MandoCodeConfig.DefaultIgnoreDirectories` in all three locations.

---

### 4. Shell Command Output Can Exhaust Memory

**File:** `Services/ShellCommandHandler.cs` — Line 87

`ReadToEnd()` loads entire stdout/stderr into memory. A command like `cat /dev/urandom` or a massive `git log` could exhaust memory.

**Fix:** Stream output line-by-line with a max buffer:
```csharp
var outputBuilder = new StringBuilder();
var maxChars = 100_000;
string? line;
while ((line = proc.StandardOutput.ReadLine()) != null && outputBuilder.Length < maxChars)
{
    Console.WriteLine(line);
    outputBuilder.AppendLine(line);
}
if (outputBuilder.Length >= maxChars)
    Console.WriteLine("[output truncated]");
```

---

### 5. Duplicate JSON Brace-Counting Logic

**File:** `Services/AIService.cs`

`ExtractJsonObject()` and `RemoveFunctionCallJson()` both implement identical brace-depth tracking with string/escape handling independently.

**Fix:** Extract a shared helper:
```csharp
private static (int Start, int End)? FindJsonObjectBounds(string text, int startIndex)
{
    // Shared brace-depth counting logic
}
```

Then call it from both `ExtractJsonObject` and `RemoveFunctionCallJson`.

---

### 6. `FileSystemPlugin.GetAllFiles` Has No Caching

**File:** `Plugins/FileSystemPlugin.cs`

`GetAllFiles()` does a full recursive directory walk every time `ListAllProjectFiles`, `ListFiles`, or `FindInFiles` is called. For large projects this is expensive.

**Recommendation:** Cache the file list with a short TTL (e.g., 5 seconds) or invalidate on write/delete operations. The `FileAutocompleteProvider` already has its own cache — consider sharing.

---

### 7. Minor: Awkward String Construction in `ReadFile`

**File:** `Plugins/FileSystemPlugin.cs` — Line 110

```csharp
$"{'='.ToString().PadRight(50, '=')}\n"
```

This creates a single `=` character, converts to string, then pads. Simpler:
```csharp
$"{new string('=', 50)}\n"
```

---

### 8. Missing `IDisposable` on App.razor

**File:** `Components/App.razor`

`CancellationTokenSource` objects are created for spinner and music visualizer but the component doesn't implement `IDisposable`. If the app exits unexpectedly (not via `/exit`), these resources leak.

**Fix:** Implement `IDisposable`:
```csharp
@implements IDisposable

// In @code block:
public void Dispose()
{
    StopSpinner();
    StopMusicVisualizer();
    MusicPlayer?.Dispose();
}
```

---

### 9. Process Exit Code Not Checked in Shell Commands

**File:** `Services/ShellCommandHandler.cs` — Line 89

`proc.WaitForExit(30_000)` returns a `bool` indicating whether the process exited within the timeout, but the return value is discarded. If the process hangs, the user gets no feedback.

**Fix:**
```csharp
if (!proc.WaitForExit(30_000))
{
    proc.Kill();
    AnsiConsole.MarkupLine("[yellow]Command timed out after 30 seconds.[/]");
}
```

---

### 10. `FindInFiles` Reads All Files Into Memory

**File:** `Plugins/FileSystemPlugin.cs`

`File.ReadAllLinesAsync` loads the entire contents of every matching file into memory. For a large codebase with many matches, this could be expensive.

**Recommendation:** Use `File.ReadLinesAsync()` (streaming) or read line-by-line with `StreamReader` to reduce peak memory usage. Also consider adding a max results limit to prevent unbounded output.

---

## Summary Table

| # | Issue | Priority | File | Type |
|---|-------|----------|------|------|
| 1 | Blocking async calls | **Medium** | `Components/App.razor` | Performance |
| 2 | No Ctrl+C cancellation support | **Medium** | `Components/App.razor` | UX |
| 3 | Duplicate ignore directory lists | **Low** | Multiple files | Maintainability |
| 4 | Shell command OOM risk | **Low** | `Services/ShellCommandHandler.cs` | Edge Case |
| 5 | Duplicate JSON brace-counting | **Low** | `Services/AIService.cs` | Code Quality |
| 6 | No file list caching in plugin | **Low** | `Plugins/FileSystemPlugin.cs` | Performance |
| 7 | Awkward string construction | **Low** | `Plugins/FileSystemPlugin.cs` | Code Quality |
| 8 | Missing `IDisposable` | **Low** | `Components/App.razor` | Resource Leak |
| 9 | Process exit code not checked | **Low** | `Services/ShellCommandHandler.cs` | Correctness |
| 10 | `FindInFiles` memory usage | **Low** | `Plugins/FileSystemPlugin.cs` | Performance |
