using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;

namespace MandoCode.Components;

public partial class App
{
    private readonly CallbackRegistrations _paneCallbacks = new();
    private void StopPane()
    {
        _paneStopped = true;
        _requestCts?.Cancel();
        _snapshotCompletion?.TrySetResult();
        _setupCompletion?.TrySetResult(new(false, true, null));
        _historyTcs?.TrySetResult();
        _integrationsCompletion?.TrySetResult();
        _ollamaPullCompletion?.TrySetResult();
        _directorySelection?.TrySetResult(null);
        _modelPickerTcs?.TrySetResult(null);
        _wizardInputTcs?.TrySetResult(string.Empty);
        _wizardSelectTcs?.TrySetResult(string.Empty);
        _peerRequest?.Completion.TrySetResult(new(false, "Agent closed."));
        _runningPeerRequest?.Completion.TrySetResult(new(false, "Agent closed."));
        if (Pane?.AskPeer == AskPeerAsync)
        {
            // Stop accepting new peer work immediately; disposal releases the other
            // callbacks only when they are still owned by this component.
            Pane.AskPeer = null;
        }

        _inputTcs?.TrySetResult(string.Empty);
        _planSelectTcs?.TrySetResult(CancelRequestLabel);
    }

    protected override void OnInitialized()
    {
        ResolveAgentServices();
        RegisterPaneCallbacks();
        using var output = Pane is null ? null : TuiConsole.Enter(Pane.Session);
        // Get project root from command line args if available. Flags (--continue/-c, and
        // anything future starting with '-') are not folder names — same filtering as
        // Program.cs, or `mandocode --continue` would set the project root to "--continue".
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _continueRequested = (Pane is null || Pane.Id == 1) && args.Any(a => a is "--continue" or "-c");
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        if (Pane?.RestoreFrom is null && positional.Length > 0 && !positional[0].StartsWith("config"))
        {
            ProjectRoot.ProjectRoot = positional[0];
        }

        // Subscribe to function invocation events for UI feedback.
        // Store delegates so remounting an agent component can detach its subscriptions
        // without retaining closures that capture request state.
        _onFunctionInvokedHandler = OnFunctionInvoked;
        _onFunctionCompletedHandler = OnFunctionCompleted;
        AI.OnFunctionInvoked += _onFunctionInvokedHandler;
        AI.OnFunctionCompleted += _onFunctionCompletedHandler;
        _onResponseTextDeltaHandler = OnResponseTextDelta;
        _onResponseStreamStartedHandler = OnResponseStreamStarted;
        AI.OnResponseTextDelta += _onResponseTextDeltaHandler;
        AI.OnResponseStreamStarted += _onResponseStreamStartedHandler;
        // Wire the propose_plan tool handoff to the approval + execution UI
        PlanHandoff.OnPlanRequested = HandleProposedPlanAsync;
        // Re-render when DiffApprovalHandler activates the instruction TextInput.
        InstructionCoordinator.StateChanged += OnInstructionStateChanged;
        // Re-render when DiffApprovalHandler activates the VDOM approval menu.
        ApprovalMenu.StateChanged += OnApprovalMenuStateChanged;
        // Wire up diff approval callbacks if enabled
        if (Config.EnableDiffApprovals)
        {
            AI.OnWriteApprovalRequested = DiffApproval.HandleDiffApproval;
            AI.OnDeleteApprovalRequested = DiffApproval.HandleDeleteApproval;
            AI.OnCommandApprovalRequested = DiffApproval.HandleCommandApproval;
            McpGate.OnApprovalRequested = DiffApproval.HandleMcpApproval;
        }

        // Detect terminal theme and apply curated palette
        if (Config.EnableThemeCustomization && (Pane is null || Pane.Id == 1))
        {
            ThemeService.DetectTheme();
            ThemeService.ApplyPalette();
        }

        // Ctrl+C cancels the active AI request instead of killing the process.
        // Stored as a field so Dispose can detach it — the lambda closes over _requestCts,
        // which would otherwise keep being dereferenced after teardown.
        _cancelKeyHandler = (_, e) =>
        {
            if (Pane is not null && !Pane.Active)
                return;
            var cts = _requestCts;
            if (cts != null && !cts.IsCancellationRequested)
            {
                e.Cancel = true; // suppress default process termination
                cts.Cancel();
            }
            else
            {
                // No active request — clean up palette before process exits
                ThemeService.ResetPalette();
            }
        };
        Console.CancelKeyPress += _cancelKeyHandler;
    }

