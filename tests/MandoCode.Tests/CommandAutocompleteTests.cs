using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class CommandAutocompleteTests
{
    [Fact]
    public void VisibleWindow_ShowsEverythingWhenItFits()
    {
        Assert.Equal((0, 10), CommandAutocomplete.VisibleWindow(10, 3, 30));
    }

    [Fact]
    public void VisibleWindow_ClipsTheFullCommandListToAShortTerminal()
    {
        // The crash: every command at once in a ~30-row window pushed the prompt above row 0.
        var total = SlashCommands.All.Count;
        var (start, count) = CommandAutocomplete.VisibleWindow(total, 0, 26);

        Assert.Equal(0, start);
        Assert.Equal(26, count);
    }

    [Theory]
    [InlineData(0, 0)]    // top: pinned to the start
    [InlineData(15, 10)]  // middle: selection centered
    [InlineData(30, 21)]  // bottom: pinned to the end
    public void VisibleWindow_KeepsTheSelectionOnScreen(int selected, int expectedStart)
    {
        var (start, count) = CommandAutocomplete.VisibleWindow(31, selected, 10);

        Assert.Equal(expectedStart, start);
        Assert.Equal(10, count);
        Assert.InRange(selected, start, start + count - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void VisibleWindow_AlwaysShowsAtLeastOneRow(int maxRows)
    {
        Assert.Equal((4, 1), CommandAutocomplete.VisibleWindow(31, 4, maxRows));
    }

    [Fact]
    public void VisibleWindow_EmptyListShowsNothing()
    {
        Assert.Equal((0, 0), CommandAutocomplete.VisibleWindow(0, 0, 10));
    }

    [Fact]
    public void DropdownHint_ShowsPositionOnlyWhenClipped()
    {
        Assert.EndsWith("(12/31)", CommandAutocomplete.DropdownHint(31, 26, 11));
        Assert.DoesNotContain("/", CommandAutocomplete.DropdownHint(31, 31, 11).Replace("TAB/Enter", ""));
    }
}
