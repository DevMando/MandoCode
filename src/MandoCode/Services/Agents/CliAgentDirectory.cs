using Microsoft.Extensions.DependencyInjection;
using MandoCode.Models;

namespace MandoCode.Services;

public sealed class CliAgentDirectory(AgentPane self)
{
    public AgentPane[] All => (self.Workspace.Registry?.Workspaces.SelectMany(w => w.Panes) ?? self.Workspace.Panes).ToArray();
    public AgentPane? Resolve(string name)
    {
        var all = All;
        var exact = all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var matches = all.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return name.Length > 0 && matches.Length == 1 ? matches[0] : null;
    }
    public AgentPane[] Match(string fragment) => All.Where(p => p != self && p.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name).ToArray();
    public static string Describe(AgentPane pane) =>
        $"Agent {pane.Name} · workspace {pane.Workspace.Name} · {pane.Services.GetRequiredService<ProjectRootAccessor>().ProjectRoot} · model {pane.Model} · " +
        (pane.IsBusy?.Invoke() == true ? "working" : "idle");
    public string ExpandMentions(string input)
    {
        var mentioned = FileReferenceToken.Paths(input).Where(IsAgentName).Select(Resolve).Where(p => p is not null && p != self).Distinct().ToArray();
        if (mentioned.Length == 0) return input;
        return input + "\n\nMentioned open agents (not files):\n" + string.Join("\n", mentioned.Select(p => Describe(p!))) +
            "\nUse get_agent_status/read_agent_transcript for observation. Prefer background interactions for independent work the user requests; finish the handoff so the user can keep talking while it runs. Refer to open agents with @Name (quote names containing spaces) in prose so the UI can identify them. " +
            "Use ask_agent_and_wait only when its answer is needed to continue the current request; it holds your turn open. Use ask_agent_async for independent questions, send_agent_message for information with no reply requested, request_agent_review for read-only review, and handoff_to_agent for transferring a clearly scoped responsibility. IDs identify background interactions; update_agent_job queues context for the next turn, cancel_agent_job requests cancellation, and wait_for_agent_job explicitly waits when you reach a dependency. Do not assume background work succeeded before its result arrives. An @mention alone does not instruct you to delegate work.";
    }
    public static bool IsAgentName(string token) => !token.Contains('/') && !token.Contains('\\');
}

public sealed record CliPeerAnswer(bool Answered, string Text);
public sealed record CliPeerRequest(string From, string Question, IReadOnlyList<string> Chain, CancellationToken Cancellation)
{
    public bool IsDelegation { get; init; }
    public string InteractionId { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "question";
    public TaskCompletionSource<CliPeerAnswer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class CliAgentCallChain
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> Current = new();
    private static readonly AsyncLocal<bool> Review = new();
    public static bool IsReview => Review.Value;
    public static IReadOnlyList<string> Capture => Current.Value ?? [];
    public static bool Reject(string key) => Capture.Contains(key) || Capture.Count >= 3;
    public static IDisposable Enter(string key, IReadOnlyList<string>? incoming = null, bool review = false)
    {
        var previous = Current.Value;
        var previousReview = Review.Value;
        Review.Value = review;
        Current.Value = (incoming ?? previous ?? []).Append(key).ToArray();
        return new Scope(() => { Current.Value = previous; Review.Value = previousReview; });
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
