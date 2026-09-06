using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class PlanRepositoryContextTests
{
    [Fact]
    public void RepositoryDistinguishesEmptyMissingAndUnsupportedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Contains("Confirmed empty directory", PlanRepositoryContext.Capture(root, "game"));
            File.WriteAllText(Path.Combine(root, "asset.bin"), "asset");
            Assert.Contains("Directory is not empty", PlanRepositoryContext.Capture(root, "game"));
            var missing = PlanRepositoryContext.Capture(Path.Combine(root, "missing"), "game");
            Assert.Contains("directory errors", missing);
            Assert.DoesNotContain("Confirmed empty directory", missing);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WebProjectIncludesManifestAndEntryPointWithoutArbitraryJsonSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"test\":\"node test.js\"}}");
            File.WriteAllText(Path.Combine(root, "index.html"), "<canvas></canvas>");
            File.WriteAllText(Path.Combine(root, "style.css"), "canvas { color: red; }");
            File.WriteAllText(Path.Combine(root, "credentials.json"), "do-not-read");
            File.WriteAllText(Path.Combine(root, "settings.json"), "private-settings");
            var snapshot = PlanRepositoryContext.Capture(root, "game");
            Assert.Contains("node test.js", snapshot);
            Assert.Contains("<canvas>", snapshot);
            Assert.Contains("style.css", snapshot);
            Assert.DoesNotContain("do-not-read", snapshot);
            Assert.DoesNotContain("private-settings", snapshot);
        }
        finally { Directory.Delete(root, true); }
    }
}
