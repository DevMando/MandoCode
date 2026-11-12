using MandoCode.Acp;
using MandoCode.Acp.Protocol;
using Spectre.Console;

namespace MandoCode.Services.Acp;

/// <summary>One entry of a question asked in the pane.</summary>
public sealed record AcpChoice(string Label, Color Color);

/// <summary>Asks the user to pick one entry; null when they dismissed the question.</summary>
public interface IAcpChoicePrompt
{
    Task<string?> ChooseAsync(string heading, IReadOnlyList<AcpChoice> choices);
}

/// <summary>The pane's <see cref="ApprovalSelectCoordinator"/> menu, serialized behind the panel's prompt gate (like the trust question).</summary>
public sealed class ApprovalMenuChoicePrompt(
    ApprovalPromptGate gate, ApprovalSelectCoordinator menu, Func<IDisposable> suppressKeys, Action<string> writeLine) : IAcpChoicePrompt
{
    public async Task<string?> ChooseAsync(string heading, IReadOnlyList<AcpChoice> choices)
    {
        using var hold = await gate.AcquireAsync();
        writeLine("");
        writeLine(heading);
        using (suppressKeys())
        {
            var picked = await menu.RequestAsync(
                choices.Select(c => new ApprovalSelectCoordinator.Option(c.Label, c.Color)).ToList(), cancelOnEscape: true);
            return string.IsNullOrEmpty(picked) ? null : picked;
        }
    }
}

/// <summary>
/// Commands an ACP pane answers itself from what the agent reports, instead of MandoCode's own model loop:
/// <c>/model</c> (the agent's <c>model</c> config option) and <c>/compact</c> (the agent's own <c>compact</c> command).
/// Nothing here is tied to one adapter.
/// </summary>
public static class AcpPaneCommands
{
    public const string CancelLabel = "Cancel";
    public const string CompactCommand = "compact";

    public static SessionConfigOption? ModelOption(IAcpAgentRuntime runtime) =>
        runtime.ConfigOptions.FirstOrDefault(o => o.IsSelect && o.Category == ConfigOptionCategories.Model);

    public static bool AgentOffersCompact(IAcpAgentRuntime runtime) =>
        runtime.AvailableCommands.Any(c => string.Equals(c.Name, CompactCommand, StringComparison.OrdinalIgnoreCase));

    public const string NoCompactMessage =
        "This agent does not announce a /compact command, so there is nothing to compact. Use /clear to start a new ACP conversation.";

    /// <summary><c>/model</c> and <c>/model &lt;name or value&gt;</c>.</summary>
    public static async Task ModelAsync(
        IAcpAgentRuntime runtime, string arguments, IAcpChoicePrompt prompt, Action<string> write, CancellationToken cancellationToken)
    {
        if (ModelOption(runtime) is not { } option || option.Choices() is not { Count: > 0 } choices)
        {
            write("This agent does not expose a model setting, so the model cannot be changed from MandoCode. "
                  + "Change it in the agent's own configuration.");
            return;
        }

        var current = option.CurrentSelection;
        ConfigChoice? picked;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            picked = Match(choices, arguments.Trim());
            if (picked is null)
            {
                write($"'{arguments.Trim()}' is not one of this agent's models. Available: {string.Join(", ", choices.Select(c => c.Value))}.");
                return;
            }
        }
        else
        {
            var entries = choices.Select(c => new AcpChoice(Label(c, current), c.Value == current ? Color.Green : Color.White))
                .Append(new AcpChoice(CancelLabel, new Color(255, 200, 80))).ToList();
            var answer = await prompt.ChooseAsync($"Model of this ACP agent ({option.Name}):", entries).ConfigureAwait(false);
            if (answer is null || answer == CancelLabel)
                return;
            picked = choices.FirstOrDefault(c => Label(c, current) == answer);
            if (picked is null)
                return;
        }

        if (picked.Value == current)
        {
            write($"The agent already uses {picked.Name}.");
            return;
        }

        try
        {
            await runtime.SetConfigOptionAsync(option.Id, picked.Value, cancellationToken).ConfigureAwait(false);
            write($"Model set to {picked.Name} ({picked.Value}).");
        }
        catch (JsonRpcException ex)
        {
            write($"The agent did not accept that model: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            write(ex.Message);
        }
    }

    private static string Label(ConfigChoice choice, string? current) =>
        (choice.Group is null ? "" : choice.Group + ": ")
        + (choice.Name == choice.Value ? choice.Name : $"{choice.Name} ({choice.Value})")
        + (choice.Value == current ? "  ← current" : "");

    private static ConfigChoice? Match(IReadOnlyList<ConfigChoice> choices, string typed)
    {
        var exact = choices.FirstOrDefault(c => string.Equals(c.Value, typed, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(c.Name, typed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var prefix = choices.Where(c => c.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                                        || c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return prefix.Length == 1 ? prefix[0] : null;
    }
}
