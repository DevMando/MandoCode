using System.Text;
using System.Text.RegularExpressions;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Protocol;
using MandoCode.Models;
using MandoCode.Plugins;
using Spectre.Console.Rendering;

namespace MandoCode.Services.Acp;

/// <summary>Where a turn's output goes. The pane implements it over its transcript; tests record the calls.</summary>
public interface IAcpTurnSink
{
    /// <summary>The unfinished reply (its tail), shown live; null clears it.</summary>
    void Preview(string? tail);

    /// <summary>A dim activity line (what the agent is doing right now).</summary>
    void Activity(string label);

    /// <summary>A finished block of the reply, to be rendered as markdown.</summary>
    void Reply(string markdown);

    /// <summary>A rendered block for the transcript: a tool call line, a diff, the plan.</summary>
    void Block(IRenderable block);

    void Notice(string text);

    void Error(string text);
}

/// <param name="StopReason">The agent's stop reason; null when the turn failed.</param>
/// <param name="Error">What was reported to the user for a failed turn; null otherwise.</param>
/// <param name="RuntimeClosed">The agent process is gone (crashed, or shut down after ignoring a cancel).</param>
public sealed record AcpTurnOutcome(string? StopReason, string? Error, string ReplyText, bool RuntimeClosed)
{
    public bool Cancelled => StopReason == StopReasons.Cancelled;
}

/// <summary>
/// Runs one turn against an <see cref="IAcpAgentRuntime"/> and renders it through an <see cref="IAcpTurnSink"/>:
/// streamed text as a live preview, markdown at boundaries, thoughts as a dim activity line, unknown updates ignored,
/// cancellation (and an agent that ignores it) and failures reported with the messages from <see cref="AcpErrorMessages"/>.
/// </summary>
public sealed class AcpTurnDriver
{
    /// <summary>Only the tail of a long reply is shown live; copying all of it per chunk would be quadratic.</summary>
    public const int PreviewTailChars = 4000;

    private readonly IAcpTurnSink _sink;
    private readonly AcpAgentConfig? _config;
    private readonly AcpTranscriptAdapter? _transcript;

    /// <param name="transcript">Renders tool calls, plans and diffs; without it they only show up as an activity line.</param>
    public AcpTurnDriver(IAcpTurnSink sink, AcpAgentConfig? config = null, AcpTranscriptAdapter? transcript = null)
    {
        _sink = sink;
        _config = config;
        _transcript = transcript;
    }

