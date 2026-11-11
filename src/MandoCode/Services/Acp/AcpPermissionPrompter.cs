using MandoCode.Acp.Protocol;
using MandoCode.Models;
using Spectre.Console;

namespace MandoCode.Services.Acp;

/// <summary>
/// <c>session/request_permission</c> in the TUI: the agent's own options, 1:1, in the panel's
/// <see cref="ApprovalSelectCoordinator"/> menu behind the panel's <see cref="ApprovalPromptGate"/>. The choice goes back as
/// the chosen <c>optionId</c>; Escape, closing the menu or cancelling the turn answers <c>cancelled</c>. MandoCode does not
/// enforce anything: the agent applies the permission itself, and the dialog says so.
/// </summary>
public sealed class AcpPermissionPrompter
{
    private readonly ApprovalPromptGate _gate;
    private readonly ApprovalSelectCoordinator _menu;
    private readonly Func<IDisposable> _suppressKeys;
    private readonly Action<string> _writeLine;
    private readonly Func<string?> _currentMode;
    private readonly Func<IDisposable>? _pauseActivity;

    /// <param name="writeLine">Prints one plain-text line (never parsed as markup).</param>
    /// <param name="pauseActivity">Stops the spinner while the question is open; disposing resumes it.</param>
    public AcpPermissionPrompter(
        ApprovalPromptGate gate,
        ApprovalSelectCoordinator menu,
        Func<IDisposable> suppressKeys,
        Action<string> writeLine,
        Func<string?> currentMode,
        Func<IDisposable>? pauseActivity = null)
    {
        _gate = gate;
        _menu = menu;
        _suppressKeys = suppressKeys;
        _writeLine = writeLine;
        _currentMode = currentMode;
        _pauseActivity = pauseActivity;
    }

    public async ValueTask<RequestPermissionResponse> RequestAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var cancelled = new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() };
        if (request.Options.Count == 0)
            return cancelled;

        IDisposable hold;
        try
        {
            hold = await _gate.AcquireAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return cancelled;
        }

        using (hold)
        using (_pauseActivity?.Invoke())
        {
            if (cancellationToken.IsCancellationRequested)
                return cancelled;

            var labels = UniqueLabels(request.Options);
            foreach (var line in Describe(request, _currentMode()))
                _writeLine(line);

            var (preview, previewTitle) = BuildPreview(request.ToolCall);
            var options = request.Options
                .Select((option, i) => new ApprovalSelectCoordinator.Option(labels[i], ColorFor(option.Kind)))
                .ToList();

            string choice;
            using (_suppressKeys())
            {
                var answer = _menu.RequestAsync(options, preview, previewTitle, cancelOnEscape: true);
                using var registration = cancellationToken.Register(_menu.Cancel);
                choice = await answer;
            }

            var index = labels.ToList().IndexOf(choice);
            return index < 0 || choice == ApprovalSelectCoordinator.Cancelled
                ? cancelled
                : new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = request.Options[index].OptionId } };
        }
    }

    /// <summary>The text shown above the menu: what the agent wants, the current mode and who applies the answer.</summary>
    public static IReadOnlyList<string> Describe(RequestPermissionRequest request, string? mode)
    {
        var call = request.ToolCall;
        var lines = new List<string> { "" };
        var what = string.IsNullOrWhiteSpace(call.Title) ? "a tool call" : call.Title!;
        lines.Add($"The agent asks for permission ({call.GetEffectiveKind()}): {what}");
        foreach (var location in call.Locations ?? [])
            lines.Add($"  {location.Path}{(location.Line is { } line ? ":" + line : "")}");
        if (!string.IsNullOrWhiteSpace(mode))
            lines.Add($"Agent mode: {mode}");
        lines.Add("The agent applies your answer itself; MandoCode only passes it on.");
        lines.Add("");
        return lines;
    }

    /// <summary>Labels are what the user sees and what comes back, so equal names are told apart.</summary>
    private static string[] UniqueLabels(IReadOnlyList<PermissionOption> options)
    {
        var labels = new string[options.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < options.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(options[i].Name) ? options[i].OptionId : options[i].Name;
            seen[name] = seen.GetValueOrDefault(name) + 1;
            labels[i] = seen[name] == 1 ? name : $"{name} ({seen[name]})";
        }

        return labels;
    }

    private static Color ColorFor(string kind) => kind switch
    {
        PermissionOptionKinds.AllowOnce or PermissionOptionKinds.AllowAlways => Color.Green,
        PermissionOptionKinds.RejectOnce or PermissionOptionKinds.RejectAlways => Color.Red,
        _ => Color.Grey,
    };

    private static (IReadOnlyList<ApprovalSelectCoordinator.PreviewLine>? Lines, string? Title) BuildPreview(ToolCallUpdate call)
    {
        var diffs = (call.Content ?? []).OfType<DiffToolCallContent>().ToList();
        if (diffs.Count == 0)
            return (null, null);

        var lines = new List<ApprovalSelectCoordinator.PreviewLine>();
        foreach (var diff in diffs)
        {
            lines.Add(new(diff.Path, Color.Grey, Decoration.Bold));
            foreach (var d in DiffService.CollapseContext(DiffService.ComputeDiff(diff.OldText, diff.NewText)))
            {
                var number = (d.NewLineNumber ?? d.OldLineNumber)?.ToString() ?? "";
                var text = $"{number,5} {Marker(d.LineType)} {d.Content}";
                lines.Add(new(text, d.LineType switch
                {
                    DiffLineType.Added => Color.Green,
                    DiffLineType.Removed => Color.Red,
                    _ => Color.Grey,
                }));
            }
        }

        return (lines, "Proposed change");
    }

    private static char Marker(DiffLineType type) => type switch
    {
        DiffLineType.Added => '+',
        DiffLineType.Removed => '-',
        _ => ' ',
    };
}
