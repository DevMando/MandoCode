using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class AgentFileTreeTests
{
    [Fact]
    public async Task ExpansionIsLazyAndCollapseKeepsSiblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-files-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        File.WriteAllText(Path.Combine(root, "folder", "nested.txt"), "nested");
        File.WriteAllText(Path.Combine(root, "sibling.txt"), "sibling");
        try
        {
            var tree = new AgentFileTree(root);
            await tree.RefreshAsync();
            Assert.Equal(3, tree.Entries.Count);
            Assert.True(tree.Entries[1].IsDirectory);
            Assert.DoesNotContain(tree.Entries, entry => entry.Path.EndsWith("nested.txt"));
            await tree.ToggleAsync(Path.Combine(root, "folder"));
            Assert.Contains(tree.Entries, entry => entry.Path.EndsWith("nested.txt") && entry.Depth == 2);
            await tree.ToggleAsync(Path.Combine(root, "folder"));
            Assert.Equal(3, tree.Entries.Count);
            Assert.Contains(tree.Entries, entry => entry.Path.EndsWith("sibling.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RefreshFindsNewFilesAndSeparateTreesKeepTheirOwnExpansion()
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-files-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        try
        {
            var first = new AgentFileTree(root);
            var second = new AgentFileTree(root);
            await first.RefreshAsync();
            await second.RefreshAsync();
            await first.ToggleAsync(Path.Combine(root, "folder"));
            Assert.False(second.IsExpanded(Path.Combine(root, "folder")));
            File.WriteAllText(Path.Combine(root, "folder", "new.txt"), "new");
            await first.RefreshAsync();
            Assert.Contains(first.Entries, entry => entry.Path.EndsWith("new.txt"));
            Assert.DoesNotContain(second.Entries, entry => entry.Path.EndsWith("new.txt"));
        }
        finally { Directory.Delete(root, true); }
    }
}
