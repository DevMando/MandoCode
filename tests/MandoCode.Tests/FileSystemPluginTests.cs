using Xunit;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

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
        _tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
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
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), "some content");

        var result = await _plugin.EditFile(path, "this string does not exist", "x");

        Assert.Contains("Could not find", result);
        // Should surface the "common causes" guidance, not just "match exactly".
        Assert.Contains("re-read", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("write_file", result);
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
