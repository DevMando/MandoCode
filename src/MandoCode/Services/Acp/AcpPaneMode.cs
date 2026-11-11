using Microsoft.Extensions.DependencyInjection;

namespace MandoCode.Services.Acp;

/// <summary>
/// Moves one pane between running MandoCode's own agent and running an external ACP agent. The native session
/// (conversation, model, MCP) is not touched in either direction, and nothing is passed between the two.
/// </summary>
public static class AcpPaneMode
{
    /// <summary>Marks the pane as running the ACP agent <paramref name="agentName"/>. The caller starts the agent.</summary>
    public static void Enter(AgentPane pane, string agentName)
    {
        pane.Runtime = AgentRuntime.Acp;
        pane.AcpAgentName = agentName;
        pane.AcpHeader = null;
        pane.Services.GetRequiredService<AcpRuntimeHolder>().IsAcpPane = true;
        pane.Workspace.Refresh();
    }

    /// <summary>Ends the pane's ACP session (the agent's process tree is killed) and hands the pane back to the native agent.</summary>
    public static async Task LeaveAsync(AgentPane pane)
    {
        var holder = pane.Services.GetRequiredService<AcpRuntimeHolder>();
        await holder.DisposeAsync();
        holder.IsAcpPane = false;
        pane.Runtime = AgentRuntime.Native;
        pane.AcpAgentName = null;
        pane.AcpHeader = null;
        pane.Workspace.Refresh();
    }

    /// <summary>Ends only the ACP session, for a switch to another ACP agent in the same pane.</summary>
    public static Task CloseSessionAsync(AgentPane pane) =>
        pane.Services.GetRequiredService<AcpRuntimeHolder>().DisposeAsync().AsTask();
}
