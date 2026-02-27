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

## High Priority

### 1. App.razor Is a God Object (56 KB, 1460 lines)

**File:** `Components/App.razor`

The main application component handles everything: spinner management, music visualizer, taskbar progress, the interactive loop, file reference processing, AI requests, planned execution, config/learn/copy/shell commands, diff approvals, delete approvals, and all operation display rendering.

This makes the file extremely hard to navigate, test, or extend.

**Recommendation:** Extract into focused service classes:

| New Class | Responsibility | Approx Lines Moved |
|-----------|---------------|-------------------|
| `SpinnerService` | Spinner lifecycle + taskbar progress (OSC 9;4) | ~80 |
| `ShellCommandHandler` | `!cmd` / `/command` execution, `cd` interception | ~90 |
| `DiffApprovalHandler` | Write/delete approval UI, diff panel rendering | ~200 |
| `OperationDisplayRenderer` | `RenderOperationDisplay`, `RenderWriteDisplay`, `RenderUpdateDisplay` | ~120 |
| `CommandRouter` | Main loop + slash command dispatch | ~150 |

This would reduce App.razor to ~300 lines focused purely on component lifecycle and wiring.

---

### 2. Thread Safety — `_recentCalls` Dictionary

**File:** `Services/FunctionInvocationFilter.cs` — Line 45

