using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class McpApprovalGateTests
{
    private static (McpApprovalGate gate, int promptCount) BuildGate(
        MandoCodeConfig config,
        DiffApprovalResponse responseToReturn)
    {
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = responseToReturn });
        };
        return (gate, count);
    }

    [Fact]
    public async Task FirstCall_TriggersPrompt()
    {
        var config = new MandoCodeConfig
        {
            McpServers = { ["solana"] = new McpServerConfig { Url = "http://x" } }
        };
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });
        };

        var result = await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(DiffApprovalResponse.Approved, result.Response);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ApprovedNoAskAgain_SuppressesSubsequentPrompts()
    {
        var config = new MandoCodeConfig
        {
            McpServers = { ["solana"] = new McpServerConfig { Url = "http://x" } }
        };
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain });
        };

        await gate.RequestAsync("solana", "get_balance");
        var second = await gate.RequestAsync("solana", "get_balance");
        var third = await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(DiffApprovalResponse.Approved, second.Response);
        Assert.Equal(DiffApprovalResponse.Approved, third.Response);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AutoApproveList_SkipsPromptEntirely()
    {
        var config = new MandoCodeConfig
        {
            McpServers =
            {
                ["solana"] = new McpServerConfig
                {
                    Url = "http://x",
                    AutoApprove = { "get_balance" }
                }
            }
        };
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Denied });
        };

        var result = await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(DiffApprovalResponse.Approved, result.Response);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeniedIsNotCached()
    {
        // Denials must re-prompt on next call — the user may change their mind, and the
        // model may legitimately retry the same tool after getting new information.
        var config = new MandoCodeConfig
        {
            McpServers = { ["solana"] = new McpServerConfig { Url = "http://x" } }
        };
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Denied });
        };

        await gate.RequestAsync("solana", "get_balance");
        await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ResetSession_ClearsApprovedNoAskAgain()
    {
        var config = new MandoCodeConfig
        {
            McpServers = { ["solana"] = new McpServerConfig { Url = "http://x" } }
        };
        var gate = new McpApprovalGate(config);
        int count = 0;
        gate.OnApprovalRequested = (_, _, _) =>
        {
            count++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain });
        };

        await gate.RequestAsync("solana", "get_balance");
        gate.ResetSession();
        await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task NoCallback_FailsOpenWithApproval()
    {
        // No UI wired up (e.g. unit tests of other services) — the gate must not block,
        // otherwise every MCP tool call in tests would hang forever.
        var config = new MandoCodeConfig
        {
            McpServers = { ["solana"] = new McpServerConfig { Url = "http://x" } }
        };
        var gate = new McpApprovalGate(config);

        var result = await gate.RequestAsync("solana", "get_balance");

        Assert.Equal(DiffApprovalResponse.Approved, result.Response);
    }
}
