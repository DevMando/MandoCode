using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace MandoCode.Components;

public partial class App
{
    private const string ExecutePlanLabel = "Execute plan";
    private const string EditPlanStepLabel = "Edit a step";
    private const string RejectPlanLabel = "One-shot it";
    private const string CancelRequestLabel = "Cancel request";

    // App-standard selection treatment for the remaining Spectre prompts — same
    // black-on-deepskyblue1 as ApprovalSelect, DiffApprovalHandler, and the command
    // autocomplete, so selection reads the same everywhere.
    private static readonly Style SelectionHighlight = new(foreground: Color.Black, background: Color.DeepSkyBlue1);

    // Plan-approval select bridge — mirrors the wizard TCS pattern above.
    private bool _planSelectActive;
    private TaskCompletionSource<string>? _planSelectTcs;
    private static readonly ApprovalSelect.Option[] ProposedPlanOptions =
    {
        new(ExecutePlanLabel, Color.Green),
        new(EditPlanStepLabel, Color.DeepSkyBlue1),
        new(RejectPlanLabel, new Color(255, 200, 80)),
        new(CancelRequestLabel, new Color(255, 200, 80)),
    };

    /// <summary>
    /// Shows the VDOM plan-approval select and awaits the user's choice. Observes
    /// <paramref name="ct"/> so a cancelled request (Esc, timeout) unwinds the await
    /// instead of leaving the turn stuck at an orphaned prompt.
    /// </summary>
    private IReadOnlyList<ApprovalSelect.Option> _planSelectOptions = ProposedPlanOptions;

    private Task<string> PromptPlanChoiceAsync(CancellationToken ct) =>
        PromptPlanChoiceAsync("The assistant proposes this plan. What would you like to do?", ProposedPlanOptions, ct);

    private async Task<string> PromptPlanChoiceAsync(string title, IReadOnlyList<ApprovalSelect.Option> options, CancellationToken ct)
    {
        _planSelectOptions = options;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _planSelectTcs = tcs;
        using var cancelReg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Title goes to scrollback imperatively (same as WizardPromptSelectAsync) so the
        // VDOM region holds only the option rows — keeps the live-repaint area minimal.
        TuiConsole.MarkupLine($"[deepskyblue1]{Spectre.Console.Markup.Escape(title)}[/]");

        _planSelectActive = true;
        await InvokeAsync(StateHasChanged);

        string choice;
        try
        {
            choice = await tcs.Task;
        }
        finally
        {
            _planSelectActive = false;
            _planSelectTcs = null;
            await InvokeAsync(StateHasChanged);
            // Brief wait so the VDOM tears down the select before imperative writes
            // (matches the wizard primitives' teardown delay).
            await Task.Delay(50);
        }

        // Persist the answer in scrollback (matches the > gold echo pattern in the main loop).
        Console.WriteLine($"\u001b[38;2;255;200;80m> {choice}\u001b[0m");
        return choice;
    }

    private void HandlePlanSelectSubmit(string choice)
    {
        _planSelectTcs?.TrySetResult(choice);
    }

    /// <summary>
    /// One line during <c>--continue</c> startup when this project has an unfinished plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shown only for <c>--continue</c>, because that is this app's existing "pick up where I left
    /// off" gesture. Launching without it means a fresh session, and a plan from a previous one is
    /// noise there. It also keeps the two halves of "where I left off" together: a resumed plan
    /// runs with the conversation it came from rather than without it.
    /// </para>
    /// <para>
    /// It notifies rather than resuming. Resuming writes files, and doing that automatically at
    /// launch would let a plan the user had walked away from start changing their project before
    /// they had read a single line.
    /// </para>
    /// </remarks>
    private void ShowUnfinishedPlanNotice()
    {
        try
        {
            if (!PlanRunners.SupportsResume) return;

            var saved = PlanRunners.FindResumable(out _);
            if (saved == null) return;

            var outstanding = PlanCheckpointStore.OutstandingSteps(saved);
            if (outstanding == 0) return;

            var done = saved.Steps.Count - outstanding;
            TuiConsole.WriteLine();
            TuiConsole.MarkupLine(
                $"[yellow]Unfinished plan:[/] {Spectre.Console.Markup.Escape(saved.Goal)} " +
                $"[dim]({done} of {saved.Steps.Count} steps done)[/]");
            TuiConsole.MarkupLine("[dim]/plan-resume[/] to continue, [dim]/plan-discard[/] to forget it.");
            TuiConsole.WriteLine();
        }
        catch { /* a notice must never stop the session starting */ }
    }

