using System.Reflection;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MandoCode.Tests;

public sealed class CliIntegrationCoordinatorTests
{
    [Fact]
    public async Task ServerUpdates_PreserveConversationsAndAgentOptions_AndReachNewAgents()
    {
        var config = new MandoCodeConfig { AllowPersistence = false, ModelName = "test:cloud", EnableMcp = false };
        var registrations = new ServiceCollection().AddLogging();
        Program.RegisterAgentServices(registrations, config, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        var second = workspace.Add();
        first.Services.GetRequiredService<MandoCodeConfig>().Temperature = 0.7;
        second.Services.GetRequiredService<MandoCodeConfig>().Temperature = 0.2;
        first.Services.GetRequiredService<AIService>().AppendAssistantNote("Keep this conversation");
        var coordinator = services.GetRequiredService<CliIntegrationCoordinator>();
        var definitions = new Dictionary<string, McpServerConfig> { ["example"] = new() { Command = "unused", Disabled = true, Args = ["one argument"], Env = new() { ["KEY"] = "secret" }, AutoApprove = ["read"] } };
        await coordinator.SaveServersAsync(definitions, workspace.Panes);
        definitions["example"].Env["KEY"] = "changed";
        Assert.Equal("secret", first.Services.GetRequiredService<MandoCodeConfig>().McpServers["example"].Env["KEY"]);
        Assert.Equal(0.7, first.Services.GetRequiredService<MandoCodeConfig>().Temperature);
        Assert.Equal(0.2, second.Services.GetRequiredService<MandoCodeConfig>().Temperature);
        Assert.False(second.Services.GetRequiredService<MandoCodeConfig>().EnableMcp);
        var history = (IEnumerable<Microsoft.Extensions.AI.ChatMessage>)typeof(AIService).GetField("_chatHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first.Services.GetRequiredService<AIService>())!;
        Assert.Contains(history, message => message.Text == "Keep this conversation");
        Assert.Equal("secret", workspace.Add().Services.GetRequiredService<MandoCodeConfig>().McpServers["example"].Env["KEY"]);
    }

    [Fact]
    public async Task Persist_ChangesOnlyServersAndReportsBadConfigWithoutReplacingIt()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mandocode-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "config.json");
            new MandoCodeConfig { Temperature = 0.7, ModelName = "saved:cloud" }.Save(path);
            var coordinator = new CliIntegrationCoordinator(new MandoCodeConfig { Temperature = 0.2 }, path);
            await coordinator.SaveServersAsync(new() { ["test"] = new() { Command = "unused", Disabled = true } }, []);
            var saved = MandoCodeConfig.Load(path);
            Assert.Equal(0.7, saved.Temperature);
            Assert.Equal("saved:cloud", saved.ModelName);
            Assert.True(saved.McpServers.ContainsKey("test"));
            File.WriteAllText(path, "invalid json");
            await Assert.ThrowsAsync<JsonException>(() => coordinator.SaveServersAsync(new(), []));
            Assert.Equal("invalid json", File.ReadAllText(path));
            Assert.True(coordinator.Servers.ContainsKey("test"));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void SharedUpdateLease_RejectsOverlapAndReleasesAfterFailure()
    {
        var coordinator = new CliIntegrationCoordinator(new MandoCodeConfig());
        using (coordinator.BeginUpdate()) { Assert.True(coordinator.IsUpdating); Assert.Throws<InvalidOperationException>(() => coordinator.BeginUpdate()); }
        Assert.False(coordinator.IsUpdating);
        using var again = coordinator.BeginUpdate();
        Assert.True(coordinator.IsUpdating);
    }

    [Fact]
    public void ServerValidationAndPairParsing_KeepArgumentsAndSecretValuesIntact()
    {
        var pairs = CliIntegrationCoordinator.ParsePairs("TOKEN=a=b=c\nHEADER=Bearer token\n");
        Assert.Equal("a=b=c", pairs["TOKEN"]);
        Assert.Equal("Bearer token", pairs["HEADER"]);
        Assert.Throws<ArgumentException>(() => CliIntegrationCoordinator.ParsePairs("missing equals"));
        CliIntegrationCoordinator.ValidateServer("valid-name", new() { Transport = "http", Url = "https://example.com/mcp" });
        Assert.Throws<ArgumentException>(() => CliIntegrationCoordinator.ValidateServer("invalid name", new() { Command = "node" }));
        Assert.Throws<ArgumentException>(() => CliIntegrationCoordinator.ValidateServer("valid", new() { Transport = "http", Url = "file:///tmp/server" }));
        Assert.Throws<ArgumentException>(() => CliIntegrationCoordinator.ValidateServer("valid", new()));
    }
}
