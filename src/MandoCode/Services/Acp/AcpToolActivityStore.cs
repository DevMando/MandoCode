using MandoCode.Acp.Protocol;
using Spectre.Console.Rendering;

namespace MandoCode.Services.Acp;

/// <summary>Per-pane, per-turn mapping from ACP ids to persistent transcript rows.</summary>
public sealed class AcpToolActivityStore(Action<AcpToolActivity, bool> publish)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AcpToolActivity> _calls = new(StringComparer.Ordinal);
    private bool _ended;
    private const int TextLimit = 32_000;

    public void BeginTurn()
    {
        lock (_gate) { _calls.Clear(); _ended = false; }
    }

    private AcpToolActivity Get(string id, out bool added)
    {
        added = !_calls.TryGetValue(id, out var activity);
        if (added) _calls[id] = activity = new AcpToolActivity(id);
        return activity!;
    }

    public void Apply(ToolCallBase update)
    {
        lock (_gate)
        {
            if (_ended) return;
            var activity = Get(update.ToolCallId, out var added);
            var old = activity.Snapshot;
            var status = IsFinal(old.Status) ? old.Status : update.Status switch
            {
                ToolCallStatuses.Completed => "Completed",
                ToolCallStatuses.Failed => "Failed",
                ToolCallStatuses.InProgress => "Running",
                ToolCallStatuses.Pending => "Pending",
                _ => old.Status
            };
            if (old.Status == "Awaiting approval" && status is "Pending" or "Running") status = old.Status;
            var output = old.Output.ToList();
            void AddOutput(string text)
            {
                if (output.Contains(text, StringComparer.Ordinal)) return;
                var remaining = TextLimit - output.Sum(value => value.Length);
                if (remaining > 0 && output.Count < 32) output.Add(Clip(text, remaining));
            }
            foreach (var content in update.Content ?? [])
                if (content is ContentToolCallContent { Content: TextContent text }) AddOutput(text.Text);
            if (update.RawOutput is { } raw) AddOutput(raw.ToString());
            activity.Update(old with
            {
                Title = string.IsNullOrWhiteSpace(update.Title) ? old.Title : Clip(update.Title, TextLimit),
                Kind = update.Kind ?? old.Kind,
                Status = status,
                Input = update.RawInput is { } input ? Clip(input.ToString(), TextLimit) : old.Input,
                Output = output.ToArray(),
                OutputUnavailable = old.OutputUnavailable || (update.Content ?? []).Any(content => content is TerminalToolCallContent)
            });
            publish(activity, added);
        }
    }

    public void Detail(string id, IRenderable detail)
    {
        lock (_gate)
        {
            if (_ended || !_calls.TryGetValue(id, out var activity)) return;
            var old = activity.Snapshot;
            if (old.Details.Count >= 32) return;
            activity.Update(old with { Details = old.Details.Append(detail).ToArray() });
            publish(activity, false);
        }
    }

    public void Permission(string id, bool? allowed)
    {
        lock (_gate)
        {
            if (_ended) return;
            var activity = Get(id, out var added);
            var old = activity.Snapshot;
            if (IsFinal(old.Status)) return;
            activity.Update(old with { Status = allowed is null ? "Awaiting approval" : allowed.Value ? "Pending" : "Permission denied" });
            publish(activity, added);
        }
    }

    public void EndTurn(string status)
    {
        lock (_gate)
        {
            _ended = true;
            foreach (var activity in _calls.Values)
            {
                if (IsFinal(activity.Snapshot.Status)) continue;
                activity.Update(activity.Snapshot with { Status = status });
                publish(activity, false);
            }
        }
    }

    private static bool IsFinal(string status) => status is "Completed" or "Failed" or "Permission denied" or "Cancelled" or "Interrupted" or "Unfinished";
    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..Math.Max(0, limit - 1)] + "…";
}
