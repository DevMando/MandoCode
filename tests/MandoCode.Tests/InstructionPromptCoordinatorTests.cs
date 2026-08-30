using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public sealed class InstructionPromptCoordinatorTests
{
    [Fact]
    public async Task RequestAsync_ExposesInitialValueUntilSubmission()
    {
        var coordinator = new InstructionPromptCoordinator();

        var request = coordinator.RequestAsync("Edit instruction:", "create beta.txt");

        Assert.True(coordinator.IsActive);
        Assert.Equal("Edit instruction:", coordinator.Prompt);
        Assert.Equal("create beta.txt", coordinator.InitialValue);

        coordinator.Submit("create gamma.txt");

        Assert.Equal("create gamma.txt", await request);
        Assert.False(coordinator.IsActive);
        Assert.Empty(coordinator.Prompt);
        Assert.Empty(coordinator.InitialValue);
    }

    [Fact]
    public void RequestAsync_WithoutInitialValue_UsesEmptyText()
    {
        var coordinator = new InstructionPromptCoordinator();

        _ = coordinator.RequestAsync("Enter instructions:");

        Assert.Empty(coordinator.InitialValue);
    }
}
