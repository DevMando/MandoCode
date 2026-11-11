namespace MandoCode.Services.Acp;

/// <summary>Where the startup of an ACP pane reports to. The pane implements it over its transcript.</summary>
public interface IAcpStartupSink
{
    void Connecting(string agentName);
    void Connected(string description, IReadOnlyList<string> warnings);
    void Failed(string message);
}

/// <summary>
/// Connects a pane's runtime without ever throwing: the pane must stay usable (it shows the reason and accepts
/// <c>/agent-acp restart</c> or being closed) whatever goes wrong while the agent starts.
/// </summary>
public static class AcpPaneStartup
{
    /// <returns>True when the agent is ready.</returns>
    public static async Task<bool> RunAsync(IAcpAgentRuntime runtime, IAcpStartupSink sink, CancellationToken cancellationToken)
    {
        sink.Connecting(runtime.AgentName);
        try
        {
            await runtime.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var description = (runtime.AgentTitle ?? runtime.AgentName)
                + (runtime.CurrentMode is { } mode ? $" (mode: {mode})" : "");
            sink.Connected(description, runtime.StartupWarnings);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The pane was closed or restarted while the agent was starting.
            return false;
        }
        catch (AcpRuntimeException ex)
        {
            sink.Failed(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // Anything unexpected (a corrupt trust file, an IO error) must not leave a pane that never takes input.
            sink.Failed($"Starting the agent failed: {ex.Message}{Environment.NewLine}{AcpErrorMessages.RestartHint}");
            return false;
        }
    }
}
