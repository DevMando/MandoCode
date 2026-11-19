using MandoCode.Acp.Protocol;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpAgentCommandCatalogTests
{
    [Fact]
    public async Task SuggestionsFollowAgentUpdatesAndLeavingAcpWithoutLeakingAcrossPanes()
    {
        await using var services = AcpTestHost.Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var pane = workspace.Add(acpAgent: "test");
        var other = workspace.Add();
        AcpPaneMode.Enter(pane, "test");
        var runtime = new FakeRuntime { AgentDisplayName = "Codex", AvailableCommands = [new AvailableCommand {
            Name = "review", Description = "Review changes", Input = new() { Hint = "focus" } }] };
        pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;
        var input = pane.Services.GetRequiredService<InputStateMachine>();
        input.UpdateText("/rev");
        Assert.Contains("/review", input.State.DropdownItems);
        Assert.Equal("[Codex] Review changes (focus)", input.State.CommandDescriptions["/review"]);
        Assert.DoesNotContain("/review", other.Services.GetRequiredService<InputStateMachine>().GetAllCommands());

        runtime.AvailableCommands = [new AvailableCommand { Name = "inspect" }];
        input.RefreshCommandSuggestions();
        Assert.DoesNotContain("/review", input.State.DropdownItems);
        input.UpdateText("/ins");
        Assert.Contains("/inspect", input.State.DropdownItems);
        runtime.Status = AcpRuntimeStatus.Closed;
        Assert.DoesNotContain("/inspect", input.GetAllCommands());
        runtime.Status = AcpRuntimeStatus.Ready;
        await AcpPaneMode.LeaveAsync(pane);
        Assert.DoesNotContain("/inspect", input.GetAllCommands());
        Assert.Equal(SlashCommands.All["/clear"], input.State.CommandDescriptions["/clear"]);
    }

    [Theory]
    [InlineData("Codex", "Codex")]
    [InlineData("Claude", "Claude")]
    [InlineData(null, "acp-agent")]
    [InlineData("   ", "acp-agent")]
    public void CommandLabelUsesTheConnectedAgentsDisplayName(string? name, string expected)
    {
        var runtime = new FakeRuntime { AgentDisplayName = name,
            AvailableCommands = [new AvailableCommand { Name = "review", Description = "Review changes" }] };
        Assert.Equal($"[{expected}] Review changes", AcpAgentCommandCatalog.Describe(runtime)["/review"]);
    }

    [Fact]
    public void CatalogRejectsInvalidNamesAndCollisionsAndDeduplicates()
    {
        var runtime = new FakeRuntime { AvailableCommands = new[] {
            "review", "REVIEW", "clear", "plan", "model", "exit", "ollama-custom", "", "/bad", "two words", "bad\nname", "compact"
        }.Select(name => new AvailableCommand { Name = name }).ToArray() };
        Assert.Equal(new[] { "review", "compact" }, AcpAgentCommandCatalog.Forwardable(runtime).Select(c => c.Name));
        Assert.True(AcpAgentCommandCatalog.CanForward(runtime, "/REVIEW Keep Case"));
        Assert.False(AcpAgentCommandCatalog.CanForward(runtime, "/unannounced"));
        Assert.False(AcpAgentCommandCatalog.CanForward(runtime, "/clear"));
    }
}
