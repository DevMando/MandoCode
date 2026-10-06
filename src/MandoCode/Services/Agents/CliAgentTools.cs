using System.ComponentModel;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Microsoft.Extensions.DependencyInjection;

namespace MandoCode.Services;

public sealed class CliAgentTools(AgentPane self, CliDelegations jobs)
{
    private readonly CliAgentDirectory _directory = new(self);
    public IEnumerable<AIFunction> Functions =>
    [
        AIFunctionFactory.Create(ListAgents, new() { Name = "list_agents" }),
        AIFunctionFactory.Create(GetAgentStatus, new() { Name = "get_agent_status" }),
        AIFunctionFactory.Create(ReadAgentTranscript, new() { Name = "read_agent_transcript" }),
        AIFunctionFactory.Create(AskAgent, new() { Name = "ask_agent_and_wait" }),
        AIFunctionFactory.Create(AskAgentAsync, new() { Name = "ask_agent_async" }),
        AIFunctionFactory.Create(SendAgentMessage, new() { Name = "send_agent_message" }),
        AIFunctionFactory.Create(DelegateToAgent, new() { Name = "delegate_to_agent" }),
        AIFunctionFactory.Create(RequestAgentReview, new() { Name = "request_agent_review" }),
        AIFunctionFactory.Create(HandoffToAgent, new() { Name = "handoff_to_agent" }),
        AIFunctionFactory.Create(UpdateAgentJob, new() { Name = "update_agent_job" }),
        AIFunctionFactory.Create(CancelAgentJob, new() { Name = "cancel_agent_job" }),
        AIFunctionFactory.Create(WaitForAgentJob, new() { Name = "wait_for_agent_job" }),
        AIFunctionFactory.Create(CheckDelegations, new() { Name = "check_delegations" })
    ];
    [Description("Lists other open agents across workspaces, their projects, models, and working/idle status. Agent mentions are participants, not file paths.")]
    public string ListAgents() => string.Join("\n", _directory.All.Where(p => p != self).Select(CliAgentDirectory.Describe)) is { Length: > 0 } list ? list : "No other agents are open.";
    [Description("Reads another open agent's live status without interrupting it or starting work.")]
    public string GetAgentStatus(string name) => _directory.Resolve(name) is { } pane ? CliAgentDirectory.Describe(pane) : Missing(name);
    [Description("Reads recent messages from an open agent, including while busy. This does not run a turn. Defaults to 12 messages, capped at 40.")]
    public async Task<string> ReadAgentTranscript(string name, int turns = 12)
    {
        if (_directory.Resolve(name) is not { } pane) return Missing(name);
        if (pane == self) return "This is your own conversation.";
        var read = pane.ReadConversation;
        if (read is null) return "That agent is still starting or has closed.";
        var messages = await read();
        return messages.Count == 0 ? "No conversation yet. " + CliAgentDirectory.Describe(pane) : string.Join("\n\n", messages.TakeLast(Math.Clamp(turns, 1, 40))
            .Select(m => $"[{m.Role}] {Trim(m.Text, 1500)}"));
    }
    [Description("Asks an idle agent a specific question and WAITS for its reply using that agent's own context and tools. This holds your current turn open: use it only when you need the answer to continue the current request, for example choosing an approach before implementing it. Prefer ask_agent_async for independent questions, request_agent_review for reviews, and delegate_to_agent for research or implementation requested by the user so you can finish the handoff and keep talking to them. Busy agents are not interrupted; inspect their status/transcript instead.")]
    public async Task<string> AskAgent(string name, string question, CancellationToken cancellationToken = default)
    {
        var (pane, error) = Target(name, question);
        if (pane is null) return error!;
        var ask = pane.AskPeer;
        if (ask is null) return "That agent is still starting or has closed.";
        var request = new CliPeerRequest(self.Name, question, CliAgentCallChain.Capture, cancellationToken);
        self.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(pane.Name)} · Asked\n{CliAgentPresentation.PlainName(self.Name)} → {CliAgentPresentation.PlainName(pane.Name)}\n\n{question}", Color.Purple, request.InteractionId, "Waiting for reply");
        CliPeerAnswer answer;
        try { answer = await ask(request); }
        catch (Exception ex)
        {
            self.Session.AppendAgentDetail(request.InteractionId, $"{CliAgentPresentation.PlainName(pane.Name)} · Question", new Text(ex.Message), "Failed", problem: true);
            throw;
        }
        self.Session.AppendAgentDetail(request.InteractionId, $"{CliAgentPresentation.PlainName(pane.Name)} · Question", CliAgentExchangeRenderer.Result($"{CliAgentPresentation.Name(pane.Name)} → {CliAgentPresentation.Name(self.Name)} · {(answer.Answered ? "Reply" : "Could not answer")}", answer.Text, answer.Answered, self.Services.GetRequiredService<ProjectRootAccessor>().ProjectRoot, headingIsMarkup: true), answer.Answered ? "Completed" : "Failed", !answer.Answered);
        return answer.Answered ? $"{pane.Name} replied:\n{answer.Text}" : $"{pane.Name} could not answer: {answer.Text}. Use get_agent_status or read_agent_transcript instead.";
    }
    [Description("DEFAULT for independent work the user requests from another agent: hands research or an implementation task to an idle agent and returns immediately after acceptance, without waiting. Use ask_agent_async for an independent question and request_agent_review for a read-only review. The agent uses its own context, tools, and approval gates. Include the goal, context, constraints, expected output, and clear file ownership if it may edit files. Finish your handoff and continue with the user; completion is reported automatically. Use check_delegations when the user asks for progress, rather than polling. Do not assume success or do dependent work before the result arrives. Use ask_agent_and_wait only when you need its answer to continue the current request. A mention alone is not permission to delegate; busy agents are not interrupted.")]
    public Task<string> DelegateToAgent(string name, string task, CancellationToken cancellationToken = default) => StartBackground(name, task, "task", cancellationToken);
    [Description("Asks an independent question and returns after acceptance WITHOUT waiting for its answer. Prefer this over ask_agent_and_wait unless the answer is needed to continue your current request. Returns an interaction ID; the reply arrives later. A mention alone is not permission to ask.")]
    public Task<string> AskAgentAsync(string name, string question, CancellationToken cancellationToken = default) => StartBackground(name, question, "question", cancellationToken);
    [Description("Requests a review of specified changes and returns after acceptance, without waiting. Supply paths or diff context, review criteria, and expected findings. This is read-only: the receiver must not edit files. The report arrives later with a tracked interaction ID.")]
    public Task<string> RequestAgentReview(string name, string changes, CancellationToken cancellationToken = default) => string.IsNullOrWhiteSpace(changes) ? Task.FromResult("Specify what to review.") : StartBackground(name, "Review only; do not edit files.\n" + changes, "review", cancellationToken);
    [Description("Transfers responsibility for a user-requested task to an idle agent, returning after acceptance without waiting. Include scope, context, constraints, exact file ownership, remaining work, and expected deliverables. After acceptance, stop working on the transferred scope; do not close either conversation. The receiver cannot see your conversation unless you include it.")]
    public Task<string> HandoffToAgent(string name, string task, CancellationToken cancellationToken = default) => string.IsNullOrWhiteSpace(task) ? Task.FromResult("Specify the transferred scope.") : StartBackground(name, "You own this transferred task; work within the following assigned scope:\n" + task, "handoff", cancellationToken);
    [Description("Delivers information to another open agent's visible inbox WITHOUT requesting a reply or starting a model turn. Works while busy. It is queued for the receiver's next turn, not confirmation it has read or acted on it. Returns a message ID. Use ask_agent_async for a later answer or delegate_to_agent for work. When answering an incoming question, return your answer normally; do not also send the same answer as an inbox message.")]
    public string SendAgentMessage(string name, string message)
    {
        var pane = _directory.Resolve(name);
        if (pane is null) return Missing(name);
        if (pane == self) return "That agent is you.";
        if (string.IsNullOrWhiteSpace(message)) return "Provide a message.";
        var id = jobs.Post(pane.PersistKey, $"From agent {self.Name}:\n{message}");
        var exchangeId = self.Session.CurrentAgentExchangeFor(pane.Name) ?? id;
        self.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(pane.Name)} · Message sent · {id}\n{CliAgentPresentation.PlainName(self.Name)} → {CliAgentPresentation.PlainName(pane.Name)}\n\n{message}", Color.Purple, exchangeId, exchangeId == id ? "Delivered" : null);
        pane.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(self.Name)} · Message received · {id}\n{CliAgentPresentation.PlainName(self.Name)} → {CliAgentPresentation.PlainName(pane.Name)}\n\n{message}\nQueued for the next turn; no reply requested.", Color.Purple, exchangeId, exchangeId == id ? "Queued" : null);
        return $"Message {id} delivered to {pane.Name}'s inbox for its next turn. No reply requested.";
    }
    [Description("Queues revised requirements or extra context for one of your background interactions, by ID. Returns immediately. The receiving agent reads it at its NEXT turn; this does not rewrite or interrupt its active request. Cancel and reassign if current work must stop.")]
    public string UpdateAgentJob(string id, string message)
    {
        var result = jobs.Update(self.PersistKey, id, message);
        self.Session.AppendAgentExchange(result, Color.Purple, id);
        var job = jobs.For(self.PersistKey).FirstOrDefault(j => j.Id == id);
        if (result.StartsWith("Update ") && job is not null && _directory.All.FirstOrDefault(p => p.PersistKey == job.ToKey) is { } peer)
            peer.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(self.Name)} · Update to {id}\n{message}\nQueued for the next turn.", Color.Purple, id);
        return result;
    }
    [Description("Requests cancellation of a specific background interaction YOU assigned, by ID. Returns immediately after requesting cancellation; check_delegations reports acknowledgement. Does not cancel unrelated work or undo completed file changes.")]
    public string CancelAgentJob(string id)
    {
        var result = jobs.Cancel(self.PersistKey, id);
        self.Session.AppendAgentExchange(result, Color.Purple, id, result.StartsWith("Cancellation requested") ? "Cancellation requested" : null);
        return result;
    }
    [Description("Explicitly WAITS for a background interaction you assigned when its result is now needed to continue. Timeout defaults to 30 seconds and is capped at 120; timing out or cancelling this wait does not cancel the job. Avoid polling in a loop; results are reported automatically.")]
    public Task<string> WaitForAgentJob(string id, int timeoutSeconds = 30, CancellationToken cancellationToken = default) => jobs.Wait(self.PersistKey, id, timeoutSeconds, cancellationToken);
    private static string BackgroundLabel(string kind) => kind switch { "question" => "Asked", "review" => "Review requested", "handoff" => "Handed off", _ => "Delegated" };
    private async Task<string> StartBackground(string name, string task, string kind, CancellationToken cancellationToken)
    {
        var (pane, error) = Target(name, task);
        if (pane is null) return error!;
        cancellationToken.ThrowIfCancellationRequested();
        var ask = pane.AskPeer;
        if (ask is null) return "That agent is still starting or has closed.";
        var cancellation = new CancellationTokenSource();
        var job = jobs.Open(self, pane, task, kind, "Sent", cancellation);
        var request = new CliPeerRequest(self.Name, task, CliAgentCallChain.Capture, cancellation.Token) { IsDelegation = true, InteractionId = job.Id, Kind = kind };
        self.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(pane.Name)} · {BackgroundLabel(kind)} · {job.Id}\n{CliAgentPresentation.PlainName(self.Name)} → {CliAgentPresentation.PlainName(pane.Name)}\n\n{task}\nThe reply will arrive later; you can keep chatting.", Color.Purple, job.Id, "Sent");
        Task<CliPeerAnswer> pending;
        bool claim;
        try
        {
            pending = ask(request);
            await Task.WhenAny(request.Accepted.Task, pending);
            claim = request.Accepted.Task.IsCompletedSuccessfully && request.Accepted.Task.Result;
            if (!claim && pending.IsFaulted) await pending;
        }
        catch (Exception ex)
        {
            jobs.Complete(job, new(false, ex.Message), self);
            return $"Interaction {job.Id} failed: {ex.Message}";
        }
        if (!claim)
        {
            var refusal = (await pending).Text;
            jobs.Complete(job, new(false, refusal), self, declined: true);
            return $"Interaction {job.Id} declined: {refusal}";
        }
        jobs.Working(job);
        self.Session.AppendAgentExchange($"{CliAgentPresentation.PlainName(pane.Name)} accepted {job.Id}", Color.Purple, job.Id, "Working in background");
        _ = CompleteAsync(job, pending);
        return $"Interaction {job.Id} handed to {pane.Name} ({kind}). Tell the user that the other agent is working and they can keep chatting. Finish this handoff turn; do not say you are waiting or call wait_for_agent_job unless the answer is needed to continue. The result arrives later. check_delegations reports progress.";
    }
    private async Task CompleteAsync(CliDelegation job, Task<CliPeerAnswer> pending)
    {
        CliPeerAnswer answer;
        try { answer = await pending; } catch (Exception ex) { answer = new(false, ex.Message); }
        jobs.Complete(job, answer, self);
    }
    [Description("Reports the status and results of background interactions you assigned. Results also arrive automatically; avoid polling in a loop.")]
    public string CheckDelegations() => string.Join("\n\n", jobs.For(self.PersistKey).Select(job => $"{job.Id} · {job.ToName} · {job.Kind} · {job.State}\n{job.Result ?? (_directory.All.FirstOrDefault(p => p.PersistKey == job.ToKey) is { } peer ? CliAgentDirectory.Describe(peer) : "Agent closed.")}")) is { Length: > 0 } status ? status : "No delegated jobs.";
    private (AgentPane? Pane, string? Error) Target(string name, string task)
    {
        var pane = _directory.Resolve(name);
        if (pane is null) return (null, Missing(name));
        if (pane == self) return (null, "That agent is you. Use your own context.");
        if (string.IsNullOrWhiteSpace(task)) return (null, "Provide a question or task.");
        if (CliAgentCallChain.Reject(pane.PersistKey)) return (null, "Agent call loop or chain limit reached. Read its status/transcript instead.");
        if (pane.AskPeer is null) return (null, "That agent is still starting or has closed.");
        return (pane, null);
    }
    private string Missing(string name) => $"No unique open agent matches '{name}'. Open agents: " + string.Join(", ", _directory.All.Where(p => p != self).Select(p => p.Name));
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + " … [truncated]";
}
