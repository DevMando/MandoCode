using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class FileReferenceScopeTests
{
    [Fact]
    public void DirectoryReference_TellsModelToKeepToolPathsInsideDirectory()
    {
        var instruction = FileAutocompleteProvider.BuildDirectoryScopeInstruction("MandoCode");

        Assert.Contains("prefixing relative paths with 'MandoCode/'", instruction);
        Assert.Contains("relativeDirectory='MandoCode'", instruction);
        Assert.Contains("Do not list the entire project root", instruction);
    }
}
