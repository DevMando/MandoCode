using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
[Trait("Category", "Component")]
public sealed class CliAgentInteractionTests
{
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        Program.RegisterAgentServices(services, new MandoCodeConfig { UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InboxMessageToBusyAgentDoesNotStartATurn_AndIsConsumedOnce()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        peer.IsBusy = () => true;
        peer.AskPeer = _ => throw new InvalidOperationException("Must not start a turn");
        using var jobs = new CliDelegations();
        var tools = new CliAgentTools(self, jobs);
        Assert.Contains("No reply requested", tools.SendAgentMessage(peer.Name, "Here are the findings"));
        Assert.Contains("Here are the findings", jobs.WithInbox(peer.PersistKey, "Next request"));
        Assert.Equal("Next request", jobs.WithInbox(peer.PersistKey, "Next request"));
        var entries = peer.Session.Snapshot().Entries;
        var exchange = Assert.Single(entries, e => e.AgentActivity is not null).AgentActivity!;
        Assert.False(exchange.Expanded);
        Assert.Same(exchange, Assert.Single(entries, e => e.AgentOutput is not null).AgentOutput);
        Assert.Empty(jobs.For(self.PersistKey));
    }

    [Theory]
    [InlineData("question")]
    [InlineData("review")]
    [InlineData("handoff")]
    public async Task BackgroundModesReturnBeforeAnswer_AndTrackTheRightRequest(string kind)
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        CliPeerRequest? received = null;
        var done = new TaskCompletionSource<CliPeerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AskPeer = request => { received = request; request.Accepted.TrySetResult(true); return done.Task; };
        using var jobs = new CliDelegations(); var tools = new CliAgentTools(self, jobs);
        var sent = kind switch
        {
            "question" => tools.AskAgentAsync(peer.Name, "Find the answer"),
            "review" => tools.RequestAgentReview(peer.Name, "Review Program.cs"),
            _ => tools.HandoffToAgent(peer.Name, "Own the docs directory")
        };
        Assert.Contains("result arrives later", await sent.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(done.Task.IsCompleted);
        var job = Assert.Single(jobs.For(self.PersistKey));
        Assert.StartsWith("job", job.Id);
        Assert.Equal(kind, job.Kind);
        Assert.Equal(job.Id, received!.InteractionId);
        Assert.Equal(kind, received.Kind);
        done.SetResult(new(true, "Answer"));
        Assert.Contains("Completed", await tools.WaitForAgentJob(job.Id));
    }

    [Fact]
    public async Task MessageBackToRequesterSharesSectionWithoutChangingWaitingStatus()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var caller = workspace.Add(); var receiver = workspace.Add();
        using var jobs = new CliDelegations();
        caller.Session.AppendAgentExchange("Question\nRequest", Color.Purple, "q1", "Waiting for reply");
        receiver.Session.AppendAgentExchange("Incoming question\nRequest", Color.Purple, "q1", "Working");
        using (receiver.Session.CaptureAgentExchange("q1", caller.Name))
            new CliAgentTools(receiver, jobs).SendAgentMessage(caller.Name, "Additional findings");
        foreach (var pane in new[] { caller, receiver })
        {
            var entries = pane.Session.Snapshot().Entries;
            var group = Assert.Single(entries, e => e.AgentActivity is not null).AgentActivity!;
            Assert.All(entries.Skip(1), entry => Assert.Same(group, entry.AgentOutput));
        }
        Assert.Equal("Waiting for reply", caller.Session.Snapshot().Entries[0].AgentActivity!.Status);
        Assert.Contains("Additional findings", jobs.WithInbox(caller.PersistKey, "Next request"));
    }

