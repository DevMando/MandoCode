using MandoCode.Services;
using Xunit;
namespace MandoCode.Tests;
[Trait("Category", "Integration")]
public class FileAutocompleteAsyncTests
{
    [Fact]
    public async Task EmptySubfolderClosesFilePickerAndKeepsPathInPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        try
        {
            using var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), []);
            var composer = new PromptComposerState(new InputStateMachine(new(), provider));
            composer.SetText("@");
            await Wait(() => !provider.IsLoading);
            composer.Refresh();
            Assert.True(composer.HandleKey("Enter"));
            Assert.Equal("@empty/", composer.Buffer.Text);
            await Wait(() => !provider.IsLoading);
            composer.Refresh();
            Assert.False(composer.IsOpen);
            Assert.Empty(composer.Suggestions.DropdownItems);
            Assert.False(composer.FilesLoading);
            Assert.Equal("@empty/", composer.Buffer.Text);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RootEntriesAreUsableBeforeRecursiveScanFinishes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        using var release = new ManualResetEventSlim();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            File.WriteAllText(Path.Combine(root, "photo.png"), "");
            File.WriteAllText(Path.Combine(root, "nested", "deep.png"), "");
            using var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), []);
            provider.RootListed = () => { listed.TrySetResult(); release.Wait(); };
            provider.GetSuggestions("");
            await listed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(provider.IsIndexing);
            Assert.Equal(new[] { "nested/", "photo.png" }, provider.GetSuggestions(""));
            provider.GetSuggestions("photo");
            await Wait(() => provider.GetSuggestions("photo").Contains("photo.png"));
            Assert.True(provider.IsIndexing);
            release.Set();
            await Wait(() => !provider.IsLoading);
            provider.GetSuggestions("deep");
            await Wait(() => !provider.IsLoading);
            Assert.Contains("nested/deep.png", provider.GetSuggestions("deep"));
            Assert.Equal(1, provider.ScanCount);
        }
        finally { release.Set(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task NavigationSelectionSurvivesRedrawsAndIndexUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(root, $"file{i:D2}.txt"), "");
            using var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), []);
            var composer = new PromptComposerState(new InputStateMachine(new(), provider));
            composer.SetText("@");
            await Wait(() => !provider.IsLoading); composer.Refresh();
            for (var i = 0; i < 12; i++) { composer.HandleKey("ArrowDown"); composer.Refresh(); }
            Assert.Equal(12, composer.Suggestions.SelectedIndex);
            var selected = composer.Suggestions.DropdownItems[12];
            File.WriteAllText(Path.Combine(root, "aaa.txt"), "");
            await Wait(() => { composer.Refresh(); return composer.Suggestions.DropdownItems.Contains("aaa.txt"); });
            Assert.Equal(selected, composer.Suggestions.DropdownItems[composer.Suggestions.SelectedIndex]);
            Assert.Equal(1, provider.ScanCount);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RemovingAtKeepsScanRunningAndReopeningReusesItsCache()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "photo.png"), "");
            using var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), []);
            var composer = new PromptComposerState(new InputStateMachine(new(), provider));
            composer.SetText("@");
            Assert.True(composer.FilesLoading);
            composer.SetText("");
            Assert.False(composer.FilesLoading); // Hide progress, but keep warming the cache.
            Assert.True(provider.IsLoading);
            composer.SetText("@");
            await Wait(() => !provider.IsLoading);
            composer.Refresh();
            Assert.Contains("photo.png", composer.Suggestions.DropdownItems);
            Assert.Equal(1, provider.ScanCount);
            composer.SetText("ordinary text");
            composer.SetText("@");
            Assert.False(provider.IsLoading);
            Assert.Contains("photo.png", composer.Suggestions.DropdownItems);
            Assert.Equal(1, provider.ScanCount);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LookupReturnsImmediatelyFindsNestedFilesAndRefreshesNewEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Directory.CreateDirectory(Path.Combine(root, "ignored"));
            File.WriteAllText(Path.Combine(root, "nested", "photo.png"), "");
            File.WriteAllText(Path.Combine(root, "ignored", "photo.png"), "");
            using var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), ["ignored"]);
            Assert.Empty(provider.GetSuggestions("photo"));
            Assert.True(provider.IsLoading); // Completion is asynchronous, never a recursive UI scan.
            provider.GetSuggestions("p"); provider.GetSuggestions("ph"); provider.GetSuggestions("photo");
            await Wait(() => !provider.IsLoading);
            Assert.Equal(1, provider.ScanCount); // Typing shares a scan rather than restarting it.
            Assert.Equal(new[] { "nested/photo.png" }, provider.GetSuggestions("photo"));
            Assert.False(provider.IsLoading);
            File.WriteAllText(Path.Combine(root, "new-photo.png"), "");
            await Wait(() => { var matches = provider.GetSuggestions("photo"); return !provider.IsLoading && matches.Contains("new-photo.png"); });
            Assert.Equal(1, provider.ScanCount); // Watcher patches the index, never rescans the project.
            File.Move(Path.Combine(root, "new-photo.png"), Path.Combine(root, "renamed-photo.png"));
            await Wait(() => { var matches = provider.GetSuggestions("photo"); return !provider.IsLoading && matches.Contains("renamed-photo.png") && !matches.Contains("new-photo.png"); });
            Assert.Equal(1, provider.ScanCount);
            provider.GetSuggestions("no-match"); provider.GetSuggestions("photo");
            await Wait(() => !provider.IsLoading);
            Assert.Equal(2, provider.GetSuggestions("photo").Count);
        } finally { Directory.Delete(root, true); }
    }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }
}
