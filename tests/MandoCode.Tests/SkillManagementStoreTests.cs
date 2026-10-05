using System.IO.Compression;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public sealed class SkillManagementStoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mandocode-skills-test-" + Guid.NewGuid().ToString("N"));
    private SkillManagementStore Store => new(Path.Combine(_temp, "user"));

    [Fact]
    public void EditRenameAndToggle_PreserveDisabledStateAndAssets()
    {
        var folder = Store.Save(null, "Code Review", "Review code: carefully\nPreserve intent.", "# Review\nCheck the diff.");
        File.WriteAllText(Path.Combine(folder, "example.txt"), "asset");
        Store.SetEnabled(folder, false);
        var renamed = Store.Save(folder, "Review Changes", "Review changes.", "Check edits.");
        var entry = Assert.Single(Store.List());
        Assert.False(entry.Enabled);
        Assert.Equal("Review Changes", entry.Name);
        Assert.Equal("Check edits.", entry.Body);
        Assert.Equal("asset", File.ReadAllText(Path.Combine(renamed, "example.txt")));
        var config = new MandoCodeConfig { UserSkillsDirectory = Store.Root, ProjectSkillsDirectory = Path.Combine(_temp, "project") };
        var loader = new SkillLoader(config, new ProjectRootAccessor(_temp));
        Assert.Empty(loader.GetAll());
        Store.SetEnabled(renamed, true);
        loader.Reload();
        Assert.Equal("Review Changes", Assert.Single(loader.GetAll()).Name);
        var trash = Store.Remove(renamed);
        Assert.Empty(Store.List());
        Assert.True(File.Exists(Path.Combine(trash, "SKILL.md")));
    }

    [Fact]
    public void Save_RoundTripsYamlAndRejectsCollisionsAndOutsidePaths()
    {
        var description = "Review code: carefully\nKeep user intent.";
        var first = Store.Save(null, "First", description, "Instructions");
        Assert.Equal(description, Assert.Single(Store.List()).Description);
        Assert.Throws<IOException>(() => Store.Save(null, "First", "", "Overwrite"));
        Assert.Throws<IOException>(() => Store.Save(Path.Combine(_temp, "outside"), "Second", "", "Overwrite"));
        Assert.Throws<IOException>(() => Store.Remove(Path.Combine(_temp, "outside")));
        Assert.Throws<ArgumentException>(() => Store.Save(null, "CON", "", "Invalid"));
        Assert.Throws<ArgumentException>(() => Store.Save(first, "First", "", " "));
        Assert.Equal("Instructions", Assert.Single(Store.List()).Body);
    }

    [Fact]
    public async Task InstallFolderAndZip_PreserveAssetsAndSkipExistingSkills()
    {
        var source = new SkillManagementStore(Path.Combine(_temp, "source"));
        var folder = source.Save(null, "Imported", "Import me", "Do the work");
        Directory.CreateDirectory(Path.Combine(folder, "references"));
        File.WriteAllText(Path.Combine(folder, "references", "example.md"), "Reference");
        var result = await Store.InstallAsync(source.Root, default);
        Assert.Equal(new[] { "Imported" }, result.Installed);
        Assert.Equal("Reference", File.ReadAllText(Path.Combine(Assert.Single(Store.List()).Folder, "references", "example.md")));
        source.Save(null, "Additional", "Second skill", "More work");
        var zip = Path.Combine(_temp, "skills.zip");
        ZipFile.CreateFromDirectory(source.Root, zip);
        var second = await Store.InstallAsync(zip, default);
        Assert.Equal(new[] { "Additional" }, second.Installed);
        Assert.Single(second.Skipped);
        Assert.Equal(2, Store.List().Count);
        Assert.DoesNotContain(Directory.GetDirectories(Store.Root), path => Path.GetFileName(path).StartsWith(".install-"));
    }

    [Fact]
    public async Task InvalidArchiveAndCancellation_DoNotPublishASkill()
    {
        Directory.CreateDirectory(_temp);
        var zip = Path.Combine(_temp, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("escape");
        await Assert.ThrowsAsync<IOException>(() => Store.InstallAsync(zip, default));
        Assert.False(File.Exists(Path.Combine(_temp, "escape.txt")));
        var source = new SkillManagementStore(Path.Combine(_temp, "source"));
        source.Save(null, "Cancelled", "", "Instructions");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.InstallAsync(source.Root, new CancellationToken(true)));
        Assert.Empty(Store.List());
    }

    [Fact]
    public void MalformedDisabledSkill_CanBeEditedButCannotBeEnabled()
    {
        var folder = Store.Save(null, "Broken", "", "Instructions");
        Store.SetEnabled(folder, false);
        File.WriteAllText(Path.Combine(folder, "SKILL.md.disabled"), "not frontmatter");
        Assert.NotNull(Assert.Single(Store.List()).Error);
        Assert.Throws<IOException>(() => Store.SetEnabled(folder, true));
        Store.Save(folder, "Broken", "", "Fixed");
        Store.SetEnabled(folder, true);
        Assert.Null(Assert.Single(Store.List()).Error);
    }

    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
