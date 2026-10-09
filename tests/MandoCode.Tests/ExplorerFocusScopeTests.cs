using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public class ExplorerFocusScopeTests
{
    [Fact]
    public async Task OpenExplorerAlternatesExactlyTwoTargetsAndNotifiesDimming()
    {
        var scope = new ExplorerFocusScope { Active = true };
        var calls = new List<string>();
        var changes = 0;
        scope.Changed += () => changes++;
        scope.FocusExplorer = () => { calls.Add("explorer"); scope.SetPromptFocused(false); return Task.CompletedTask; };
        scope.FocusPrompt = () => { calls.Add("prompt"); scope.SetPromptFocused(true); return Task.CompletedTask; };
        await scope.ToggleAsync();
        Assert.False(scope.PromptFocused);
        await scope.ToggleAsync();
        Assert.True(scope.PromptFocused);
        await scope.ToggleAsync();
        Assert.Equal(new[] { "explorer", "prompt", "explorer" }, calls);
        Assert.Equal(3, changes);
        scope.Active = false;
        await scope.ToggleAsync();
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task AgentsHaveIndependentFocusAndMissingTargetDoesNotChangeState()
    {
        var first = new ExplorerFocusScope { Active = true };
        var second = new ExplorerFocusScope { Active = true };
        first.FocusExplorer = () => { first.SetPromptFocused(false); return Task.CompletedTask; };
        await first.ToggleAsync();
        Assert.False(first.PromptFocused);
        Assert.True(second.PromptFocused);
        await second.ToggleAsync();
        Assert.True(second.PromptFocused);
    }
}
