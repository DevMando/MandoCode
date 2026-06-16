using Xunit;
using ArdinCode.Plugins;
using ArdinCode.Services;

namespace ArdinCode.Tests;

/// <summary>
/// Focused tests for the two pain points we just fixed:
///   1. edit_file is CRLF-tolerant (models emit LF-only old_text against CRLF files)
///   2. ReadFile / EditFile / DeleteFile produce actionable "file not found" diagnostics
/// </summary>
public class FileSystemPluginTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSystemPlugin _plugin;

    public FileSystemPluginTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ardincode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _plugin = new FileSystemPlugin(new ProjectRootAccessor(_tempRoot));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────
    //  CRLF tolerance
    // ──────────────────────────────────────────────

    [Fact]
    public async Task EditFile_MatchesCrlfFile_AgainstLfOldText()
    {
        // File on disk uses CRLF. Model emits LF-only old_text.
        var path = "foo.txt";
        var crlfContent = "line one\r\nline two\r\nline three\r\n";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), crlfContent);

        var lfOldText = "line one\nline two";         // LF-only — would fail exact match
        var lfNewText = "LINE ONE\nLINE TWO";

        var result = await _plugin.EditFile(path, lfOldText, lfNewText);

        Assert.StartsWith("Successfully edited", result);
        Assert.Contains("line-endings normalized", result);

        // File should still use CRLF — we preserve the original style.
        var after = await File.ReadAllTextAsync(Path.Combine(_tempRoot, path));
        Assert.Contains("\r\n", after);
        Assert.Contains("LINE ONE\r\nLINE TWO", after);
    }

    [Fact]
    public async Task EditFile_PreservesLfOnlyFile_WhenEditingWithLfOldText()
    {
        var path = "foo.txt";
        var lfContent = "alpha\nbeta\ngamma\n";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), lfContent);

        var result = await _plugin.EditFile(path, "beta", "BETA");

        Assert.StartsWith("Successfully edited", result);
        // No normalization marker on the exact-match path.
        Assert.DoesNotContain("line-endings normalized", result);

        var after = await File.ReadAllTextAsync(Path.Combine(_tempRoot, path));
        Assert.DoesNotContain("\r\n", after); // didn't turn into CRLF
        Assert.Contains("BETA", after);
    }

    [Fact]
    public async Task EditFile_NoMatch_ReturnsDiagnosticGuidance()
    {
        var path = "foo.txt";
        var fileContent = "some content";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), fileContent);

        var result = await _plugin.EditFile(path, "this string does not exist", "x");

        Assert.Contains("Could not find", result);
        Assert.Contains("write_file", result);
        // The current file content is appended so the model can fix old_text in place
        // without firing a separate read_file_contents trip (that re-read cascade was
        // the bloat that filled chat history in past failures).
        Assert.Contains("Current file content", result);
        Assert.Contains(fileContent, result);
    }

    [Fact]
    public async Task EditFile_NoMatch_LargeFile_TruncatesContentHint()
    {
        // For files larger than the 5K content-hint cap, the error response must truncate
        // — otherwise the hint itself becomes a bloat source we were trying to prevent.
        var path = "big.txt";
        var bigContent = new string('a', 8000);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), bigContent);

        var result = await _plugin.EditFile(path, "missing fragment", "x");

        Assert.Contains("Could not find", result);
        Assert.Contains("Current file content", result);
        Assert.Contains("truncated", result);
        // The full 8000-char content must not appear verbatim — the truncation marker
        // proves the cap kicked in.
        Assert.DoesNotContain(bigContent, result);
    }

    // ──────────────────────────────────────────────
    //  File-not-found diagnostics
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_Missing_IncludesSameNameMatchesFromElsewhere()
    {
        // Create a CSS file at a different location than the one we ask for.
        var nestedDir = Path.Combine(_tempRoot, "Games", "crypto");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "main.css"), "body {}");

        // Model asks for a non-existent "styles/main.css".
        var result = await _plugin.ReadFile("Games/crypto/styles/main.css");

        Assert.Contains("File not found", result);
        // Should name the actual location of main.css elsewhere in the project.
        Assert.Contains("Games/crypto/main.css", result);
    }

    [Fact]
    public async Task ReadFile_Missing_ListsNearestExistingParentDirectory()
    {
        var gamesDir = Path.Combine(_tempRoot, "Games", "crypto");
        Directory.CreateDirectory(gamesDir);
        await File.WriteAllTextAsync(Path.Combine(gamesDir, "index.html"), "<html/>");
        await File.WriteAllTextAsync(Path.Combine(gamesDir, "app.js"), "console.log(1)");

        // Parent "styles/" doesn't exist; helper should walk up to Games/crypto/ and list it.
        var result = await _plugin.ReadFile("Games/crypto/styles/main.css");

        Assert.Contains("File not found", result);
        Assert.Contains("Contents of", result);
        Assert.Contains("index.html", result);
        Assert.Contains("app.js", result);
    }

    [Fact]
    public async Task EditFile_Missing_UsesSameDiagnostic()
    {
        // Seed a file so the parent-dir-listing fallback has something to show.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "existing.txt"), "x");

        var result = await _plugin.EditFile("nope/nothing.txt", "a", "b");

        Assert.Contains("File not found", result);
        Assert.Contains("Contents of", result); // Fell back to project root listing.
        Assert.Contains("existing.txt", result);
    }

    [Fact]
    public async Task DeleteFile_Missing_UsesSameDiagnostic()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "sentinel.txt"), "x");

        var result = await _plugin.DeleteFile("nope/nothing.txt");

        Assert.Contains("File not found", result);
        Assert.Contains("Contents of", result);
    }
}