    /// <summary>
    /// `/plan` — inspect, resume or discard the plan recorded for this project.
    /// </summary>
    /// <remarks>
    /// Only the workflow engine records progress, so only it can be resumed; the legacy runner
    /// keeps the plan in a local variable that dies with the process. The command says so rather
    /// than reporting "nothing to resume", which would be indistinguishable from having lost work.
    /// </remarks>
    private async Task HandlePlanCommandAsync(string arg)
    {
        TuiConsole.WriteLine();

        if (!string.IsNullOrWhiteSpace(arg) && arg is not "resume" and not "discard")
        {
            await ForcePlanAsync(arg);
            return;
        }

        if (arg == "discard")
        {
            PlanRunners.DiscardResumable();
            TuiConsole.MarkupLine("[dim]Saved plan discarded.[/]");
            TuiConsole.WriteLine();
            return;
        }

        var saved = PlanRunners.FindResumable(out var refusal);

        if (refusal != null)
        {
            // Explain rather than silently offering nothing — the plan is on disk, it just can't
            // be safely continued as things stand.
            TuiConsole.MarkupLine($"[yellow]{Spectre.Console.Markup.Escape(refusal)}[/]");
            TuiConsole.MarkupLine("[dim]Use[/] /plan-discard [dim]to forget it.[/]");
            TuiConsole.WriteLine();
            return;
        }

        if (saved == null)
        {
            TuiConsole.MarkupLine("[dim]No unfinished plan for this project.[/]");
            TuiConsole.WriteLine();
            return;
        }

        var outstanding = PlanCheckpointStore.OutstandingSteps(saved);
        var done = saved.Steps.Count - outstanding;

        if (arg != "resume")
        {
            TuiConsole.MarkupLine(
                $"[deepskyblue1]Unfinished plan:[/] {Spectre.Console.Markup.Escape(saved.Goal)}");
            TuiConsole.MarkupLine($"[dim]{done} of {saved.Steps.Count} steps done.[/]");
            TuiConsole.WriteLine();
            DisplaySavedPlan(saved);
            TuiConsole.MarkupLine("[dim]/plan-resume[/] to continue, [dim]/plan-discard[/] to forget it.");
            TuiConsole.WriteLine();
            return;
        }

        TuiConsole.MarkupLine(
            $"[green]Resuming:[/] {Spectre.Console.Markup.Escape(saved.Goal)} " +
            $"[dim]({outstanding} step(s) left)[/]");
        TuiConsole.WriteLine();

        // The plan was started in a process that no longer exists, so nothing has told the agent
        // what request it is fulfilling. Steps treat that request as authoritative for WHERE work
        // happens, so without it a resumed plan can write to the wrong place entirely.
        AI.SetRequestContext(saved.Goal);

        // Steps already Completed or Skipped keep that status, so the runner steps over them and
        // only outstanding work executes. ResumeAsync also seeds what earlier steps produced.
        var plan = PlanCheckpointStore.ToPlan(saved);

        _requestCts = new CancellationTokenSource();
        StartCancelKeyListener();
        try
        {
            using var execution = PlanHandoff.BeginResumedExecution(saved.FileOperations);
            var runner = PlanRunners.Current as WorkflowPlanRunner;
            var progress = runner != null
                ? runner.ResumeAsync(plan, saved, _requestCts.Token)
                : PlanRunners.Current.ExecutePlanAsync(plan, _requestCts.Token);

            await foreach (var progressEvent in progress)
            {
                await HandleProgressEventAsync(progressEvent, plan, _requestCts.Token);
            }

            if (PlanHandoff.TakeReplacementInstructions() is { } revisedInstructions && !_requestCts.IsCancellationRequested)
                await ReviewReplacementPlanAsync(plan, revisedInstructions, _requestCts.Token);
            var manifest = PlanHandoff.BuildManifest(plan, PlanHandoff.FileOperations);
            AI.AppendAssistantNote(manifest);
        }
        catch (OperationCanceledException)
        {
            TuiConsole.MarkupLine("[yellow]Resume cancelled.[/]");
        }
        finally
        {
            Spinner.Stop();
            StopCancelKeyListener();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
        }

        TuiConsole.WriteLine();
    }

    private Task ForcePlanAsync(string goal) => ForcePlanAsync(goal, goal);

