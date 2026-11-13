namespace MandoCode.Acp.Transport;

/// <summary>Everything needed to launch an agent. Built from the user's configuration by the application.</summary>
/// <param name="AgentName">Display name, used in logs and messages.</param>
/// <param name="Command">A bare command name (looked up in PATH) or an absolute path to a native executable.</param>
/// <param name="Args">Arguments, passed one by one (never through a shell).</param>
/// <param name="Env">Extra environment variables from the agent entry; they win over inherited ones.</param>
/// <param name="PassAnthropicApiKey">When false (the default) <c>ANTHROPIC_API_KEY</c> is removed from the inherited environment.</param>
/// <param name="WorkingDirectory">The agent's working directory (the project root).</param>
public sealed record AcpLaunchSpec(
    string AgentName,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env,
    bool PassAnthropicApiKey,
    string WorkingDirectory);
