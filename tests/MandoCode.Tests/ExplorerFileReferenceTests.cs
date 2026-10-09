using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public class ExplorerFileReferenceTests
{
    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("My Folder/My File.cs")]
    [InlineData("folder/a\"b.txt")]
    [InlineData("café/文.txt")]
    public void ReferenceTokensRoundTrip(string path)
    {
        var token = FileReferenceToken.Format(path);
        Assert.Equal(path, Assert.Single(FileReferenceToken.Paths("explain " + token + " please")));
    }

    [Fact]
    public void InsertingAtCaretPreservesDraftAndReplacesAnUnfinishedReference()
    {
        var composer = new PromptComposerState(new InputStateMachine(new Dictionary<string, string>(), null));
        composer.SetText("explain @old please");
        composer.Buffer.Begin("explain @old".Length, 1, false);
        composer.Buffer.End();
        composer.InsertFileReference("src/Program.cs");
        Assert.Equal("explain @src/Program.cs  please", composer.Buffer.Text);
        Assert.Equal("explain @src/Program.cs ".Length, composer.Buffer.Cursor);
        Assert.Null(composer.Suggestions.SubmittedText);
        Assert.False(composer.IsOpen);
    }

    [Fact]
    public void InsertingAddsSeparationAndSupportsMultipleReferences()
    {
        var composer = new PromptComposerState(new InputStateMachine(new Dictionary<string, string>(), null));
        composer.SetText("compare");
        composer.InsertFileReference("first.txt");
        composer.InsertFileReference("My Folder/second.txt");
        Assert.StartsWith("compare @first.txt ", composer.Buffer.Text);
        Assert.Equal(new[] { "first.txt", "My Folder/second.txt" }, FileReferenceToken.Paths(composer.Buffer.Text));
    }
}
