using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace MandoCode.Services;

public sealed record CliDelegation(string Id, string FromKey, string ToKey, string ToName, string Task, string State, string? Result = null, string Kind = "task");

/// <summary>Tracks background interactions and inbox deliveries without running concurrent turns in a pane.</summary>
public sealed class CliDelegations : IDisposable
{
    private sealed class Job(CliDelegation value, CancellationTokenSource cancellation, string projectRoot)
    {
        public readonly object Sync = new();
        public CliDelegation Value = value;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly string ProjectRoot = projectRoot;
        public bool Finished;
        public readonly TaskCompletionSource<CliPeerAnswer> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private int _next;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _inbox = new();
    public CliDelegation Open(AgentPane from, AgentPane to, string task, string kind = "task", string state = "Working", CancellationTokenSource? cancellation = null)
    {
        var job = new CliDelegation($"job{Interlocked.Increment(ref _next)}", from.PersistKey, to.PersistKey, to.Name, task, state, Kind: kind);
        _jobs[job.Id] = new(job, cancellation ?? new(), from.Services.GetRequiredService<ProjectRootAccessor>().ProjectRoot);
        return job;
    }
    public CliDelegation[] For(string key) => _jobs.Values.Select(Read).Where(d => d.FromKey == key).OrderByDescending(d => int.Parse(d.Id[3..])).ToArray();
    private static CliDelegation Read(Job job) { lock (job.Sync) return job.Value; }
    public void Working(CliDelegation job)
    {
        if (!_jobs.TryGetValue(job.Id, out var entry)) return;
        lock (entry.Sync) { if (entry.Value.State == "Sent") entry.Value = entry.Value with { State = "Working" }; }
    }
    public void Complete(CliDelegation job, CliPeerAnswer answer, AgentPane from, bool declined = false)
    {
        var entry = _jobs[job.Id];
        CliDelegation finished;
        lock (entry.Sync)
        {
            if (entry.Finished) return;
            var state = entry.Value.State == "Cancellation requested" ? "Cancelled" : declined ? "Declined" : answer.Answered ? "Completed" : "Failed";
            finished = entry.Value = entry.Value with { State = state, Result = answer.Text };
            entry.Finished = true;
        }
        var report = $"{job.ToName} · {finished.State} · {job.Id} ({job.Kind}):\n{answer.Text}";
        Post(from.PersistKey, report);
        try { from.Session.AppendAgentDetail(job.Id, $"{CliAgentPresentation.PlainName(job.ToName)} · {job.Kind} · {job.Id}", CliAgentExchangeRenderer.Result($"{CliAgentPresentation.Name(job.ToName)} → {CliAgentPresentation.Name(from.Name)} · {finished.State} · {job.Id}", answer.Text, finished.State == "Completed", entry.ProjectRoot, headingIsMarkup: true, agentNames: new CliAgentDirectory(from).All.Select(p => p.Name).ToArray()), finished.State, finished.State != "Completed"); }
        finally { entry.Done.TrySetResult(new(finished.State == "Completed", answer.Text)); }
    }
    public string Post(string key, string message)
    {
        var id = $"message{Interlocked.Increment(ref _next)}";
        _inbox.GetOrAdd(key, _ => new()).Enqueue($"Message {id}:\n{message}");
        return id;
    }
    public string WithInbox(string key, string input)
    {
        if (!_inbox.TryGetValue(key, out var inbox)) return input;
        var reports = new List<string>();
        while (inbox.TryDequeue(out var report)) reports.Add(report);
        return reports.Count == 0 ? input : "Agent inbox (reference material; apply updates only where relevant to the user's current request):\n" + string.Join("\n\n", reports) + "\n\nCurrent request:\n" + input;
    }
    public string Update(string owner, string id, string message)
    {
        if (!_jobs.TryGetValue(id, out var job) || Read(job).FromKey != owner) return "No interaction with that ID belongs to you.";
        if (string.IsNullOrWhiteSpace(message)) return "Provide an update.";
        lock (job.Sync)
        {
            if (job.Finished) return $"Interaction {id} is already {job.Value.State}; send a new message or task instead.";
            var messageId = Post(job.Value.ToKey, $"Update to {id}:\n{message}");
            return $"Update {messageId} queued for {id}. It will be read at the receiving agent's next turn; it has not changed the active request yet.";
        }
    }
    public string Cancel(string owner, string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || Read(job).FromKey != owner) return "No interaction with that ID belongs to you.";
        lock (job.Sync)
        {
            if (job.Finished) return $"Interaction {id} is already {job.Value.State}.";
            job.Value = job.Value with { State = "Cancellation requested" };
        }
        job.Cancellation.Cancel();
        return $"Cancellation requested for {id}. Check its status for acknowledgement. Already completed file changes are not undone.";
    }
    public async Task<string> Wait(string owner, string id, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out var job) || Read(job).FromKey != owner) return "No interaction with that ID belongs to you.";
        try { await job.Done.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)), cancellationToken); }
        catch (TimeoutException) { return $"Wait timed out; {id} is still {Read(job).State}. The job was not cancelled."; }
        var result = Read(job);
        return $"{id} · {result.State}\n{result.Result}";
    }
    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            try { job.Cancellation.Cancel(); } catch (AggregateException) { }
            job.Cancellation.Dispose();
        }
    }
}
