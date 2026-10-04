using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class PromptComposerStateTests
{
    [Fact]
    public void EnterAcceptsCommand_WithoutSubmitting_AndEscapePreservesText()
    {
        var machine = new InputStateMachine(new Dictionary<string, string> { ["/help"] = "Help", ["/history"] = "History" }, null);
        var composer = new PromptComposerState(machine);
        composer.SetText("/he");
        Assert.True(composer.IsOpen);
        Assert.True(composer.HandleKey("Enter"));
        Assert.Equal("/help", composer.Buffer.Text);
        Assert.False(composer.IsOpen);
        Assert.Null(machine.State.SubmittedText);
        composer.SetText("/h");
        Assert.True(composer.HandleKey("Escape"));
        Assert.Equal("/h", composer.Buffer.Text);
        Assert.False(composer.IsOpen);
    }

    [Fact]
    public void FileCompletionAtCaret_PreservesTheRestOfTheMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tui-composer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Program.cs"), "example");
            var provider = new FileAutocompleteProvider(new ProjectRootAccessor(directory), new HashSet<string>());
            var composer = new PromptComposerState(new InputStateMachine(new Dictionary<string, string>(), provider));
            composer.SetText("explain @Pro please");
            composer.Buffer.Begin("explain @Pro".Length, 1, false);
            composer.Buffer.End();
            composer.Refresh();
            Assert.True(composer.IsOpen);
            Assert.True(composer.HandleKey("Tab"));
            Assert.Contains("@Program.cs", composer.Buffer.Text);
            Assert.EndsWith(" please", composer.Buffer.Text);
        }
        finally { Directory.Delete(directory, true); }
    }
}