    /// <summary>Resolve shared workspace services separately from the agent's scoped services.
    /// Using the host scope for agent state would make panes share conversations and settings.</summary>
    private void ResolveAgentServices()
    {
        IntegrationCoordinator = Services.GetRequiredService<CliIntegrationCoordinator>();
        Delegations = Services.GetRequiredService<CliDelegations>();
        ModelDefaults = Services.GetRequiredService<AgentModelDefaults>();
        var agentServices = Pane?.Services ?? Services;
        AI = agentServices.GetRequiredService<AIService>();
        AI.UseExplicitPlanningOnly();
        Config = agentServices.GetRequiredService<MandoCodeConfig>();
        PlanRunners = agentServices.GetRequiredService<PlanRunnerSelector>();
        PlanHandoff = agentServices.GetRequiredService<PlanHandoff>();
        FileProvider = agentServices.GetRequiredService<FileAutocompleteProvider>();
        TokenTracker = agentServices.GetRequiredService<TokenTrackingService>();
        MusicPlayer = agentServices.GetRequiredService<MusicPlayerService>();
        ProjectRoot = agentServices.GetRequiredService<ProjectRootAccessor>();
        Spinner = agentServices.GetRequiredService<SpinnerService>();
        OperationRenderer = agentServices.GetRequiredService<OperationDisplayRenderer>();
        DiffApproval = agentServices.GetRequiredService<DiffApprovalHandler>();
        KeyCoordinator = agentServices.GetRequiredService<CancelKeyCoordinator>();
        InstructionCoordinator = agentServices.GetRequiredService<InstructionPromptCoordinator>();
        ApprovalMenu = agentServices.GetRequiredService<ApprovalSelectCoordinator>();
        Shell = agentServices.GetRequiredService<ShellCommandHandler>();
        ThemeService = agentServices.GetRequiredService<TerminalThemeService>();
        StateMachine = agentServices.GetRequiredService<InputStateMachine>();
        Skills = agentServices.GetRequiredService<SkillLoader>();
        McpManager = agentServices.GetRequiredService<McpClientManager>();
        McpGate = agentServices.GetRequiredService<McpApprovalGate>();
        PromptGate = agentServices.GetRequiredService<ApprovalPromptGate>();
        UpdateCheck = agentServices.GetRequiredService<UpdateCheckService>();
    }

    /// <summary>Register the component's pane callbacks as owned leases. An old component
    /// must not clear callbacks installed by a replacement during remounting.</summary>
    private void RegisterPaneCallbacks()
    {
        if (Pane is null) return;

        // History and request ownership belong to this mounted component.
        _paneCallbacks.Bind<Func<bool, bool>>(
            () => Pane.SaveHistory, value => Pane.SaveHistory = value, SaveAgentHistory);
        _paneCallbacks.Bind<Func<bool>>(
            () => Pane.IsHistoryOpen, value => Pane.IsHistoryOpen = value, () => _historyActive);
        _paneCallbacks.Bind<Action>(
            () => Pane.CloseHistory, value => Pane.CloseHistory = value, CloseHistory);
        _paneCallbacks.Bind<Func<bool>>(
            () => Pane.IsBusy, value => Pane.IsBusy = value, IsAgentBusy);
        _paneCallbacks.Bind<Func<bool>>(
            () => Pane.IsAwaitingInput, value => Pane.IsAwaitingInput = value, () => HasModalInput);
        _paneCallbacks.Bind<Action>(
            () => Pane.Stop, value => Pane.Stop = value, StopPane);
        _paneCallbacks.Bind<Func<string, Task>>(
            () => Pane.SubmitCommand, value => Pane.SubmitCommand = value, HandleVdomSubmit);

        // Other agents read a snapshot of the transcript on the renderer dispatcher.
        _paneCallbacks.Bind<Func<CliPeerRequest, Task<CliPeerAnswer>>>(
            () => Pane.AskPeer, value => Pane.AskPeer = value, AskPeerAsync);
        _paneCallbacks.Bind<Func<Task<IReadOnlyList<ChatMsg>>>>(
            () => Pane.ReadConversation, value => Pane.ReadConversation = value, ReadPaneConversationAsync);
        AI.SetHostTools(new CliAgentTools(Pane, Delegations).Functions);

        // Workspace shortcuts use callbacks so they do not depend on component internals.
        _paneCallbacks.Bind<Func<Task>>(
            () => Pane.ToggleSnapshots, value => Pane.ToggleSnapshots = value, ToggleSnapshots);
        _paneCallbacks.Bind<Func<Task>>(
            () => Pane.ToggleSettings, value => Pane.ToggleSettings = value, ToggleSettings);
        _paneCallbacks.Bind<Func<Task>>(
            () => Pane.ToggleFileExplorer, value => Pane.ToggleFileExplorer = value, ToggleFileExplorer);
        _paneCallbacks.Bind<Func<bool>>(
            () => Pane.IsFileExplorerOpen, value => Pane.IsFileExplorerOpen = value, () => _fileExplorerOpen);
        _paneCallbacks.Bind<Func<Task>>(
            () => Pane.ToggleGitChanges, value => Pane.ToggleGitChanges = value, ToggleGitChanges);
        _paneCallbacks.Bind<Func<bool>>(
            () => Pane.IsGitChangesOpen, value => Pane.IsGitChangesOpen = value, () => _gitChangesOpen);
    }

