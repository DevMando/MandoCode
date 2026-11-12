using MandoCode.Acp.Protocol;
using MandoCode.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services.Acp;

/// <summary>
/// Turns the agent's tool calls, plan and file changes into transcript blocks. Tool-call state is kept by <c>toolCallId</c>
/// so that <c>tool_call_update</c> (which carries only what changed) is shown as one line per call plus its final status;
/// the plan is always replaced as a whole; diffs go through the same renderer as MandoCode's own file edits.
/// Unknown content and update variants are ignored.
/// </summary>
public sealed class AcpTranscriptAdapter
{
    private sealed class ToolState
    {
        public string? Title;
        public string Kind = ToolKinds.Other;
        public IReadOnlyList<ToolCallLocation> Locations = [];
        public string Status = ToolCallStatuses.Pending;
        public bool Announced;
        public bool FinalShown;
        public bool TerminalNoted;
        public string? Phase;
        public List<DiffToolCallContent> PendingDiffs = [];
    }

    private readonly Func<OperationDisplayEvent, IRenderable> _renderOperation;
    private readonly string _projectRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolState> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shownDiffs = new(StringComparer.Ordinal);
    private IReadOnlyList<PlanEntry>? _plan;
    private bool _planChanged;

    /// <summary>Forgets tool calls, shown diffs and the plan: a new conversation starts with a clean slate.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _tools.Clear();
            _shownDiffs.Clear();
            _plan = null;
            _planChanged = false;
        }
    }

    /// <param name="renderOperation">Builds the display for a file operation (<c>OperationDisplayRenderer.BuildRenderable</c>).</param>
    public AcpTranscriptAdapter(Func<OperationDisplayEvent, IRenderable> renderOperation, string projectRoot)
    {
        _renderOperation = renderOperation;
        _projectRoot = projectRoot;
    }

    public const string TerminalNotSupported = "terminal output not supported";

    /// <summary>A new turn: calls from earlier turns are no longer interesting.</summary>
    public void BeginTurn()
    {
        lock (_gate)
        {
            _tools.Clear();
            _shownDiffs.Clear();
            _plan = null;
            _planChanged = false;
        }
    }

    public static string IconFor(string? kind) => kind switch
    {
        ToolKinds.Read => "📄",
        ToolKinds.Edit => "✏️",
        ToolKinds.Delete => "🗑️",
        ToolKinds.Move => "📦",
        ToolKinds.Search => "🔎",
        ToolKinds.Execute => "⚡",
        ToolKinds.Think => "🧠",
        ToolKinds.Fetch => "🌐",
        ToolKinds.SwitchMode => "🔀",
        _ => "⚡",
    };

    /// <summary>Applies a <c>tool_call</c> / <c>tool_call_update</c> and returns what to add to the transcript.</summary>
    public IReadOnlyList<IRenderable> Apply(ToolCallBase update)
    {
        var blocks = new List<IRenderable>();
        lock (_gate)
        {
            if (!_tools.TryGetValue(update.ToolCallId, out var state))
                _tools[update.ToolCallId] = state = new ToolState();

            if (!string.IsNullOrWhiteSpace(update.Title)) state.Title = update.Title;
            if (update.Kind is not null) state.Kind = update.Kind;
            if (update.Locations is { Count: > 0 }) state.Locations = update.Locations;
            if (update.Status is not null) state.Status = update.Status;
            else if (update is ToolCall call) state.Status = call.GetEffectiveStatus();

            if (!state.Announced)
            {
                state.Announced = true;
                blocks.Add(new Text(Headline(state), new Style(Color.Grey)));
            }

            // A diff is what the agent proposes, not what happened: it is shown once the call has completed
            // (a write through fs/write_text_file shows its own diff when it really happens).
            foreach (var diff in (update.Content ?? []).OfType<DiffToolCallContent>())
            {
                state.PendingDiffs.RemoveAll(d => string.Equals(d.Path, diff.Path, StringComparison.Ordinal));
                state.PendingDiffs.Add(diff);
            }

            if (!state.FinalShown && state.Status is ToolCallStatuses.Pending or ToolCallStatuses.InProgress
                && PhaseText(state) is { } phase && phase != state.Phase)
            {
                state.Phase = phase;
                blocks.Add(new Text("  ⎿ " + phase, new Style(Color.Grey)));
            }

            foreach (var content in update.Content ?? [])
            {
                switch (content)
                {
                    case TerminalToolCallContent when !state.TerminalNoted:
                        state.TerminalNoted = true;
                        blocks.Add(new Text("  ⎿ " + TerminalNotSupported, new Style(Color.Grey)));
                        break;

                    // Text, images, unknown variants: the call's title and status are what the transcript shows.
                }
            }

            if (!state.FinalShown && state.Status is ToolCallStatuses.Completed or ToolCallStatuses.Failed)
            {
                state.FinalShown = true;
                if (state.Status == ToolCallStatuses.Failed)
                {
                    blocks.Add(new Text("  ⎿ failed" + FailureDetail(update), new Style(Color.Red)));
                }
                else
                {
                    foreach (var diff in state.PendingDiffs)
                        if (ShowDiff(diff.Path, diff.OldText, diff.NewText) is { } rendered)
                            blocks.Add(rendered);
                    blocks.Add(new Text("  ⎿ completed", new Style(Color.Grey)));
                }

                state.PendingDiffs.Clear();
            }
        }

        return blocks;
    }

    /// <summary>What a call that changes things is doing while it is not finished; null for calls that just read.</summary>
    private static string? PhaseText(ToolState state)
    {
        var changes = state.Kind is ToolKinds.Edit or ToolKinds.Delete or ToolKinds.Move or ToolKinds.Execute || state.PendingDiffs.Count > 0;
        if (!changes)
            return null;
        if (state.Status == ToolCallStatuses.Pending)
            return "Awaiting permission…";
        return state.Kind == ToolKinds.Execute ? "Running…" : "Writing…";
    }

    /// <summary>
    /// The user's answer to the permission question for this call. A refusal ends the call at once ("permission denied"),
    /// without waiting for, or contradicting, whatever the agent reports afterwards.
    /// </summary>
    public IRenderable? NotePermission(string toolCallId, bool allowed)
    {
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolCallId, out var state))
                _tools[toolCallId] = state = new ToolState();
            if (allowed || state.FinalShown)
                return null;

            state.FinalShown = true;
            state.PendingDiffs.Clear();
            return new Text("  ⎿ permission denied", new Style(Color.Yellow));
        }
    }

    private static string FailureDetail(ToolCallBase update)
    {
        foreach (var content in (update.Content ?? []).OfType<ContentToolCallContent>())
        {
            if (content.Content is not TextContent { Text: { } text })
                continue;
            if (FirstMeaningfulLine(text) is { } line)
                return ": " + (line.Length > 160 ? line[..160] + "…" : line);
        }

        return "";
    }

    /// <summary>The first line that says something: agents wrap messages in code fences, and an empty fence is not a message.</summary>
    internal static string? FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var isFence = raw.TrimStart().StartsWith("```", StringComparison.Ordinal);
            var line = raw.Trim().Trim('`').Trim();
            // A fence line holds at most a language tag ("text", "console"), which is not a message.
            if (line.Length == 0 || (isFence && !line.Contains(' ')))
                continue;
            return line;
        }

        return null;
    }

    private static string Headline(ToolState state)
    {
        var title = string.IsNullOrWhiteSpace(state.Title) ? state.Kind : state.Title!;
        var where = state.Locations.Count == 0
            ? ""
            : " · " + string.Join(", ", state.Locations.Take(3).Select(l => l.Path + (l.Line is { } line ? ":" + line : ""))) + (state.Locations.Count > 3 ? $" (+{state.Locations.Count - 3})" : "");
        return $"{IconFor(state.Kind)} {title}{where}";
    }

    /// <summary>A file the agent wrote through <c>fs/write_text_file</c>: shown as a diff unless the tool call already showed it.</summary>
    public IRenderable? FileChanged(AcpFileChange change)
    {
        lock (_gate)
            return ShowDiff(change.Path, change.OldText, change.NewText);
    }

    private IRenderable? ShowDiff(string path, string? oldText, string newText)
    {
        var key = Normalize(path) + "\0" + newText.GetHashCode() + ":" + newText.Length;
        if (!_shownDiffs.Add(key))
            return null;

        var display = DisplayPath(path);
        var evt = new OperationDisplayEvent { FilePath = display };
        if (oldText is null)
        {
            var lines = newText.Replace("\r\n", "\n").Split('\n');
            const int preview = 10;
            evt.OperationType = "Write";
            evt.IsNewFile = true;
            evt.LineCount = lines.Length;
            evt.ContentPreview = string.Join('\n', lines.Take(preview));
            evt.RemainingLines = Math.Max(0, lines.Length - preview);
        }
        else
        {
            var diff = DiffService.ComputeDiff(oldText, newText);
            evt.OperationType = "Update";
            evt.Additions = diff.Count(d => d.LineType == DiffLineType.Added);
            evt.Deletions = diff.Count(d => d.LineType == DiffLineType.Removed);
            evt.InlineDiff = DiffService.CollapseContext(diff);
        }

        return _renderOperation(evt);
    }

    private string DisplayPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_projectRoot, full);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>A <c>plan</c> update: the previous plan is replaced as a whole.</summary>
    public void SetPlan(Plan plan)
    {
        lock (_gate)
        {
            _plan = plan.Entries.ToArray();
            _planChanged = true;
        }
    }

    /// <summary>A short status for the activity line, such as "Plan 2/5"; null without a plan.</summary>
    public string? PlanSummary()
    {
        lock (_gate)
            return _plan is { Count: > 0 } plan
                ? $"Plan {plan.Count(e => e.Status == PlanEntryStatuses.Completed)}/{plan.Count}"
                : null;
    }

    /// <summary>The checklist if the plan changed since it was last taken; null otherwise.</summary>
    public IRenderable? TakePlan()
    {
        lock (_gate)
        {
            if (!_planChanged || _plan is not { Count: > 0 } plan)
            {
                _planChanged = false;
                return null;
            }

            _planChanged = false;
            var rows = new List<IRenderable> { new Text("Plan", new Style(Color.Grey, decoration: Decoration.Bold)) };
            foreach (var entry in plan)
            {
                var (box, color) = entry.Status switch
                {
                    PlanEntryStatuses.Completed => ("[x]", Color.Green),
                    PlanEntryStatuses.InProgress => ("[>]", Color.Yellow),
                    _ => ("[ ]", Color.Grey),
                };
                rows.Add(new Text($"  {box} {entry.Content}", new Style(color)));
            }

            return new Rows(rows);
        }
    }
}
