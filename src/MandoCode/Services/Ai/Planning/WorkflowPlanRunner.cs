using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MandoCode.Models;
using Microsoft.Agents.AI.Workflows;

namespace MandoCode.Services;

/// <summary>
/// Executes an approved plan as a Microsoft Agent Framework workflow.
/// </summary>
/// <remarks>
/// <para>
/// The application's plan runner. It emits the standard <see cref="TaskProgressEvent"/> stream,
/// so neither front-end needs workflow-specific handling.
/// </para>
/// <para>
/// Topology is fixed — intake, step runner, triage, finalizer — regardless of how many steps the
/// plan has. The cursor lives in the workflow's shared state and in the messages, never in the
/// graph's shape: resume requires byte-identical topology, and a node-per-step layout would differ
/// between a 3-step and a 12-step plan.
/// </para>
/// </remarks>
public sealed class WorkflowPlanRunner(
    IPlanStepExecutor stepExecutor,
    PlanHandoff? planHandoff = null,
    Action<PlanRunState>? onStateSaved = null) : IPlanRunner
{
    private readonly IPlanStepExecutor _stepExecutor = stepExecutor
        ?? throw new ArgumentNullException(nameof(stepExecutor));

    // Optional: supplies the file-operation evidence recorded at the middleware choke point, which
    // is part of what a resumed run needs in order to know what already happened on disk.
    private readonly PlanHandoff? _planHandoff = planHandoff;

    // Invoked whenever the run advances, so the host can record progress for resume.
    private readonly Action<PlanRunState>? _onStateSaved = onStateSaved;

    /// <summary>
    /// One progress event, plus an optional handshake the producer waits on.
    /// </summary>
    /// <remarks>
    /// The handshake is what preserves the existing consumer contract: an interactive consumer
    /// handles a failed step by mutating <c>plan.Status</c>, and that decision is only visible once
    /// it comes back for the next event. Completing <see cref="Ack"/> after the <c>yield return</c>
    /// resumes gives the workflow exactly that ordering.
    /// </remarks>
    private sealed record Signal(TaskProgressEvent Event, TaskCompletionSource? Ack);

    public IAsyncEnumerable<TaskProgressEvent> ExecutePlanAsync(
        TaskPlan plan,
        CancellationToken cancellationToken = default)
        => RunAsync(plan, seedResults: null, cancellationToken);

    /// <summary>
    /// Continues a plan recorded before an interruption.
    /// </summary>
    /// <remarks>
    /// Seeds the results earlier steps produced. A resumed run that skipped this would execute its
    /// remaining steps blind to everything already built — the context those steps usually depend
    /// on — even though the record holds it.
    /// </remarks>
    public IAsyncEnumerable<TaskProgressEvent> ResumeAsync(
        PlanRunState state,
        CancellationToken cancellationToken = default)
        => ResumeAsync(PlanCheckpointStore.ToPlan(state), state, cancellationToken);

    /// <summary>
    /// Continues a saved run using the same reconstructed plan instance the interactive host owns.
    /// Failure decisions mutate that instance during progress-event handshakes, so constructing a
    /// second plan inside the runner would make retry/cancel choices invisible to workflow triage.
    /// </summary>
    public IAsyncEnumerable<TaskProgressEvent> ResumeAsync(
        TaskPlan plan,
        PlanRunState state,
        CancellationToken cancellationToken = default)
        => RunAsync(plan, state.PreviousResults, cancellationToken);

    private async IAsyncEnumerable<TaskProgressEvent> RunAsync(
        TaskPlan plan,
        IReadOnlyList<string>? seedResults,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = lifetime.Token;
        var drained = false;
        var channel = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        async Task RaiseAsync(TaskProgressEvent evt, bool waitForConsumer, CancellationToken ct)
        {
            var ack = waitForConsumer ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            await channel.Writer.WriteAsync(new Signal(evt, ack), CancellationToken.None);

            if (ack == null) return;

            // Never let a consumer that stops enumerating wedge the workflow forever.
            using var reg = ct.Register(() => ack.TrySetResult());
            await ack.Task;
        }

        var ctx = new PlanRunContext(
            plan, _stepExecutor, RaiseAsync, runToken, _planHandoff, _onStateSaved, seedResults);
        void RecordMutation()
        {
            var cursor = ctx.NextRunnableIndex(0);
            ctx.RecordState(PlanRunState.From(plan, cursor < 0 ? plan.Steps.Count : cursor,
                ctx.PreviousResults, _planHandoff?.FileOperations ?? []));
        }
        if (_planHandoff != null) _planHandoff.FileOperationRecorded += RecordMutation;
        var workflow = BuildWorkflow(ctx);

        var pump = Task.Run(async () =>
        {
            try
            {
                // Named: the third positional parameter is sessionId, not the token.
                await using var run = await InProcessExecution.RunStreamingAsync(
                    workflow, new StartPlanRun(), cancellationToken: runToken);

                await foreach (var evt in run.WatchStreamAsync(runToken))
                {
                    if (evt is WorkflowErrorEvent error)
                        throw new InvalidOperationException($"Plan workflow failed: {error}");
                    if (evt is WorkflowOutputEvent) break;
                }

                // WatchStreamAsync can return before the run has actually quiesced — verified in the
                // Phase 0 spike, where a tool call landed after the stream had ended. Declaring the
                // plan finished here would report success while its steps were still writing files.
                while (await run.GetStatusAsync(runToken) == RunStatus.Running)
                {
                    await Task.Delay(25, runToken);
                }
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                plan.Status = TaskPlanStatus.Cancelled;
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return signal.Event;

                // Resumed: the consumer has handled the event and asked for the next one, so any
                // status it set is now visible to triage.
                signal.Ack?.TrySetResult();
            }
            drained = true;
        }
        finally
        {
            // A break or an exception in the consumer skips the post-yield acknowledgement.
            // Cancel BEFORE awaiting the producer, releasing every outstanding handshake.
            if (!drained)
            {
                plan.Status = TaskPlanStatus.Cancelled;
                lifetime.Cancel();
            }
            try { await pump; }
            finally
            {
                if (_planHandoff != null) _planHandoff.FileOperationRecorded -= RecordMutation;
            }
        }
    }

    /// <summary>
    /// Builds the graph. Kept here rather than in a factory type because the executors close over
    /// per-run state and the graph is therefore built per run, not shared.
    /// </summary>
    /// <remarks>Internal rather than private so the topology test can assert on the built graph.</remarks>
    internal static Workflow BuildWorkflow(PlanRunContext ctx)
    {
        var intake = new PlanIntakeExecutor(ctx);
        var stepRunner = new PlanStepRunnerExecutor(ctx);
        var triage = new PlanTriageExecutor(ctx);
        var finalizer = new PlanFinalizerExecutor(ctx);

        // Plain edges: every message is typed and addressed to a specific executor id, so routing is
        // already unambiguous. (The conditional AddEdge<T> overloads are mutually ambiguous to C#
        // overload resolution anyway.)
        return new WorkflowBuilder(intake)
            .AddEdge(intake, stepRunner)
            .AddEdge(intake, finalizer)      // empty plan
            .AddEdge(stepRunner, triage)
            .AddEdge(triage, stepRunner)     // loop back for the next step
            .AddEdge(triage, finalizer)
            .WithOutputFrom(finalizer)
            .Build();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kept for contract parity with <see cref="TaskPlannerService"/>. Triage reads step status when
    /// it reconciles a failure, so setting it here is all that is required.
    /// </remarks>
    public void SkipStep(TaskPlan plan, TaskStep step) => step.Status = TaskStepStatus.Skipped;

    /// <inheritdoc />
    public void CancelPlan(TaskPlan plan) => plan.Status = TaskPlanStatus.Cancelled;
}
