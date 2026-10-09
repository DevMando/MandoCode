using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public sealed class PresentationBoundaryTests
{
    [Fact]
    public void ClosingNestedPanelKeepsParentModalAndExplorerState()
    {
        var state = new AgentPresentationState();
        state.SetOpen(AgentPanel.FileExplorer, true);
        Assert.False(state.HidesChatPrompt);
        Assert.False(state.BlocksRequests);
        state.SetOpen(AgentPanel.Settings, true);
        state.SetOpen(AgentPanel.Integrations, true);
        state.SetOpen(AgentPanel.Integrations, false);
        Assert.True(state.HidesChatPrompt);
        Assert.True(state.BlocksRequests);
        Assert.True(state.IsOpen(AgentPanel.FileExplorer));
        state.SetOpen(AgentPanel.Settings, false);
        Assert.False(state.HidesChatPrompt);
    }

    [Fact]
    public void DirectoryPickerHidesPromptWithoutBlockingAgentRequests()
    {
        var state = new AgentPresentationState();
        state.SetOpen(AgentPanel.DirectoryBrowser, true);
        Assert.True(state.HidesChatPrompt);
        Assert.False(state.BlocksRequests);
    }

    [Fact]
    public void OldComponentDisposalDoesNotClearNewOwnersCallback()
    {
        Action? callback = null;
        var oldOwner = new CallbackRegistrations();
        var newOwner = new CallbackRegistrations();
        Action original = () => { };
        Action replacement = () => { };
        oldOwner.Bind(() => callback, value => callback = value, original);
        newOwner.Bind(() => callback, value => callback = value, replacement);
        oldOwner.Dispose();
        Assert.Same(replacement, callback);
        newOwner.Dispose();
        newOwner.Dispose();
        Assert.Null(callback);
        Assert.Throws<ObjectDisposedException>(() => newOwner.Bind(() => callback, value => callback = value, original));
    }

    [Fact]
    public void CommandParsingPreservesCaseSensitiveArguments()
    {
        var command = CliCommand.Parse(" /TRANSCRIPT-SAVE C:/Work/MyFile.md ");
        Assert.Equal("transcript-save", command.Name);
        Assert.Equal("C:/Work/MyFile.md", command.Arguments);
        Assert.Throws<ArgumentException>(() => CliCommand.Parse("hello"));
    }
}