    [Fact]
    public async Task UpdatesAndCancellationAreOwnedAndScopedToOneJob()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add(); var outsider = workspace.Add();
        var requests = new List<CliPeerRequest>();
        peer.AskPeer = request =>
        {
            requests.Add(request);
            request.Accepted.TrySetResult(true);
            request.Cancellation.Register(() => request.Completion.TrySetResult(new(false, "Cancelled")));
            return request.Completion.Task;
        };
        using var jobs = new CliDelegations(); var tools = new CliAgentTools(self, jobs);
        await tools.DelegateToAgent(peer.Name, "Task one");
        await tools.AskAgentAsync(peer.Name, "Question two");
        var id = requests[0].InteractionId!;
        Assert.Contains("next turn", tools.UpdateAgentJob(id, "Use the new requirements"));
        Assert.Contains("new requirements", jobs.WithInbox(peer.PersistKey, "Next"));
        var otherTools = new CliAgentTools(outsider, jobs);
        Assert.Contains("belongs to you", otherTools.CancelAgentJob(id));
        Assert.Contains("belongs to you", otherTools.UpdateAgentJob(id, "Bad update"));
        Assert.False(requests[0].Cancellation.IsCancellationRequested);
        Assert.Contains("Cancellation requested", tools.CancelAgentJob(id));
        Assert.Contains("Cancelled", await tools.WaitForAgentJob(id));
        Assert.False(requests[1].Cancellation.IsCancellationRequested);
        requests[1].Completion.SetResult(new(true, "Other job completed"));
        Assert.Contains("Completed", await tools.WaitForAgentJob(requests[1].InteractionId!));
    }

    [Fact]
    public async Task CancellingAWaitDoesNotCancelTheJob_AndReceiverFailureDoesNotHangAcceptance()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        CliPeerRequest? received = null;
        peer.AskPeer = request => { received = request; request.Accepted.SetResult(true); return request.Completion.Task; };
        using var jobs = new CliDelegations(); var tools = new CliAgentTools(self, jobs);
        await tools.DelegateToAgent(peer.Name, "Background work");
        using var waitCancellation = new CancellationTokenSource(); waitCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.WaitForAgentJob(received!.InteractionId!, cancellationToken: waitCancellation.Token));
        Assert.False(received!.Cancellation.IsCancellationRequested);
        received.Completion.SetResult(new(true, "Done"));
        await tools.WaitForAgentJob(received.InteractionId!);
        peer.AskPeer = _ => Task.FromException<CliPeerAnswer>(new InvalidOperationException("Receiver closed"));
        Assert.Contains("failed", await tools.AskAgentAsync(peer.Name, "Question").WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    [InlineData("delegate_to_agent")]
    [InlineData("propose_plan")]
    public async Task ReadOnlyReviewBlocksSideEffectsAtTheActualMiddleware(string name)
    {
        var executed = false;
        var function = AIFunctionFactory.Create(() => { executed = true; return "ran"; }, name);
        var middleware = new AgentFunctionMiddleware(0);
        using (CliAgentCallChain.Enter("reviewer", review: true))
        {
            var refusal = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, function);
            Assert.Contains("read-only", refusal!.ToString());
            Assert.False(executed);
        }
        Assert.False(CliAgentCallChain.IsReview);
    }

    [Fact]
    public void AgentMentionsAreStyled_WithoutChangingFilesEmailsOrCode()
    {
        var inline = CliAgentPresentation.Inline("Ask @Ares about @./Ares and mail@Ares", ["Ares"]);
        Assert.Contains(CliAgentPresentation.Name("Ares"), inline);
        Assert.Contains("@./Ares", inline);
        Assert.Contains("mail@Ares", inline);
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = 120;
        console.Write(MarkdownHtmlRenderer.BuildRenderable("Ask @Ares and @\"Agent Two\".\n\n`@Ares` and @Unknown", agentNames: ["Ares", "Agent Two"]));
        var rendered = writer.ToString();
        Assert.Contains(CliAgentPresentation.PlainName("Ares"), rendered);
        Assert.Contains(CliAgentPresentation.PlainName("Agent Two"), rendered);
        Assert.Contains("@Ares", rendered);
        Assert.Contains("@Unknown", rendered);
    }
}
