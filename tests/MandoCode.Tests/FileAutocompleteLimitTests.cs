using MandoCode.Services;
using Xunit;
namespace MandoCode.Tests;
[Trait("Category", "Integration")]
public class FileAutocompleteLimitTests
{
    [Fact]
    public void AllMatchesRemainAvailableEvenWhenFoldersFillTheFirstPage()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            for (var i = 0; i < 20; i++) { Directory.CreateDirectory(Path.Combine(root, "folder" + i)); File.WriteAllText(Path.Combine(root, "folder" + i, "match.txt"), ""); }
            File.WriteAllText(Path.Combine(root, "match.txt"), "");
            var provider = new FileAutocompleteProvider(new ProjectRootAccessor(root), []);
            var all = provider.FilterFiles("");
            Assert.Equal(21, all.Count); Assert.Contains("match.txt", all);
            Assert.Equal(21, provider.FilterFiles("match").Count);
        } finally { Directory.Delete(root, true); }
    }
}
