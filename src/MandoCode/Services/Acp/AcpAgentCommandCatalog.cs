using MandoCode.Acp.Protocol;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

/// <summary>Agent commands are session-scoped suggestions and prompts, never shell invocations.</summary>
public static class AcpAgentCommandCatalog
{
    private static bool ValidName(string name) => name.Length > 0 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    private static bool Reserved(string name) => SlashCommands.All.Keys.Any(key =>
        string.Equals(key.TrimStart('/').Split(' ', 2)[0], name, StringComparison.OrdinalIgnoreCase)) ||
        AcpPaneRestrictions.IsCommandBlocked(name);

    public static IReadOnlyList<AvailableCommand> Forwardable(IAcpAgentRuntime? runtime) =>
        runtime is not { Status: AcpRuntimeStatus.Ready } ? [] : runtime.AvailableCommands
            .Where(c => ValidName(c.Name) && (!Reserved(c.Name) || c.Name.Equals("compact", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool CanForward(IAcpAgentRuntime? runtime, string input)
    {
        var name = input.TrimStart().TrimStart('/').Split(' ', 2)[0];
        return Forwardable(runtime).Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyDictionary<string, string> Describe(IAcpAgentRuntime? runtime)
    {
        var descriptions = AcpPaneRestrictions.DescribeCommands(SlashCommands.All).ToDictionary(kv => kv.Key, kv => kv.Value);
        var label = string.IsNullOrWhiteSpace(runtime?.AgentDisplayName) ? "agent" : runtime.AgentDisplayName.Trim();
        foreach (var command in Forwardable(runtime))
        {
            var key = command.Name.Equals("compact", StringComparison.OrdinalIgnoreCase) ? "/compact" : "/" + command.Name;
            var hint = string.IsNullOrWhiteSpace(command.Input?.Hint) ? "" : $" ({command.Input.Hint})";
            descriptions[key] = $"[{label}] {command.Description}{hint}";
        }
        return descriptions;
    }
}