    public async Task<AcpTurnOutcome> RunAsync(IAcpAgentRuntime runtime, IReadOnlyList<ContentBlock> prompt, CancellationToken cancellation)
    {
        if (runtime.Status != AcpRuntimeStatus.Ready)
        {
            var message = $"The agent is not running. {AcpErrorMessages.RestartHint}";
            _sink.Error(message);
            return new AcpTurnOutcome(null, message, "", RuntimeClosed: true);
        }

        IAcpTurn turn;
        try
        {
            turn = runtime.Prompt(prompt);
        }
        catch (InvalidOperationException ex)
        {
            _sink.Error(ex.Message);
            return new AcpTurnOutcome(null, ex.Message, "", RuntimeClosed: false);
        }

        _transcript?.BeginTurn();
        var reply = new StringBuilder();
        var pending = new StringBuilder();
        string? activity = null;

        void SetActivity(string label)
        {
            if (activity == label)
                return;
            activity = label;
            _sink.Activity(label);
        }

        void FlushPlan()
        {
            if (_transcript?.TakePlan() is { } plan)
                _sink.Block(plan);
        }

        void Settle()
        {
            var text = pending.ToString().Trim();
            pending.Clear();
            _sink.Preview(null);
            if (text.Length == 0)
                return;
            reply.Append(text).Append('\n');
            _sink.Reply(text);
        }

        async Task ConsumeAsync()
        {
            await foreach (var update in turn.Updates.ConfigureAwait(false))
            {
                switch (update)
                {
                    case AgentMessageChunk { Content: TextContent text }:
                        SetActivity("Responding…");
                        pending.Append(text.Text);
                        var start = Math.Max(0, pending.Length - PreviewTailChars);
                        _sink.Preview(pending.ToString(start, pending.Length - start));
                        break;

                    case AgentThoughtChunk:
                        // The thought itself is never shown, only that the agent is thinking.
                        SetActivity("Thinking…");
                        break;

                    case ToolCallBase call:
                        // A tool call ends the text block before it.
                        if (call is ToolCall)
                        {
                            Settle();
                            SetActivity(string.IsNullOrWhiteSpace(call.Title) ? "Working…" : call.Title!);
                        }

                        if (_transcript is not null)
                        {
                            var blocks = _transcript.Apply(call);
                            if (blocks.Count > 0 && call is not ToolCall)
                                Settle();
                            foreach (var block in blocks)
                                _sink.Block(block);
                        }

                        break;

                    case Plan plan when _transcript is not null:
                        _transcript.SetPlan(plan);
                        if (_transcript.PlanSummary() is { } summary)
                            SetActivity(summary);
                        break;

                    // Plans, mode and command announcements, user echoes and anything newer: not shown here.
                }
            }
        }

        // A cancel must not depend on the agent being well behaved: if it ignores session/cancel the
        // updates never end, so the consumer is raced against the "unresponsive" signal.
        var unresponsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? cancelRequest = null;
        using var registration = cancellation.Register(() =>
        {
            SetActivity("Cancelling…");
            var request = runtime.CancelAsync();
            Volatile.Write(ref cancelRequest, request);
            _ = request.ContinueWith(t =>
            {
                if (t.IsFaulted || (t.IsCompletedSuccessfully && !t.Result))
                    unresponsive.TrySetResult();
            }, TaskScheduler.Default);
        });

        var consume = ConsumeAsync();
        var runtimeClosed = false;
        string? stopReason = null;
        string? error = null;

        try
        {
            if (await Task.WhenAny(consume, unresponsive.Task).ConfigureAwait(false) == unresponsive.Task)
            {
                // Closing the runtime kills the agent's process tree; that also ends the update stream.
                await runtime.DisposeAsync().ConfigureAwait(false);
                runtimeClosed = true;
                try { await consume.ConfigureAwait(false); } catch (Exception) { /* closed on purpose */ }
                Settle();
                error = "The agent did not stop within the grace period after the cancel, so it was shut down. " + AcpErrorMessages.RestartHint;
                _sink.Error(error);
                return new AcpTurnOutcome(null, error, reply.ToString().Trim(), runtimeClosed);
            }

            await consume.ConfigureAwait(false);
            stopReason = await turn.Completion.ConfigureAwait(false);
            Settle();
            FlushPlan();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Settle();
            FlushPlan();
            error = AcpErrorMessages.ForTurn(ex, _config);
            runtimeClosed = ex is AcpConnectionClosedException or AcpProtocolViolationException;
            _sink.Error(error);
            return new AcpTurnOutcome(null, error, reply.ToString().Trim(), runtimeClosed);
        }

        if (cancelRequest is { } request && !request.IsCompleted)
            await Task.WhenAny(request, Task.Delay(100)).ConfigureAwait(false);

        if (AcpErrorMessages.ForStopReason(stopReason) is { } note)
            _sink.Notice(note);

        return new AcpTurnOutcome(stopReason, null, reply.ToString().Trim(), runtimeClosed);
    }
}

/// <summary>
/// Builds the prompt blocks for a user message: the text itself plus a <c>resource_link</c> for every <c>@file</c> inside
/// the project. Mentions are read with the same parser as the native pane (<see cref="FileReferenceToken"/>, so
/// <c>@"src/My Component.cs"</c> works) and judged by the same guard as the agent's own file requests: relative paths
/// only inside the project folder, no <c>..</c> escapes, no link or junction that leads out.
/// </summary>
public static class AcpPromptBuilder
{
    public static IReadOnlyList<ContentBlock> Build(string input, string projectRoot)
    {
        var blocks = new List<ContentBlock> { new TextContent(input) };
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var guard = new AcpFileSystemHandler(projectRoot);

        foreach (var mention in FileReferenceToken.Paths(input))
        {
            foreach (var candidate in Candidates(mention))
            {
                if (TryResolve(guard, projectRoot, candidate) is not { } link || !seen.Add(link.Canonical))
                    continue;

                blocks.Add(new ResourceLink { Name = Path.GetFileName(link.Full), Uri = new Uri(link.Full).AbsoluteUri });
                break;
            }
        }

        return blocks;
    }

    /// <summary>The mention as written, then without trailing dots ("see @a.cs." ends a sentence, not the name).</summary>
    private static IEnumerable<string> Candidates(string mention)
    {
        yield return mention;
        var trimmed = mention.TrimEnd('.');
        if (trimmed.Length > 0 && trimmed != mention)
            yield return trimmed;
    }

    private static (string Full, string Canonical)? TryResolve(AcpFileSystemHandler guard, string projectRoot, string mention)
    {
        string full;
        try
        {
            full = Path.GetFullPath(mention, projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;   // not a path at all
        }

        try
        {
            var canonical = guard.Guard(full);   // throws for outside the project, a link that leads out, a directory root
            return File.Exists(canonical) ? (full, canonical) : null;
        }
        catch (JsonRpcException)
        {
            return null;
        }
    }
}