    private async Task ForcePlanAsync(string planningRequest, string originalRequest)
    {
        _requestCts = new CancellationTokenSource();
        StartCancelKeyListener();
        var rejected = false;
        try
        {
            AI.AppendUserNote(originalRequest);
            Spinner.Start("Creating plan...");
            var proposal = await AI.GeneratePlanAsync(planningRequest, cancellationToken: _requestCts.Token);
            Spinner.Stop();
            _lastPlanOutcome = PlanTurnOutcome.None;
            var manifest = await PlanHandoff.ProcessAsync(
                proposal.Goal, proposal.Steps, _requestCts.Token, originalRequest: originalRequest);
            rejected = _lastPlanOutcome == PlanTurnOutcome.Rejected;
            if (_lastPlanOutcome == PlanTurnOutcome.Executed && !string.IsNullOrWhiteSpace(manifest))
                AI.AppendAssistantNote(manifest);
        }
        catch (OperationCanceledException)
        {
            TuiConsole.MarkupLine("[yellow]Planning cancelled.[/]");
        }
        catch (Exception ex)
        {
            TuiConsole.MarkupLine($"[red]Could not create plan:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
        }
        finally
        {
            Spinner.Stop();
            StopCancelKeyListener();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
        }

        if (rejected)
        {
            await ProcessDirectRequestAsync(
                "Proceed with my original request directly.",
                "The user reviewed the forced plan and chose to skip stepwise execution. " +
                "Answer the request directly now. Do not call propose_plan.");
            PlanHandoff.ClearPendingProposal();
        }
    }

    private static void DisplaySavedPlan(PlanRunState saved)
    {
        var table = new Spectre.Console.Table { Border = TableBorder.Rounded };
        table.AddColumn("Step");
        table.AddColumn("Description");
        table.AddColumn("Status");

        foreach (var step in saved.Steps)
        {
            var status = step.Status switch
            {
                TaskStepStatus.Completed => "[green]done[/]",
                TaskStepStatus.Skipped => "[dim]skipped[/]",
                TaskStepStatus.Failed => "[red]failed[/]",
                _ => "[yellow]pending[/]",
            };
            table.AddRow(
                step.Number.ToString(),
                Spectre.Console.Markup.Escape(step.Description),
                status);
        }

        TuiConsole.Write(table);
        TuiConsole.WriteLine();
    }

    private enum PlanTurnOutcome { None, Executed, Rejected, Cancelled }

    private PlanTurnOutcome _lastPlanOutcome = PlanTurnOutcome.None;

    // Bounds the answer-directly follow-up to a single extra turn. Without it a model that responds
    // to a rejection by proposing another plan could ping-pong indefinitely.
    private int _planFollowUpDepth;

    /// <summary>
    /// Runs a plan the model proposed during the turn that just finished, then records the outcome
    /// in chat history. No-op when no plan was proposed.
    /// </summary>
    /// <remarks>
    /// An executed plan's manifest goes into history as an assistant note rather than being fed back
    /// to the model for a closing turn. That is deliberate: giving the model an open turn after a
    /// plan is exactly what used to make it redo the work, and no amount of "the files already
    /// exist" phrasing reliably stopped it.
    /// <para>
    /// A rejected plan is the one case that does need another turn — the user asked for a direct
    /// answer instead of stepwise execution, and before plans were deferred the model got that for
    /// free by simply continuing its open turn.
    /// </para>
    /// </remarks>
    private async Task RunPendingPlanAsync()
    {
        if (!PlanHandoff.HasPendingProposal) return;

        // Already inside the post-rejection follow-up: the user has just declined a plan, so
        // silently running another one would be the opposite of what they asked for.
        if (_planFollowUpDepth > 0)
        {
            PlanHandoff.ClearPendingProposal();
            return;
        }

        var ct = _requestCts?.Token ?? CancellationToken.None;

        // The user cancelled this turn; running the plan it proposed is the opposite of what they
        // asked for, and leaving it queued would run it at the end of some later, unrelated turn.
        if (ct.IsCancellationRequested)
        {
            PlanHandoff.ClearPendingProposal();
            return;
        }

        _lastPlanOutcome = PlanTurnOutcome.None;
        var manifest = await PlanHandoff.RunPendingPlanAsync(ct);

        if (_lastPlanOutcome == PlanTurnOutcome.Rejected)
        {
            _planFollowUpDepth++;
            try
            {
                await ProcessDirectRequestAsync(
                    "Continue with my original request.",
                    "The user reviewed your proposed plan and chose to skip stepwise execution. "
                  + "Answer their original request directly now. Do not call propose_plan.");
            }
            finally
            {
                _planFollowUpDepth--;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(manifest)) return;

        AI.AppendAssistantNote(manifest);

        // Keep --continue honest: the plan's outcome is part of this turn.
        try { if (Pane is null || Pane.Id == 1) SessionResumeStore.Save(ProjectRoot.ProjectRoot, AI.ExportHistoryJson()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Could not save compacted context: {0}", ex.Message); }
        SaveAgentHistory(false);
    }

    private async Task<string> HandleProposedPlanAsync(TaskPlan plan, CancellationToken ct)
    {
        string choice;
        // Serialize the plan-approval prompt against the diff/command/MCP prompts — the
        // model can emit propose_plan alongside another approval-gated tool call in the
        // same response. Held ONLY around the prompt, never around plan execution: the
        // plan's own step approvals acquire this same gate, so holding it across
        // ExecutePlanAsync would deadlock the first step.
        using (await PromptGate.AcquireAsync(ct))
        {
            // Stop the outer "Thinking..." spinner so it doesn't fight the approval prompt.
            Spinner.Stop();

            // One suppression scope spans the approval menu AND every follow-up step picker/editor.
            // Ending it after the first menu lets the background Escape listener race RazorConsole's
            // keyboard pump and silently steal arrow presses and typed characters.
            using (KeyCoordinator.Suppress())
            {
                // Loops so editing a step returns to the menu: reviewing a plan is iterative, and
                // being forced to approve or reject immediately after one edit defeats the point.
                while (true)
                {
                    TuiConsole.WriteLine();
                    DisplayPlan(plan);

                    // VDOM select instead of a Spectre SelectionPrompt — Spectre's blocking
                    // ReadKey races RazorConsole's keyboard pump and intermittently lost arrow
                    // presses (see ApprovalSelect.razor).
                    choice = await PromptPlanChoiceAsync(ct);

                    if (choice != EditPlanStepLabel) break;

                    await EditPlanStepAsync(plan, ct);
                }
            }
        }

        if (choice == CancelRequestLabel)
        {
            _lastPlanOutcome = PlanTurnOutcome.Cancelled;
            TuiConsole.MarkupLine("[dim]Plan cancelled.[/]");
            TuiConsole.WriteLine();
            // Cancel the token rather than trusting a "stop here" string. This predates deferred
            // execution — observed live, the model received exactly that string and went on to
            // execute the cancelled plan's steps itself via direct tool calls. The model has no
            // open turn to do that in any more, but cancelling is still what unwinds the plan run
            // itself (same path as Esc), so it stays.
            _requestCts?.Cancel();
            return "User cancelled the request. Stop here and do not take further action.";
        }

        if (choice == RejectPlanLabel)
        {
            _lastPlanOutcome = PlanTurnOutcome.Rejected;
            TuiConsole.MarkupLine("[dim]Plan skipped — one-shotting your request.[/]");
            TuiConsole.WriteLine();
            // The caller turns this into a fresh follow-up turn: with execution deferred there is
            // no open turn left for the model to answer in, so one is started explicitly.
            return "User rejected the proposed plan.";
        }

        _lastPlanOutcome = PlanTurnOutcome.Executed;

        TuiConsole.WriteLine();
        TuiConsole.MarkupLine("[green]Executing plan...[/]");
        TuiConsole.WriteLine();

        try
        {
            await foreach (var progressEvent in PlanRunners.Current.ExecutePlanAsync(plan, ct))
            {
                await HandleProgressEventAsync(progressEvent, plan, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Spinner.Stop();
            PlanRunners.Current.CancelPlan(plan);
        }

        if (PlanHandoff.TakeReplacementInstructions() is { } revisedInstructions && !ct.IsCancellationRequested)
            return await ReviewReplacementPlanAsync(plan, revisedInstructions, ct);

        TuiConsole.WriteLine();
        if (plan.Status == TaskPlanStatus.Completed)
        {
            TuiConsole.MarkupLine("[green]Plan completed successfully![/]");
        }
        else if (plan.Status == TaskPlanStatus.CompletedWithIssues)
        {
            TuiConsole.MarkupLine("[yellow]Plan completed with skipped or failed steps.[/]");
        }
        else if (plan.Status == TaskPlanStatus.Cancelled)
        {
            TuiConsole.MarkupLine("[yellow]Plan was cancelled.[/]");
        }
        else if (plan.Status == TaskPlanStatus.Paused)
        {
            TuiConsole.MarkupLine("[yellow]Plan paused; outstanding work remains saved.[/]");
        }
        else
        {
            TuiConsole.MarkupLine("[red]Plan completed with errors.[/]");
        }

        if (!string.IsNullOrEmpty(plan.ExecutionSummary))
        {
            TuiConsole.MarkupLine($"[dim]{Spectre.Console.Markup.Escape(plan.ExecutionSummary)}[/]");
        }
        TuiConsole.WriteLine();

        // The summary alone is pure information — small models treat an open turn as an
        // invitation to keep building (observed: a completed 5-step plan was followed by
        // an uninvited duplicate project AND a third plan proposal). End with an explicit
        // stop directive; the scope-level one-plan-per-turn circuit backs it up mechanically.
        var summary = plan.ExecutionSummary
               ?? $"Plan finished with status {plan.Status}.";
        return summary +
               "\n\nThe plan has finished. Respond to the user now with a brief summary of the outcome. " +
               "Do NOT create more files, call more tools, or propose another plan.";
    }

    private async Task<string> ReviewReplacementPlanAsync(TaskPlan oldPlan, string instructions, CancellationToken ct)
    {
        TuiConsole.MarkupLine("[yellow]The previous plan was stopped because you changed the instructions. Review a replacement before work resumes.[/]");
        var updatedRequest = $"Original request:\n{oldPlan.OriginalRequest}\n\nLatest user instructions (supersede conflicting earlier requirements):\n{instructions}";
        AI.AppendUserNote(instructions);
        AI.SetRequestContext(updatedRequest);
        Spinner.Start("Creating replacement plan...");
        TaskPlan replacement;
        try
        {
            var context = "The previous plan was cancelled. Generate new steps and acceptance criteria for the latest instructions. " +
                "Inspect existing work and retain completed changes that still satisfy the updated request; do not execute tools or repeat work while planning.\n" +
                "Earlier step results:\n" + string.Join("\n", oldPlan.Steps.Select(step => $"{step.Description}: {step.Result ?? step.ErrorMessage ?? step.Status.ToString()}")) +
                "\nObserved file changes:\n" + string.Join("\n", PlanHandoff.FileOperations.Select(op => $"{op.Operation}: {op.Path}"));
            var proposal = await AI.GeneratePlanAsync(updatedRequest, context, ct);
            replacement = new TaskPlan { OriginalRequest = updatedRequest, Steps = TaskPlannerService.FromProposals(proposal.Steps), Status = TaskPlanStatus.Pending };
            if (replacement.Steps.Count == 0) throw new InvalidOperationException("The replacement plan contained no steps; the previous plan remains stopped.");
            PlanFinalQuality.Ensure(replacement);
        }
        finally { Spinner.Stop(); }
        var summary = await HandleProposedPlanAsync(replacement, ct);
        // The outer handoff builds its manifest from the same plan object. Carry the
        // reviewed replacement's outcome back so it never reports the stale criteria.
        oldPlan.OriginalRequest = replacement.OriginalRequest;
        oldPlan.Steps = replacement.Steps;
        oldPlan.Status = replacement.Status;
        oldPlan.ExecutionSummary = replacement.ExecutionSummary;
        return summary;
    }

    /// <summary>
    /// Renders the proposed plan for approval, including each step's instruction.
    /// </summary>
    /// <remarks>
    /// The instruction is shown because it is the text actually sent to the model — the
    /// description is a ≤60-character label for this table and nothing else. Showing only the
    /// label meant approving work you had not read: two steps can look identical in summary and
    /// target completely different files.
    /// </remarks>
    private void DisplayPlan(TaskPlan plan)
    {
        var table = new Spectre.Console.Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Step").Centered())
            .AddColumn(new TableColumn("Description"))
            .AddColumn(new TableColumn("What it will do"));

        foreach (var step in plan.Steps)
        {
            var instruction = step.Instruction ?? "";
            if (step.AcceptanceCriteria.Count > 0) instruction += "\n\n" + PlanAcceptance.Describe(step);
            // Wrapped rather than truncated: a clipped instruction is the same problem as showing
            // none — you cannot approve what you cannot read.
            table.AddRow(
                $"[deepskyblue1]{step.StepNumber}[/]",
                Spectre.Console.Markup.Escape(step.Description),
                $"[dim]{Spectre.Console.Markup.Escape(instruction)}[/]"
            );
        }

        TuiConsole.MarkupLine("[deepskyblue1]Created plan:[/]");
        TuiConsole.WriteLine();
        TuiConsole.Write(table);
        TuiConsole.WriteLine();
    }

    /// <summary>
    /// Lets the user rewrite a step's instruction before the plan runs.
    /// </summary>
    /// <remarks>
    /// Edits the instruction, not the description: the description is a display label, so changing
    /// it would alter what the table says without changing what actually happens — the worst of
    /// both. The step's own text is the thing with consequences.
    /// </remarks>
    private async Task<bool> EditPlanStepAsync(
        TaskPlan plan,
        CancellationToken ct,
        int minimumStepNumber = 1,
        bool reviseFollowingSteps = true)
    {
        // This method owns two consecutive VDOM inputs (row selector, then text editor).
        // Keep the background Escape listener out even if a future caller forgets to suppress it.
        using var keyboardOwnership = KeyCoordinator.Suppress();

        TuiConsole.WriteLine();

        const string goBackLabel = "Go back";
        var selectableSteps = plan.Steps
            .Where(candidate => candidate.StepNumber >= minimumStepNumber)
            .ToArray();
        if (selectableSteps.Length == 0)
        {
            TuiConsole.MarkupLine("[dim]No step selected.[/]");
            TuiConsole.WriteLine();
            return false;
        }

        var stepLabels = selectableSteps
            .Select(candidate => $"Step {candidate.StepNumber} — {candidate.Description}")
            .Append(goBackLabel)
            .ToArray();
        var stepOptions = stepLabels
            .Select((label, index) => new ApprovalSelectCoordinator.Option(
                label,
                index < selectableSteps.Length ? Color.DeepSkyBlue1 : new Color(255, 200, 80)))
            .ToArray();

        // ApprovalSelect keeps a stable row shape while repainting both the highlight and cursor.
        // RazorConsole's generic Select left its '>' marker behind on the first row even while the
        // highlight moved. The custom component was written specifically to avoid that VDOM diff bug.
        TuiConsole.MarkupLine("[deepskyblue1]Select a step to edit (Up/Down, Enter):[/]");
        var selectedLabel = await ApprovalMenu.RequestAsync(stepOptions);

        if (selectedLabel == goBackLabel)
        {
            TuiConsole.MarkupLine("[dim]No step selected.[/]");
            TuiConsole.WriteLine();
            return false;
        }

        var selectedIndex = Array.IndexOf(stepLabels, selectedLabel);
        if (selectedIndex < 0 || selectedIndex >= selectableSteps.Length)
            return false;

        var step = selectableSteps[selectedIndex];

        var revised = await InstructionCoordinator.RequestAsync(
            $"Edit step {step.StepNumber} instruction",
            step.Instruction, multiline: true);

        if (string.IsNullOrWhiteSpace(revised) ||
            string.Equals(revised.Trim(), step.Instruction?.Trim(), StringComparison.Ordinal))
        {
            TuiConsole.MarkupLine("[dim]Left unchanged.[/]");
            TuiConsole.WriteLine();
            return false;
        }

        var trimmed = revised.Trim();
        step.Instruction = trimmed;
        step.AcceptanceCriteria = [trimmed];
        // A stale label makes the review table misleading, so always derive it from the edited text.
        step.Description = trimmed.Length > 60 ? trimmed[..57] + "..." : trimmed;

        TuiConsole.MarkupLine($"[green]Step {step.StepNumber} updated.[/]");
        TuiConsole.WriteLine();

        if (reviseFollowingSteps && plan.Steps.Any(candidate => candidate.StepNumber > step.StepNumber))
            await ReviseFollowingStepsAfterEditAsync(plan, step, ct);

        return true;
    }

    private async Task ReviseFollowingStepsAfterEditAsync(
        TaskPlan plan,
        TaskStep editedStep,
        CancellationToken ct)
    {
        TuiConsole.MarkupLine(
            $"[dim]Updating steps after {editedStep.StepNumber} so they stay consistent with your edit...[/]");
        Spinner.Start("Updating dependent steps...");

        try
        {
            var earlier = plan.Steps
                .Where(step => step.StepNumber < editedStep.StepNumber)
                .Select(step => $"Step {step.StepNumber}: {step.Description}\nInstruction: {step.Instruction}");
            var later = plan.Steps
                .Where(step => step.StepNumber > editedStep.StepNumber)
                .Select(step => $"Step {step.StepNumber}: {step.Description}\nInstruction: {step.Instruction}");
            var context =
                $"The user edited step {editedStep.StepNumber}. Their edited instruction is authoritative and must not be changed:\n" +
                $"{editedStep.Instruction}\n\n" +
                "Earlier steps that must remain unchanged:\n" + string.Join("\n\n", earlier) + "\n\n" +
                "Return only replacement steps that come after the edited step. Update paths, values, and verification " +
                "expectations so they are consistent with the edit. State each replacement as the actual work to execute; " +
                "never say to replace, update, or revise a plan step, and never refer to old step numbers. " +
                "Do not repeat earlier or edited steps.\n\n" +
                "Current later steps to replace:\n" + string.Join("\n\n", later);

            var revision = await AI.GeneratePlanAsync(plan.OriginalRequest, context, ct);
            if (revision.Steps.Length == 1 &&
                revision.Steps[0].description == "Complete the requested goal" &&
                revision.Steps[0].instruction == plan.OriginalRequest.Trim())
                throw new InvalidOperationException("The model did not return a usable dependent-step revision.");

            var candidate = PlanRevision.CreateFollowingCandidate(plan, editedStep.StepNumber, revision);
            PlanRevision.ApplyFollowing(plan, editedStep.StepNumber, candidate);
            TuiConsole.MarkupLine(
                "[green]Dependent steps updated.[/] [dim]Review the complete plan before executing it.[/]");
            TuiConsole.WriteLine();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TuiConsole.MarkupLine(
                $"[yellow]Could not update dependent steps automatically:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
            TuiConsole.MarkupLine("[dim]Review the later steps manually before execution.[/]");
            TuiConsole.WriteLine();
        }
        finally
        {
            Spinner.Stop();
        }
    }

    private async Task HandleProgressEventAsync(
        TaskProgressEvent progressEvent,
        TaskPlan plan,
        CancellationToken ct)
    {
        switch (progressEvent.ProgressType)
        {
            case TaskProgressType.StepActivity:
                Spinner.UpdateActivity(progressEvent.Message ?? "Checking step...");
                break;

            case TaskProgressType.PlanPaused:
                Spinner.Stop();
                TuiConsole.MarkupLine($"[yellow]{Spectre.Console.Markup.Escape(progressEvent.Message ?? "Plan paused.")}[/]");
                TuiConsole.MarkupLine("[dim]Outstanding work is saved. Use /plan-resume to continue.[/]");
                break;

            case TaskProgressType.PersistenceWarning:
                TuiConsole.MarkupLine($"[yellow]{Spectre.Console.Markup.Escape(progressEvent.Message ?? "Plan progress could not be saved.")}[/]");
                break;

            case TaskProgressType.StepStarted:
                Spinner.Stop();
                _recentReadCount = 0;
                _recentReadFiles.Clear();
                _lastOperationType = null;
                SpinnerService.SetTaskbarProgress((progressEvent.CurrentStep - 1) * 100 / progressEvent.TotalSteps);
                TuiConsole.MarkupLine($"[deepskyblue1]Step {progressEvent.CurrentStep}/{progressEvent.TotalSteps}:[/] {Spectre.Console.Markup.Escape(progressEvent.StepDescription)}");
                TuiConsole.WriteLine();
                Spinner.Start("Working...");
                break;

            case TaskProgressType.StepCompleted:
                Spinner.Stop();
                SpinnerService.SetTaskbarProgress(progressEvent.CurrentStep * 100 / progressEvent.TotalSteps);
                if (!string.IsNullOrEmpty(progressEvent.Message))
                {
                    // Render through the same guarded markdown pipeline as non-plan AI
                    // responses so **bold**, lists, inline code, and file-path linkification
                    // match — AND so a malformed/huge step message can't hang the plan loop.
                    // Without the guard, one bad step's render froze the whole multi-step run.
                    RenderMarkdownGuarded(progressEvent.Message);
                }
                TuiConsole.MarkupLine($"[green]Step {progressEvent.CurrentStep} completed.[/]");
                TuiConsole.WriteLine();
                break;

            case TaskProgressType.StepFailed:
                Spinner.Stop();
                SpinnerService.SetTaskbarError(progressEvent.CurrentStep * 100 / progressEvent.TotalSteps);
                TuiConsole.MarkupLine($"[red]Step {progressEvent.CurrentStep} failed:[/] {Spectre.Console.Markup.Escape(progressEvent.Message ?? "Unknown error")}");

                // Cancellation is already the user's terminal decision. Diff/command approval
                // cancellation reports a StepFailed event while also marking the plan cancelled;
                // do not follow that with a contradictory retry/replan/skip prompt.
                if (plan.Status == TaskPlanStatus.Cancelled || ct.IsCancellationRequested)
                {
                    SpinnerService.ClearTaskbarProgress();
                    break;
                }

                // Ask user what to do
                // Palette: green = try again, gold = redirect (skip), red = destructive.
                const string retryStepLabel = "Retry this step";
                const string revisePlanLabel = "Revise the remaining plan";
                const string skipStepLabel = "Skip this step and continue";
                const string cancelPlanLabel = "Cancel the plan";

                var stepToDecide = plan.Steps.FirstOrDefault(s => s.StepNumber == progressEvent.CurrentStep);

                // Retry is offered only on the workflow engine. The legacy runner walks the steps
                // with a foreach and has already moved past this one — it has no way back.
                var choices = new List<ApprovalSelect.Option>();
                if (PlanRunners.UsingWorkflowEngine)
                {
                    choices.Add(new(retryStepLabel, Color.Green));
                    choices.Add(new(revisePlanLabel, Color.DeepSkyBlue1));
                }
                choices.Add(new(skipStepLabel, new Color(255, 200, 80)));
                choices.Add(new(cancelPlanLabel, Color.Red));

                string failChoice;
                using (await PromptGate.AcquireAsync(ct))
                using (KeyCoordinator.Suppress())
                {
                    failChoice = await PromptPlanChoiceAsync("How would you like to proceed?", choices, ct);
                }
                if (failChoice == cancelPlanLabel)
                {
                    PlanRunners.Current.CancelPlan(plan);
                }
                else if (failChoice == retryStepLabel && stepToDecide != null)
                {
                    // Pending is the signal triage reads as "run this one again".
                    stepToDecide.Status = TaskStepStatus.Pending;
                    TuiConsole.MarkupLine($"[dim]Retrying step {progressEvent.CurrentStep}...[/]");
                    TuiConsole.WriteLine();
                }
                else if (failChoice == revisePlanLabel && stepToDecide != null)
                {
                    var decision = await ReplanAfterFailureAsync(
                        plan,
                        stepToDecide,
                        progressEvent.Message ?? "Unknown error",
                        ct);
                    if (decision == CliReplanDecision.Cancel)
                        PlanRunners.Current.CancelPlan(plan);
                    else if (decision == CliReplanDecision.KeepCurrent)
                        PlanRunners.Current.SkipStep(plan, stepToDecide);
                }
                else if (stepToDecide != null)
                {
                    PlanRunners.Current.SkipStep(plan, stepToDecide);
                    SpinnerService.SetTaskbarWarning(progressEvent.CurrentStep * 100 / progressEvent.TotalSteps);
                }
                break;

            case TaskProgressType.PlanCompleted:
                Spinner.Stop();
                SpinnerService.ClearTaskbarProgress();
                break;

            case TaskProgressType.PlanCancelled:
                Spinner.Stop();
                SpinnerService.ClearTaskbarProgress();
                break;
        }
    }

    private enum CliReplanDecision { Applied, KeepCurrent, Cancel }

    private async Task<CliReplanDecision> ReplanAfterFailureAsync(
        TaskPlan plan,
        TaskStep failedStep,
        string error,
        CancellationToken ct)
    {
        Spinner.Start("Revising plan...");
        TuiConsole.MarkupLine("[dim]Revising the unfinished portion of the plan...[/]");

        TaskPlan candidate;
        try
        {
            var settled = plan.Steps
                .Where(step => step.StepNumber < failedStep.StepNumber)
                .Select(step => $"Step {step.StepNumber} [{step.Status}]: {step.Description}\nResult: {step.Result ?? "(none)"}");
            var remaining = plan.Steps
                .Where(step => step.StepNumber >= failedStep.StepNumber)
                .Select(step => $"Step {step.StepNumber}: {step.Description}\nInstruction: {step.Instruction}");
            var context =
                $"Failed step {failedStep.StepNumber}: {failedStep.Description}\n" +
                $"Failure: {error}\n\n" +
                "Settled earlier steps:\n" + string.Join("\n\n", settled) + "\n\n" +
                "Return only replacements for the failed and later steps. State each replacement as the actual work to " +
                "execute; never say to replace, update, or revise a plan step, and never refer to old step numbers. " +
                "Do not repeat settled steps.\n\n" +
                "Current failed and remaining steps to replace:\n" + string.Join("\n\n", remaining);

            var revision = await AI.GeneratePlanAsync(plan.OriginalRequest, context, ct);
            if (revision.Steps.Length == 1 &&
                revision.Steps[0].description == "Complete the requested goal" &&
                revision.Steps[0].instruction == plan.OriginalRequest.Trim())
                throw new InvalidOperationException("The model did not return a usable revision.");

            candidate = PlanRevision.CreateCandidate(plan, failedStep.StepNumber, revision);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TuiConsole.MarkupLine($"[red]Could not revise the plan:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
            return CliReplanDecision.KeepCurrent;
        }
        finally
        {
            Spinner.Stop();
        }

        const string useLabel = "Use revised plan";
        const string editLabel = "Edit a step";
        const string keepLabel = "Keep current plan and skip this step";
        const string cancelLabel = "Cancel the plan";
        string choice;

        // Keep the background Escape listener suppressed across the revised-plan menu and
        // its nested row selector/text editor. Releasing ownership between those VDOM inputs
        // makes the listener consume their keys before RazorConsole sees them.
        using (KeyCoordinator.Suppress())
        {
            while (true)
            {
                TuiConsole.WriteLine();
                DisplayPlan(candidate);
                TuiConsole.MarkupLine(
                    $"[dim]Steps before {failedStep.StepNumber} are settled and will not run again.[/]");

                using (await PromptGate.AcquireAsync(ct))
                {
                    choice = await PromptPlanChoiceAsync("Review the revised remaining plan",
                        new ApprovalSelect.Option[]
                        {
                            new(useLabel, Color.Green),
                            new(editLabel, Color.DeepSkyBlue1),
                            new(keepLabel, new Color(255, 200, 80)),
                            new(cancelLabel, Color.Red)
                        }, ct);
                }
                if (choice == editLabel)
                {
                    await EditPlanStepAsync(
                        candidate,
                        ct,
                        failedStep.StepNumber,
                        reviseFollowingSteps: false);
                    continue;
                }
                if (choice == cancelLabel) return CliReplanDecision.Cancel;
                if (choice == keepLabel) return CliReplanDecision.KeepCurrent;

                PlanRevision.ApplyApproved(plan, failedStep.StepNumber, candidate);
                TuiConsole.MarkupLine(
                    $"[green]Revised plan approved[/] [dim]— resuming at step {failedStep.StepNumber} of {plan.Steps.Count}.[/]");
                TuiConsole.WriteLine();
                return CliReplanDecision.Applied;
            }
        }
    }


    /// <summary>
    /// Shows a fuzzy-searchable picker of installed skills and returns the selected skill
    /// name (or null if the user cancels or no skills are installed). Uses Spectre's
    /// SelectionPrompt with EnableSearch so typing filters the list.
    /// </summary>
}