The deduplication cache is a plain `Dictionary<string, (DateTime, object?)>` but is accessed from concurrent function invocations (Semantic Kernel's `AllowConcurrentInvocation = true` in `AIService.cs:115`). Concurrent reads and writes to `Dictionary` can corrupt the internal data structure.

**Current:**
```csharp
private readonly Dictionary<string, (DateTime Time, object? Result)> _recentCalls = new();
```

**Fix:**
```csharp
private readonly ConcurrentDictionary<string, (DateTime Time, object? Result)> _recentCalls = new();
```

Also update `CleanupOldEntries()` and `ClearCache()` to use `ConcurrentDictionary` methods (`TryRemove`, `Clear`).

---

### 3. Thread Safety — `_chatHistory` Mutations

**File:** `Services/AIService.cs`

`ChatHistory` is mutated from multiple code paths without synchronization:
- `ChatAsync()` — adds user message, then assistant message
- `ChatStreamAsync()` — adds user message, then assistant message
- `ExecutePlanStepAsync()` — adds plan step messages to main history
- `ClearHistory()` — clears and re-adds system prompt
- `EnterLearnMode()` — clears and adds educator prompt

If a plan step runs while a UI-triggered clear happens, the collection can be corrupted.

**Fix:** Add a `lock` around all `_chatHistory` mutations, or use a `SemaphoreSlim` for async-compatible locking.

---

### 4. Path Traversal Security Gap

**File:** `Plugins/FileSystemPlugin.cs` — Line 382

The sandbox check uses `StartsWith` without ensuring a directory separator boundary:

```csharp
if (!fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
```

If `_projectRoot` is `C:\projects\myapp`, a crafted path like `C:\projects\myappevil\secret.txt` passes the check because it starts with the same string prefix.

**Fix:**
```csharp
private string GetFullPath(string relativePath)
{
    var fullPath = Path.Combine(_projectRoot, relativePath);
    fullPath = Path.GetFullPath(fullPath);

    // Ensure path is within project root with separator boundary
    var normalizedRoot = _projectRoot.TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

    if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
        && !fullPath.Equals(_projectRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Access denied: Path is outside project root: {relativePath}");
    }

    return fullPath;
}
```

---

## Medium Priority

### 5. Blocking Async Calls

**File:** `Components/App.razor`

**Line 220** — `.GetAwaiter().GetResult()` blocks the UI thread during initialization:
```csharp
_isConnected = CheckOllamaConnectionAsync().GetAwaiter().GetResult();
```

**Lines 134, 204** — `task?.Wait(1000)` in `StopSpinner()` and `StopMusicVisualizer()` blocks the calling thread instead of awaiting.

**Fix:** Move connection check to `OnAfterRenderAsync` (already async). For spinner/visualizer, use `await` with a timeout or fire-and-forget with cancellation.

---

### 6. `ChatStreamAsync` Lacks Retry Policy

**File:** `Services/AIService.cs` — Line 263

`ChatAsync()` wraps its call in `RetryPolicy.ExecuteWithRetryAsync()`, but `ChatStreamAsync()` — used for all direct user requests — does not. This means interactive prompts lack transient error resilience (HTTP failures, timeouts, socket errors) while plan steps have it.

**Fix:** Wrap the `GetChatMessageContentAsync` call inside `ChatStreamAsync` with the same retry policy:
```csharp
var result = await RetryPolicy.ExecuteWithRetryAsync(
    async () => await _chatService.GetChatMessageContentAsync(
        _chatHistory, _settings, _kernel, cts.Token),
    _config.MaxRetryAttempts,
    "ChatStreamAsync"
);
```

---

### 7. Duplicated Error Message Formatting

**File:** `Services/AIService.cs`

`ChatAsync()` (lines 239-250) has an inline error message for tool-support errors, and `FormatErrorMessage()` (lines 317-337) has a different, better-formatted version of the same error. `ChatStreamAsync` uses `FormatErrorMessage` but `ChatAsync` doesn't.

**Fix:** Replace the inline error in `ChatAsync` with:
```csharp
catch (Exception ex)
{
    return FormatErrorMessage(ex);
}
```

---

### 8. `NormalizeParameterName` Allocates a Dictionary Every Call

**File:** `Services/AIService.cs` — Line 955

A new `Dictionary<string, string>` with 12 entries is allocated on every single function parameter normalization call.

**Current:**
```csharp
private string NormalizeParameterName(string paramName)
{
    var paramMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "pattern", "pattern" },
        { "glob_pattern", "pattern" },
        // ... 10 more entries
    };
    // ...
}
```

**Fix:** Make it a static readonly field:
```csharp
private static readonly Dictionary<string, string> ParamMappings =
    new(StringComparer.OrdinalIgnoreCase)
{
    { "pattern", "pattern" },
    { "glob_pattern", "pattern" },
    // ...
};

private static string NormalizeParameterName(string paramName)
{
    return ParamMappings.TryGetValue(paramName, out var normalized)
        ? normalized
        : paramName;
}
```

---

### 9. Naive Glob Matching

**File:** `Plugins/FileSystemPlugin.cs` — Lines 336-374

`MatchesPattern` is hand-rolled and misses common glob patterns:

| Pattern | Expected | Actual |
|---------|----------|--------|
| `src/*.cs` | Files in src/ only | Matches any path ending in `.cs` after stripping `**` |
| `**/*.test.js` | Test files anywhere | Partially works, fragile |
| `src/**/*.cs` | C# files under src/ | Works but only for two-segment `**` splits |
| `?.txt` | Single char + .txt | No support for `?` wildcards |

**Fix:** Replace with `Microsoft.Extensions.FileSystemGlobbing` (already part of .NET):
```csharp
using Microsoft.Extensions.FileSystemGlobbing;

private bool MatchesPattern(string filePath, string pattern)
{
    var matcher = new Matcher();
    matcher.AddInclude(pattern);
    return matcher.Match(filePath).HasMatches;
}
```

Or add the NuGet package `Microsoft.Extensions.FileSystemGlobbing` if not already referenced.

---

### 10. No Cancellation Support for User Aborting AI Requests

**File:** `Components/App.razor` — `ProcessDirectRequestAsync`

There is no way for the user to cancel a long-running AI request (e.g., Ctrl+C). The 5-minute timeout in `ChatStreamAsync` is the only safety net.

**Recommendation:** Listen for `Console.CancelKeyPress` and wire it to a `CancellationTokenSource` passed through to the AI service. Display a "Request cancelled" message on Ctrl+C.

---

## Low Priority

### 11. Duplicate Ignore Directory Lists (3 Places)

The default ignore directory list appears in three separate locations:

| File | Line | Context |
|------|------|---------|
| `Plugins/FileSystemPlugin.cs` | 12 | Hardcoded `HashSet` in field initializer |
| `Program.cs` | 86 | Hardcoded `HashSet` in DI registration |
| `Models/MandoCodeConfig.cs` | 197 | Hardcoded `List` in `CreateDefault()` |

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

### 12. Shell Command Output Can Exhaust Memory

**File:** `Components/App.razor` — Lines 1099-1100

`ReadToEnd()` loads entire stdout/stderr into memory:
```csharp
var stdout = proc.StandardOutput.ReadToEnd();
var stderr = proc.StandardError.ReadToEnd();
```

A command like `cat /dev/urandom` or a massive `git log` could exhaust memory.

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

### 13. Duplicate JSON Brace-Counting Logic

**File:** `Services/AIService.cs`

`ExtractJsonObject()` (line 665) and `RemoveFunctionCallJson()` (line 721) both implement identical brace-depth tracking with string/escape handling independently.

**Fix:** Extract a shared helper:
```csharp
private static (int Start, int End)? FindJsonObjectBounds(string text, int startIndex)
{
    // Shared brace-depth counting logic
}
```

Then call it from both `ExtractJsonObject` and `RemoveFunctionCallJson`.

---

### 14. `FileSystemPlugin.GetAllFiles` Has No Caching

**File:** `Plugins/FileSystemPlugin.cs` — Line 309

`GetAllFiles()` does a full recursive directory walk every time `ListAllProjectFiles`, `ListFiles`, or `FindInFiles` is called. For large projects this is expensive.

**Recommendation:** Cache the file list with a short TTL (e.g., 5 seconds) or invalidate on write/delete operations. The `FileAutocompleteProvider` already has its own cache — consider sharing.

---

### 15. Minor: Awkward String Construction in `ReadFile`

**File:** `Plugins/FileSystemPlugin.cs` — Line 109

```csharp
$"{'='.ToString().PadRight(50, '=')}\n"
```

This creates a single `=` character, converts to string, then pads. Simpler:
```csharp
$"{new string('=', 50)}\n"
```

---

### 16. Missing `IDisposable` on App.razor

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

### 17. Process Exit Code Not Checked in Shell Commands

**File:** `Components/App.razor` — Line 1101

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

### 18. `FindInFiles` Reads All Files Into Memory

**File:** `Plugins/FileSystemPlugin.cs` — Line 254

`File.ReadAllLinesAsync` loads the entire contents of every matching file into memory. For a large codebase with many matches, this could be expensive.

**Recommendation:** Use `File.ReadLinesAsync()` (streaming) or read line-by-line with `StreamReader` to reduce peak memory usage. Also consider adding a max results limit to prevent unbounded output.

---

## Summary Table

| # | Issue | Priority | File | Type |
|---|-------|----------|------|------|
| 1 | App.razor God Object (1460 lines) | **High** | `Components/App.razor` | Architecture |
| 2 | Thread safety on `_recentCalls` | **High** | `Services/FunctionInvocationFilter.cs` | Concurrency |
| 3 | Thread safety on `_chatHistory` | **High** | `Services/AIService.cs` | Concurrency |
| 4 | Path traversal security gap | **High** | `Plugins/FileSystemPlugin.cs` | Security |
| 5 | Blocking async calls | **Medium** | `Components/App.razor` | Performance |
| 6 | Missing retry in `ChatStreamAsync` | **Medium** | `Services/AIService.cs` | Resilience |
| 7 | Duplicated error message formatting | **Medium** | `Services/AIService.cs` | Code Quality |
| 8 | Dict allocation per parameter call | **Medium** | `Services/AIService.cs` | Performance |
| 9 | Naive glob matching | **Medium** | `Plugins/FileSystemPlugin.cs` | Correctness |
| 10 | No Ctrl+C cancellation support | **Medium** | `Components/App.razor` | UX |
| 11 | Duplicate ignore directory lists | **Low** | Multiple files | Maintainability |
| 12 | Shell command OOM risk | **Low** | `Components/App.razor` | Edge Case |
| 13 | Duplicate JSON brace-counting | **Low** | `Services/AIService.cs` | Code Quality |
| 14 | No file list caching in plugin | **Low** | `Plugins/FileSystemPlugin.cs` | Performance |
| 15 | Awkward string construction | **Low** | `Plugins/FileSystemPlugin.cs` | Code Quality |
| 16 | Missing `IDisposable` | **Low** | `Components/App.razor` | Resource Leak |
| 17 | Process exit code not checked | **Low** | `Components/App.razor` | Correctness |
| 18 | `FindInFiles` memory usage | **Low** | `Plugins/FileSystemPlugin.cs` | Performance |
