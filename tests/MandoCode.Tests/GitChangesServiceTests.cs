using System.Diagnostics;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Integration")]
public sealed class GitChangesServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mandocode-git-changes-" + Guid.NewGuid().ToString("N"));
    public GitChangesServiceTests() { Directory.CreateDirectory(_root); }
    private async Task Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        Assert.True(process.ExitCode == 0, await error);
    }
    private async Task Init()
    {
        await Git("init", "--quiet");
        await Git("config", "user.name", "Test"); await Git("config", "user.email", "test@example.invalid");
        await Git("config", "core.autocrlf", "false");
    }
    [Fact]
    public async Task CombinedDiffCountsStagedAndUnstagedAndRestoreResetsBoth()
    {
        await Init();
        var path = Path.Combine(_root, "file with spaces.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\n"); await Git("add", "."); await Git("commit", "-qm", "Initial");
        await File.WriteAllTextAsync(path, "one\nstaged\n"); await Git("add", ".");
        await File.AppendAllTextAsync(path, "unstaged\n");
        var file = Assert.Single(await GitChangesService.ReadAsync(_root));
        Assert.Equal("MM", file.Status); Assert.Equal(2, file.Added); Assert.Equal(1, file.Deleted);
        var diff = await GitChangesService.PreviewAsync(_root, file);
        Assert.Contains("+staged", diff.Lines); Assert.Contains("+unstaged", diff.Lines); Assert.Contains("-two", diff.Lines);
        await GitChangesService.DiscardAsync(_root, file);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path));
        Assert.Empty(await GitChangesService.ReadAsync(_root));
    }
    [Fact]
    public async Task DeletedAndUntrackedFilesHavePreviewsAndScopedDiscard()
    {
        await Init();
        var path = Path.Combine(_root, "deleted.txt");
        await File.WriteAllTextAsync(path, "original\n"); await Git("add", "."); await Git("commit", "-qm", "Initial"); File.Delete(path);
        await File.WriteAllTextAsync(Path.Combine(_root, "new [file].txt"), "hello\nworld\n");
        var files = await GitChangesService.ReadAsync(_root);
        var deleted = Assert.Single(files, x => x.Label == "Deleted"); Assert.Equal(1, deleted.Deleted);
        var added = Assert.Single(files, x => x.Untracked); Assert.Equal(2, added.Added);
        await GitChangesService.DiscardAsync(_root, deleted); Assert.True(File.Exists(path));
        await GitChangesService.DiscardAsync(_root, added); Assert.False(File.Exists(Path.Combine(_root, added.Path)));
        Assert.Empty(await GitChangesService.ReadAsync(_root));
    }
    [Fact]
    public async Task ProjectSubdirectoryExcludesSiblingChangesAndBinaryPreviewIsExplicit()
    {
        await Init(); Directory.CreateDirectory(Path.Combine(_root, "project"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sibling.txt"), "outside\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "project", "image.bin"), [0, 1, 2]);
        var file = Assert.Single(await GitChangesService.ReadAsync(Path.Combine(_root, "project")));
        Assert.Equal("image.bin", file.Path); Assert.True(file.Binary);
        Assert.True((await GitChangesService.PreviewAsync(Path.Combine(_root, "project"), file)).Binary);
    }
    [Fact]
    public void NullDelimitedStatusPreservesRenamePathsAndNewlines()
    {
        var files = GitChangesService.Parse("R  new name.txt\0old name.txt\0?? with\nnewline.txt\0 M ../outside.txt\0");
        Assert.Equal(2, files.Count); Assert.Equal("old name.txt", files[0].OriginalPath); Assert.Equal("with\nnewline.txt", files[1].Path);
    }
    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