    private bool IsAgentBusy() => !_hasRendered || _isProcessing
        || InstructionCoordinator.IsActive || ApprovalMenu.IsActive
        || _planSelectActive || _presentation.BlocksRequests;

    private async Task<IReadOnlyList<ChatMsg>> ReadPaneConversationAsync()
    {
        IReadOnlyList<ChatMsg> conversation = [];
        await InvokeAsync(() => conversation = _messages
            .Select(message => new ChatMsg { Role = message.Role, Text = message.Text })
            .ToArray());
        return conversation;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        using var output = Pane is null ? null : TuiConsole.Enter(Pane.Session);
        if (Pane is not null && _headerModel != Config.GetEffectiveModelName())
        {
            _headerModel = Config.GetEffectiveModelName();
            Pane.Workspace.Refresh();
        }

        if (firstRender && !_connectionChecked)
        {
            await InitializeConnectionAsync();
        }

        if (_connectionChecked && !_hasRendered)
        {
            _hasRendered = true;
            RestoreAgentHistory();
            // --continue: reload the most recent conversation for this project folder,
            // full fidelity (tool calls included). Failure degrades to a fresh start with
            // an honest note — never an error.
            if (_continueRequested)
            {
                var savedJson = SessionResumeStore.Load(ProjectRoot.ProjectRoot);
                var restoredCount = savedJson == null ? 0 : AI.TryRestoreHistoryJson(savedJson);
                TuiConsole.MarkupLine(restoredCount > 0 ? $"[green]Conversation restored ({restoredCount} messages) — continuing where you left off.[/]" : "[dim]No previous session to continue for this folder — starting fresh.[/]");
                TuiConsole.WriteLine();
                // Same gesture, so the same place: "continue where I left off" should mention a
                // plan that was still running as well as the conversation.
                ShowUnfinishedPlanNotice();
            }

            // Initialize imperative input with the shared state machine
            // Component input owns the keyboard; the imperative adapter is compatibility-only.
            // Small delay to ensure UI is fully rendered before accepting input
            await Task.Delay(100);
            _ = Task.Run(async () => await RunInteractiveLoopAsync());
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SaveAgentHistory(true);
        _directorySelection?.TrySetResult(null);
        using var output = Pane is null ? null : TuiConsole.Enter(Pane.Session);
        StopPane();
        _paneCallbacks.Dispose();
        StopCancelKeyListener();
        StopMusicVisualizer();
        Spinner.Stop();
        // Detach event handlers registered in OnInitialized.
        // AIService belongs to the agent scope; a component may remount within that scope.
        // Without unsubscription, stale handlers linger
        // across App lifecycle and capture a disposed _requestCts via closure.
        if (_onFunctionInvokedHandler != null)
            AI.OnFunctionInvoked -= _onFunctionInvokedHandler;
        if (_onFunctionCompletedHandler != null)
            AI.OnFunctionCompleted -= _onFunctionCompletedHandler;
        if (_onResponseTextDeltaHandler != null)
            AI.OnResponseTextDelta -= _onResponseTextDeltaHandler;
        if (_onResponseStreamStartedHandler != null)
            AI.OnResponseStreamStarted -= _onResponseStreamStartedHandler;
        if (_cancelKeyHandler != null)
            Console.CancelKeyPress -= _cancelKeyHandler;
        InstructionCoordinator.StateChanged -= OnInstructionStateChanged;
        ApprovalMenu.StateChanged -= OnApprovalMenuStateChanged;
        // Clear the PlanHandoff callback so late-arriving propose_plan calls don't
        // hit a disposed App. AIService stays, but plan requests just return the
        // "not wired up" fallback from PlanHandoff.ProcessAsync.
        if (PlanHandoff.OnPlanRequested == HandleProposedPlanAsync)
            PlanHandoff.OnPlanRequested = null;
        var cts = Interlocked.Exchange(ref _requestCts, null);
        cts?.Dispose();
        MusicPlayer?.Dispose();
        if (Pane is null || Pane.Id == 1)
            ThemeService?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Pane is null)
        {
            ((IDisposable)this).Dispose();
            return;
        }

        Dispose();
        await Pane.DisposeAsync();
    }
}