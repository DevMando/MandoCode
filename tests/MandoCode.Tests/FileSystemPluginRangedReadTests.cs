using Xunit;
using System.Text.RegularExpressions;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for ranged reads (startLine/endLine on read_file_contents) and the compound-cd
/// fix in execute_command. Ranged reads exist so the model can page through files larger
/// than the ~10K output cap instead of editing blind below the truncation horizon —
/// the root cause of "could not find old_text" thrash loops on large generated files.
/// </summary>
public class FileSystemPluginRangedReadTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSystemPlugin _plugin;

    public FileSystemPluginRangedReadTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _plugin = new FileSystemPlugin(new ProjectRootAccessor(_tempRoot));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private async Task<string> WriteLines(string path, int count, string prefix = "line")
    {
        var content = string.Join('\n', Enumerable.Range(1, count).Select(i => $"{prefix} {i}"));
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), content);
        return content;
    }

    // ──────────────────────────────────────────────
    //  Ranged reads
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_SmallFile_ReturnsWholeFileWithPlainHeader()
    {
        await WriteLines("small.txt", 5);

        var result = await _plugin.ReadFile("small.txt");

        Assert.Contains("File: small.txt (5 lines)", result);
        Assert.Contains("line 1", result);
        Assert.Contains("line 5", result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public async Task ReadFile_Range_ReturnsOnlyRequestedLines()
    {
        await WriteLines("ranged.txt", 10);

        var result = await _plugin.ReadFile("ranged.txt", startLine: 3, endLine: 5);

        Assert.Contains("File: ranged.txt (lines 3-5 of 10)", result);
        Assert.Contains("line 3", result);
        Assert.Contains("line 5", result);
        Assert.DoesNotContain("line 2", result);
        Assert.DoesNotContain("line 6", result);
    }

    [Fact]
    public async Task ReadFile_EndLineZero_ReadsToEndOfFile()
    {
        await WriteLines("tail.txt", 10);

        var result = await _plugin.ReadFile("tail.txt", startLine: 8);

        Assert.Contains("File: tail.txt (lines 8-10 of 10)", result);
        Assert.Contains("line 8", result);
        Assert.Contains("line 10", result);
        Assert.DoesNotContain("line 7", result);
    }

    [Fact]
    public async Task ReadFile_LargeFile_TruncatesAtLineBoundary_AndResumeHintIsAccurate()
    {
        // ~30 chars per line x 1000 lines ≈ 30K chars — well past the 10K cap.
        await WriteLines("big.txt", 1000, "this is a long padded line nr");

        var first = await _plugin.ReadFile("big.txt");

        // The hint must name the cutoff line and the exact startLine to resume from.
        var hint = Regex.Match(first, @"truncated at line (\d+) of 1000 — call read_file_contents with startLine=(\d+)");
        Assert.True(hint.Success, $"resume hint missing or malformed in: {first[^200..]}");

        var lastShown = int.Parse(hint.Groups[1].Value);
        var resumeAt = int.Parse(hint.Groups[2].Value);
        Assert.Equal(lastShown + 1, resumeAt);

        // The first page ends with a COMPLETE line (cut at a line boundary)...
        Assert.Contains($"this is a long padded line nr {lastShown}\n...", first);

        // ...and resuming from the hint yields the next line, with no gap and no overlap.
        var second = await _plugin.ReadFile("big.txt", startLine: resumeAt);
        Assert.Contains($"(lines {resumeAt}-", second);
        Assert.Contains($"this is a long padded line nr {resumeAt}", second);
        Assert.DoesNotContain($"this is a long padded line nr {lastShown}\n", second);
    }

    [Fact]
    public async Task ReadFile_StartPastEnd_ReturnsActionableError()
    {
        await WriteLines("short.txt", 5);

        var result = await _plugin.ReadFile("short.txt", startLine: 99);

        Assert.StartsWith("Error:", result);
        Assert.Contains("only 5 lines", result);
        Assert.Contains("between 1 and 5", result);
    }

    [Fact]
    public async Task ReadFile_EndBeforeStart_ReturnsActionableError()
    {
        await WriteLines("short.txt", 5);

        var result = await _plugin.ReadFile("short.txt", startLine: 4, endLine: 2);

        Assert.StartsWith("Error:", result);
        Assert.Contains("endLine", result);
    }

    [Fact]
    public async Task ReadFile_CrlfFile_PreservesCrlfInRangedOutput()
    {
        // Split on '\n' + join with '\n' must reproduce the original bytes, so the
        // model's old_text is composed against real file content, CRLF included.
        var path = "crlf.txt";
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, path), "alpha\r\nbeta\r\ngamma\r\ndelta");

        var result = await _plugin.ReadFile(path, startLine: 2, endLine: 3);

        Assert.Contains("beta\r\ngamma", result);
        Assert.DoesNotContain("alpha", result);
        Assert.DoesNotContain("delta", result);
    }

    // ──────────────────────────────────────────────
    //  Compound cd commands
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommand_CompoundCd_RunsInShellInsteadOfCdInterceptor()
    {
        // `cd X && <cmd>` used to be swallowed whole by the cd interceptor, which
        // treated the ENTIRE string (including `&& <cmd>`) as a directory path and
        // failed with "No such directory". Compound commands must reach the real shell.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "sub"));

        var result = await _plugin.ExecuteCommand("cd sub && echo compound-ok");

        Assert.DoesNotContain("No such directory", result);
        Assert.Contains("compound-ok", result);
    }
}
