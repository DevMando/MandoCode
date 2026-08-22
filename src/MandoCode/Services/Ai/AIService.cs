/**
 *  Author: DevMando
 *  Date: 2025-12-10
 *  Description: AIService.cs - Manages AI interactions using Semantic Kernel with Ollama.
 *  File: AIService.cs
 */

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
// MAF side of the SK -> Agent Framework migration (feat/agent-framework-migration). Both stacks
// intentionally coexist while the migration is in progress — see BuildAgent()'s doc comment.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using MandoCode.Models;
using MandoCode.Plugins;
using ModelContextProtocol.Client;
using System.Net;
using System.Text.Json;

namespace MandoCode.Services;


/// <summary>
/// Manages AI interactions using Semantic Kernel with Ollama.
/// </summary>
public class AIService
{
    private Kernel _kernel;
    private IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private string _systemPrompt;

    // MAF agent — the live chat path since the cutover (feat/agent-framework-migration). _kernel/
    // _chatService/_functionFilter are still built (BuildKernel) but no longer called from any
    // live call site — kept alive only for FallbackFunctionCallExecutor, until final SK cleanup.
    private AIAgent? _agent;

    // Set by ExecuteAgentModelCallAsync when a call throws after at least one tool call
    // genuinely completed — MAF's RunAsync is atomic, so unlike SK's connector (which mutates
    // the passed ChatHistory mid-call) there's otherwise no record of rounds that finished just
    // before a failure (e.g. context overflow partway through a multi-round tool-calling turn).
    // Reset to null at the start of every call; consumed by ExecutePlanStepAsync's
    // context-overflow recovery and CompactChatHistoryAsync.
    private List<string>? _lastCallPartialTrace;

    // MCP tools bridged to AIFunction, keyed by "mcp_<server>" — populated (and reconciled
    // against removed/disabled servers) by AttachMcpPluginsAsync, folded into _agent's tool
    // list by BuildAgent. See BuildAgent's doc comment.
    private readonly Dictionary<string, IReadOnlyList<AIFunction>> _mcpAgentToolsByServer = new();

    // Tool name -> server name, rebuilt from _mcpAgentToolsByServer whenever it changes. Feeds
    // AgentFunctionMiddleware.McpServerNameResolver — MAF tools have no plugin-name prefix to
    // check the way SK's PluginName.StartsWith("mcp_") did, so the middleware needs this map.
    private readonly Dictionary<string, string> _mcpToolServerByName = new();

    // MAF-side function-calling middleware — see AgentFunctionMiddleware's doc comment (Phase 3).
    // Rebuilt in BuildAgent alongside _agent; wiring mirrors _functionFilter's (see the
    // OnWriteApprovalRequested/OnDeleteApprovalRequested/OnCommandApprovalRequested setters above).
    private AgentFunctionMiddleware? _agentFunctionMiddleware;

    // The verbatim user message that opened the current chat turn (including @file/@folder
    // expansions). Plan steps execute in isolated chat histories and need it for ground
    // truth about target paths — see ChatStreamAsync and BuildStepContext.
    private string? _currentTurnUserMessage;
    private MandoCodeConfig _config;
    private OllamaPromptExecutionSettings _settings;
    private readonly ProjectRootAccessor _projectRootAccessor;
    private readonly FunctionCompletionTracker _completionTracker = new();
    private FunctionInvocationFilter _functionFilter;
    private readonly TokenTrackingService _tokenTracker;
    private readonly PlanHandoff _planHandoff;
    private readonly SkillLoader _skillLoader;
    private readonly McpClientManager _mcpManager;
    private readonly McpApprovalGate _mcpApprovalGate;
    private readonly SpinnerService _spinner;
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private readonly FallbackFunctionCallExecutor _fallbackExecutor;

    /// <summary>
    /// Event raised when a function is about to be invoked.
    /// </summary>
    public event Action<FunctionCall>? OnFunctionInvoked;

    /// <summary>
    /// Event raised when a function completes (success or failure).
    /// </summary>
    public event Action<FunctionExecutionResult>? OnFunctionCompleted;

    /// <summary>
    /// Exposes the completion tracker for external consumers (e.g., TaskPlannerService).
    /// </summary>
    public FunctionCompletionTracker CompletionTracker => _completionTracker;

    /// <summary>
    /// Async callback for requesting user approval before writing a file.
    /// Set this from the UI layer (App.razor) to enable diff approvals.
    /// </summary>
    private Func<string, string?, string, Task<DiffApprovalResult>>? _onWriteApprovalRequested;
    public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested
    {
        get => _onWriteApprovalRequested;
        set
        {
            _onWriteApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnWriteApprovalRequested = value;
            }
            if (_agentFunctionMiddleware != null)
            {
                _agentFunctionMiddleware.OnWriteApprovalRequested = value;
            }
        }
    }

    /// <summary>
    /// Async callback for requesting user approval before deleting a file.
    /// Set this from the UI layer (App.razor) to enable delete approvals.
    /// </summary>
    private Func<string, string?, Task<DiffApprovalResult>>? _onDeleteApprovalRequested;
    public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested
    {
        get => _onDeleteApprovalRequested;
        set
        {
            _onDeleteApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnDeleteApprovalRequested = value;
            }
            if (_agentFunctionMiddleware != null)
            {
                _agentFunctionMiddleware.OnDeleteApprovalRequested = value;
            }
        }
    }

    /// <summary>
    /// Async callback for requesting user approval before executing a shell command.
    /// Set this from the UI layer (App.razor) to enable command approvals.
    /// </summary>
    private Func<string, Task<DiffApprovalResult>>? _onCommandApprovalRequested;
    public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested
    {
        get => _onCommandApprovalRequested;
        set
        {
            _onCommandApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnCommandApprovalRequested = value;
            }
            if (_agentFunctionMiddleware != null)
            {
                _agentFunctionMiddleware.OnCommandApprovalRequested = value;
            }
        }
    }

    public AIService(ProjectRootAccessor projectRootAccessor, MandoCodeConfig config, TokenTrackingService tokenTracker, PlanHandoff planHandoff, SkillLoader skillLoader, McpClientManager mcpManager, McpApprovalGate mcpApprovalGate, SpinnerService spinner)
    {
        _projectRootAccessor = projectRootAccessor;
        _config = config;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        _skillLoader = skillLoader;
        _mcpManager = mcpManager;
        _mcpApprovalGate = mcpApprovalGate;
        _spinner = spinner;
        _fallbackExecutor = new FallbackFunctionCallExecutor(
            call => OnFunctionInvoked?.Invoke(call),
            result => OnFunctionCompleted?.Invoke(result));
        RebuildSystemPrompt();
        BuildKernel();
        BuildAgent();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Composes the system prompt from the current config: main prompt (web-search
    /// claims conditional on EnableWebSearch), shell-specific rules (cmd.exe vs bash),
    /// and the skill index so the model knows which workflows load_skill() offers.
    /// Called from the constructor and from every settings path — the prompt must track
    /// the config, or toggling websearch would leave the model promising searches it
    /// can't run (or denying ones it can).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_systemPrompt))]
    private void RebuildSystemPrompt()
    {
        var skillIndex = SystemPrompts.BuildSkillIndex(_skillLoader.GetAll());
        _systemPrompt = SystemPrompts.BuildMandoCodeAssistant(_config.EnableWebSearch, _config.AgentName) + "\n\n" + ShellEnvironment.SystemPromptRules;
        if (!string.IsNullOrEmpty(skillIndex))
        {
            _systemPrompt += "\n\n" + skillIndex;
        }

        // Date grounding. A model's training data often ends months before "now"; without an
        // anchor it misreads current events in search results as fabricated "future" content
        // (a session once accused its own search tool of generating fiction over a real
        // announcement dated after its cutoff). Day-level staleness is acceptable — the
        // failure mode this prevents is being off by a year, not an afternoon. The prompt is
        // rebuilt on every settings change and session (re)build, which refreshes the date.
        _systemPrompt += $"\n\nCurrent date: {DateTime.Now:dddd, MMMM d, yyyy}. Your training data may predate this. " +
            "Treat web results, news, and file timestamps dated up to today as real current events — " +
            "not speculation, leaks, or simulated content. Only dates AFTER today are actually the future.";
    }

    /// <summary>
    /// Reinitializes the AI service with a new configuration.
    /// Rebuilds the kernel with the updated model and settings.
    /// </summary>
    public async Task ReinitializeAsync(MandoCodeConfig config)
    {
        _config = config;
        RebuildSystemPrompt();
        BuildKernel();
        BuildAgent();
        await AttachMcpPluginsAsync();
        await ClearHistoryAsync();
    }

    /// <summary>
    /// Rebuilds the kernel with the current config WITHOUT clearing chat history.
    /// Used by /config set for kernel-baked settings (temperature, maxTokens, toolBudget,
    /// plugin toggles) so an inline tweak doesn't nuke the conversation. Model/endpoint
    /// switches via /model and /setup keep using <see cref="ReinitializeAsync"/> —
    /// a different model mid-history is a different conversation.
    /// </summary>
    public async Task RefreshSettingsAsync(MandoCodeConfig config)
    {
        _config = config;
        RebuildSystemPrompt();

        // The history-preserving path still has the OLD system prompt as message 0 —
        // swap it in place so a mid-conversation toggle (e.g. websearch) actually
        // reaches the model instead of waiting for the next /clear.
        if (_chatHistory.Count > 0 && _chatHistory[0].Role == AuthorRole.System)
        {
            _chatHistory[0] = new ChatMessageContent(AuthorRole.System, _systemPrompt);
        }

        BuildKernel();
        BuildAgent();
        await AttachMcpPluginsAsync();
    }

    /// <summary>
    /// Registers tools from every active MCP client as SK plugins on the current kernel AND
    /// (feat/agent-framework-migration) as MAF AIFunctions on <see cref="_agent"/>. Idempotent
    /// per server within a single kernel instance — registration is skipped if a plugin with
    /// the same <c>mcp_&lt;server&gt;</c> name is already present. BuildKernel/BuildAgent discard
    /// the old kernel/agent, so after a rebuild the next call re-registers from scratch.
    ///
    /// Reconciles <see cref="_mcpAgentToolsByServer"/> against the CURRENT active-client set
    /// first, before the idempotency check below — a server disabled or removed since the last
    /// call must drop out of the dictionary (and therefore _agent's tool list on the next
    /// rebuild) even though the loop below never revisits it. Rebuilds <see cref="_agent"/> at
    /// the end whenever anything changed, since AIAgent's tool list is fixed at construction —
    /// unlike _kernel.Plugins, there's no in-place "add a tool" for the agent side.
    /// </summary>
    public async Task AttachMcpPluginsAsync(CancellationToken cancellationToken = default)
    {
        var activeServerNames = _mcpManager.ActiveClients.Keys.ToHashSet();
        var changed = false;

        foreach (var staleKey in _mcpAgentToolsByServer.Keys
            .Where(pluginName => !activeServerNames.Contains(pluginName["mcp_".Length..]))
            .ToList())
        {
            _mcpAgentToolsByServer.Remove(staleKey);
            changed = true;
        }

        if (_config.EnableMcp)
        {
            foreach (var (serverName, client) in _mcpManager.ActiveClients)
            {
                var pluginName = $"mcp_{serverName}";
                if (_kernel.Plugins.Any(p => p.Name == pluginName)) continue;

                try
                {
                    var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                    if (tools.Count == 0) continue;
                    _kernel.Plugins.AddFromFunctions(
                        pluginName,
                        tools.Select(t => t.AsKernelFunction()));

                    // MAF side: no bridge method needed at all — McpClientTool derives directly
                    // from Microsoft.Extensions.AI.AIFunction, so it already IS one.
                    _mcpAgentToolsByServer[pluginName] = tools.Cast<AIFunction>().ToList();
                    changed = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MCP] Failed to list tools for '{serverName}': {ex.Message}");
                }
            }
        }

        if (changed)
        {
            _mcpToolServerByName.Clear();
            foreach (var (pluginName, fns) in _mcpAgentToolsByServer)
            {
                var serverName = pluginName["mcp_".Length..];
                foreach (var fn in fns) _mcpToolServerByName[fn.Name] = serverName;
            }

            BuildAgent();
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_kernel), nameof(_chatService), nameof(_settings), nameof(_functionFilter))]
    private void BuildKernel()
    {
        _settings = new()
        {
            Temperature = (float)_config.Temperature,
            NumPredict = _config.MaxTokens,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
        };

        var builder = Kernel.CreateBuilder();

        // Route Ollama traffic through NumCtxHttpHandler so the configured context window
        // rides on every request instead of depending on how the daemon was started.
        // Timeout is infinite on purpose: model calls are bounded by MandoCode's own stall
        // watchdog and request-timeout ceiling, and a fixed HttpClient timeout underneath
        // them would surface as a bogus transport error on slow local generations.
        // The old client (like the old kernel it served) is left for GC rather than
        // disposed — a rebuild can race a call still in flight on the discarded kernel.
        var ollamaHttpClient = new HttpClient(new NumCtxHttpHandler(EffectiveNumCtx))
        {
            BaseAddress = new Uri(_config.OllamaEndpoint),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        builder.AddOllamaChatCompletion(
            modelId: _config.GetEffectiveModelName(),
            httpClient: ollamaHttpClient
        );

        var fileSystemPlugin = new FileSystemPlugin(_projectRootAccessor, _spinner);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        if (_config.EnableWebSearch)
        {
            builder.Plugins.AddFromObject(new WebSearchPlugin(_config.GetEffectiveTavilyApiKey()), "WebSearch");
        }

        if (_config.EnableTaskPlanning)
        {
            builder.Plugins.AddFromObject(new PlanningPlugin(), "Planning");
        }

        // Always register the Skills plugin — even when no skills are installed, so
        // users can add skills and trigger a reload without rebuilding the kernel.
        builder.Plugins.AddFromObject(new SkillsPlugin(_skillLoader), "Skills");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Set up function invocation filter for UI events, deduplication, and propose_plan interception.
        // Handlers on the PREVIOUS filter are deliberately left attached: a rebuild (e.g. /config set
        // mid-session) can race a function still in flight on the old kernel, and that function's
        // completion must still reach _completionTracker — detaching here would leak the pending count
        // and pin the stall watchdog paused. The old filter only fires for calls already routed through
        // the discarded kernel, so nothing fires twice; it becomes collectible once those finish.
        _functionFilter = new FunctionInvocationFilter(_config.FunctionDeduplicationWindowSeconds, _projectRootAccessor, _tokenTracker, _planHandoff, _config.ToolResultCharBudget);
        _functionFilter.OnFunctionInvoked += call => OnFunctionInvoked?.Invoke(call);
        _functionFilter.OnFunctionCompleted += result => OnFunctionCompleted?.Invoke(result);
        _functionFilter.OnFunctionStarted += () => _completionTracker.RegisterStart();
        _functionFilter.OnFunctionFinished += () => _completionTracker.RegisterCompletion();

        // Wire diff approval callbacks through to the filter
        if (_onWriteApprovalRequested != null)
        {
            _functionFilter.OnWriteApprovalRequested = _onWriteApprovalRequested;
        }
        if (_onDeleteApprovalRequested != null)
        {
            _functionFilter.OnDeleteApprovalRequested = _onDeleteApprovalRequested;
        }
        if (_onCommandApprovalRequested != null)
        {
            _functionFilter.OnCommandApprovalRequested = _onCommandApprovalRequested;
        }

        // MCP gate — filter delegates to the gate for any plugin whose name starts with "mcp_"
        _functionFilter.McpApprovalGate = _mcpApprovalGate;

        _kernel.FunctionInvocationFilters.Add(_functionFilter);
    }

    /// <summary>
    /// Builds the Microsoft Agent Framework equivalent of <see cref="BuildKernel"/>, as a
    /// parallel construction path for the SK -> Agent Framework migration
    /// (feat/agent-framework-migration, see memory agent-framework-migration.md). Mirrors
    /// BuildKernel's non-plugin concerns — the same NumCtxHttpHandler wiring, endpoint, model,
    /// and temperature/max-tokens mapping — plus (Phase 2) the same 16 plugin functions,
    /// registered as plain <see cref="AIFunction"/>s with the exact snake_case names the system
    /// prompt and any user skills already reference (MAF has no plugin/namespace concept —
    /// [KernelFunction] is left in place on the plugin classes for SK's AddFromObject and simply
    /// ignored here; each tool is bound directly off a plugin instance method).
    ///
    /// (Phase 3) <see cref="AgentFunctionMiddleware"/> is attached via <c>AsBuilder().Use(...)</c>
    /// and ports the approval gating, circuit breakers, and propose_plan interception from
    /// <see cref="FunctionInvocationFilter"/> — gating happens inline in the middleware, awaiting
    /// the same approval callbacks, NOT via <c>ApprovalRequiredAIFunction</c> (see that class's
    /// doc comment for why). MCP tools (from <see cref="_mcpAgentToolsByServer"/>, kept current by
    /// <see cref="AttachMcpPluginsAsync"/>) are included in the tool list and gated the same way as
    /// everything else, via <see cref="AgentFunctionMiddleware.McpServerNameResolver"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_agent), nameof(_agentFunctionMiddleware))]
    private void BuildAgent()
    {
        // Same handler/timeout rationale as BuildKernel's ollamaHttpClient — see there. Two
        // clients, one per stack, both pointed at the same daemon; harmless duplication while
        // both are alive, deleted along with BuildKernel once the migration finishes.
        var ollamaHttpClient = new HttpClient(new NumCtxHttpHandler(EffectiveNumCtx))
        {
            BaseAddress = new Uri(_config.OllamaEndpoint),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        var ollamaClient = new OllamaApiClient(ollamaHttpClient, _config.GetEffectiveModelName());

        var tools = new List<AITool>();

        // FileSystem — same instance construction and ignore-directories wiring as BuildKernel.
        var fileSystemPlugin = new FileSystemPlugin(_projectRootAccessor, _spinner);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        tools.Add(NamedTool(fileSystemPlugin.ListAllProjectFiles, "list_all_project_files"));
        tools.Add(NamedTool(fileSystemPlugin.ListFiles, "list_files_match_glob_pattern"));
        tools.Add(NamedTool(fileSystemPlugin.ReadFile, "read_file_contents"));
        tools.Add(NamedTool(fileSystemPlugin.CreateFolder, "create_folder"));
        tools.Add(NamedTool(fileSystemPlugin.WriteFile, "write_file"));
        tools.Add(NamedTool(fileSystemPlugin.EditFile, "edit_file"));
        tools.Add(NamedTool(fileSystemPlugin.GrepFiles, "grep_files"));
        tools.Add(NamedTool(fileSystemPlugin.DeleteFile, "delete_file"));
        tools.Add(NamedTool(fileSystemPlugin.DeleteFolder, "delete_folder"));
        tools.Add(NamedTool(fileSystemPlugin.FindInFiles, "search_text_in_files"));
        tools.Add(NamedTool(fileSystemPlugin.GetAbsolutePath, "get_absolute_path"));
        tools.Add(NamedTool(fileSystemPlugin.ExecuteCommand, "execute_command"));

        if (_config.EnableWebSearch)
        {
            var webSearchPlugin = new WebSearchPlugin(_config.GetEffectiveTavilyApiKey());
            tools.Add(NamedTool(webSearchPlugin.SearchWeb, "search_web"));
            tools.Add(NamedTool(webSearchPlugin.FetchWebpage, "fetch_webpage"));
        }

        if (_config.EnableTaskPlanning)
        {
            var planningPlugin = new PlanningPlugin();
            tools.Add(NamedTool(planningPlugin.ProposePlan, "propose_plan"));
        }

        // Always registered — same rationale as BuildKernel: lets users add skills and reload
        // without a rebuild.
        var skillsPlugin = new SkillsPlugin(_skillLoader);
        tools.Add(NamedTool(skillsPlugin.LoadSkill, "load_skill"));

        // MCP tools — whatever AttachMcpPluginsAsync has discovered so far. Read-only here;
        // AttachMcpPluginsAsync owns reconciling this dictionary against active/removed servers
        // and calls BuildAgent again itself whenever it changes.
        foreach (var mcpTools in _mcpAgentToolsByServer.Values)
        {
            tools.AddRange(mcpTools);
        }

        // Compaction (Phase 4 — NOT wired, see below): Microsoft Learn documents a
        // PipelineCompactionStrategy/CompactionProvider API (experimental, MAAI001) for exactly
        // this. It does not exist in Microsoft.Agents.AI 1.18.0 — the actual latest version on
        // NuGet as of this migration — confirmed by the compiler failing to resolve
        // PipelineCompactionStrategy/ToolResultCompactionStrategy/SlidingWindowCompactionStrategy/
        // TruncationCompactionStrategy/CompactionTriggers/CompactionProvider against the real
        // referenced-assembly graph, not just a missing `using`. The docs describe a feature
        // ahead of what's actually shipped. CompactChatHistoryAsync (SK side, unchanged) remains
        // the only compaction mechanism for now. Re-check when a newer Microsoft.Agents.AI
        // version ships — see memory agent-framework-migration.md.
        var baseAgent = ollamaClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "MandoCode",
            ChatOptions = new Microsoft.Extensions.AI.ChatOptions
            {
                Instructions = _systemPrompt,
                Temperature = (float)_config.Temperature,
                MaxOutputTokens = _config.MaxTokens,
                Tools = tools,
            },
        });

        // Same rebuild rationale as BuildKernel's _functionFilter: the previous middleware
        // instance is left attached to the discarded agent rather than detached, since a call
        // still in flight on it must still reach _completionTracker.
        _agentFunctionMiddleware = new AgentFunctionMiddleware(
            _config.FunctionDeduplicationWindowSeconds, _projectRootAccessor, _tokenTracker, _planHandoff, _config.ToolResultCharBudget);
        _agentFunctionMiddleware.OnFunctionInvoked += call => OnFunctionInvoked?.Invoke(call);
        _agentFunctionMiddleware.OnFunctionCompleted += result => OnFunctionCompleted?.Invoke(result);
        _agentFunctionMiddleware.OnFunctionStarted += () => _completionTracker.RegisterStart();
        _agentFunctionMiddleware.OnFunctionFinished += () => _completionTracker.RegisterCompletion();

        if (_onWriteApprovalRequested != null)
        {
            _agentFunctionMiddleware.OnWriteApprovalRequested = _onWriteApprovalRequested;
        }
        if (_onDeleteApprovalRequested != null)
        {
            _agentFunctionMiddleware.OnDeleteApprovalRequested = _onDeleteApprovalRequested;
        }
        if (_onCommandApprovalRequested != null)
        {
            _agentFunctionMiddleware.OnCommandApprovalRequested = _onCommandApprovalRequested;
        }

        _agentFunctionMiddleware.McpApprovalGate = _mcpApprovalGate;
        _agentFunctionMiddleware.McpServerNameResolver = name =>
            _mcpToolServerByName.TryGetValue(name, out var server) ? server : null;

        _agent = baseAgent.AsBuilder().Use(_agentFunctionMiddleware.InterceptAsync).Build();
    }

    /// <summary>
    /// Wraps a plugin instance method as an <see cref="AIFunction"/> under an explicit name.
    /// AIFunctionFactory otherwise derives the tool name from the C# method name (e.g.
    /// "ListAllProjectFiles"), which doesn't match the snake_case names
    /// ([KernelFunction("list_all_project_files")]) the system prompt, skills, and the model's
    /// own tool-call habits already depend on — so every call site here passes the real name
    /// explicitly rather than relying on the default.
    /// </summary>
    private static AIFunction NamedTool(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    /// <summary>
    /// The num_ctx stamped on outgoing chat requests: the configured context length for
    /// local models, 0 (leave the request untouched) for cloud models — their context
    /// lives server-side at the model's full window, so a local KV-cache size is
    /// meaningless there. Re-read per request via <see cref="NumCtxHttpHandler"/>, so
    /// /config changes apply from the next message.
    /// </summary>
    private int EffectiveNumCtx()
        => MandoCodeConfig.IsCloudModel(_config.GetEffectiveModelName())
            ? 0
            : Math.Max(0, _config.ContextLength);

    /// <summary>
    /// Validates that the configured model supports function calling (tools).
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateModelAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var modelName = _config.GetEffectiveModelName();

            // Check if model exists and get its info
            using var response = await client.PostAsync(
                OllamaSetupHelper.BuildUrl(_config.OllamaEndpoint, "api/show"),
                new StringContent(JsonSerializer.Serialize(new { name = modelName }), System.Text.Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Model '{modelName}' not found. Run: ollama pull {modelName}");
            }

            // Model exists and is available — Ollama handles tool support at the API level
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Could not validate model: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a message to the AI and streams the response chunk by chunk.
    /// NOTE: Uses non-streaming mode internally for reliable function execution with local models.
    /// Streaming with auto-invocation causes issues where function calls are not properly parsed
    /// or executed by the Semantic Kernel with local Ollama models.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Capture the verbatim request for plan-step context. If this turn proposes a
        // plan, each step runs in its own fresh chat history and only sees the model's
        // distilled `goal` — a lossy summary. Observed live: "@STarfox/ create a game…"
        // became goal "create a game…", and every step wrote to the project root instead
        // of STarfox/. The verbatim message (with App.razor's @file/@folder expansions)
        // is the ground truth for target paths.
        _currentTurnUserMessage = userMessage;

        // Add message under lock, then release before the long AI call
        await _historyLock.WaitAsync(cancellationToken);
        try { _chatHistory.AddUserMessage(userMessage); }
        finally { _historyLock.Release(); }

        int continuations = 0;
        while (true)
        {
            var (response, needsContinuation) = await RunOneChatTurnAsync(continuations, cancellationToken);
            yield return response;

            if (!needsContinuation)
                yield break;

            continuations++;
        }
    }

    /// <summary>
    /// Runs a single chat turn inside its own <see cref="InvocationScope"/>. When the
    /// tool budget is exhausted, the assistant's text response acts as an implicit
    /// progress summary — we return <c>needsContinuation=true</c>, push a "keep going"
    /// user message, and the caller loops for another turn with a fresh budget.
    /// </summary>
    private async Task<(string response, bool needsContinuation)> RunOneChatTurnAsync(int continuationIndex, CancellationToken cancellationToken)
    {
        string response;
        bool needsContinuation = false;

        // Pre-flight overflow check. Local Ollama never REJECTS an oversized prompt — it
        // silently drops the oldest tokens, which chops the system prompt and tool
        // definitions first and surfaces as an empty or incoherent response. The reactive
        // IsContextOverflowError recovery below never fires for that, so the only working
        // moment to compact is BEFORE the send. Cloud models skip this: their providers do
        // reject oversized prompts, which routes to the existing reactive recovery.
        var preflightNote = "";
        if (_config.ContextLength > 0 && !MandoCodeConfig.IsCloudModel(_config.GetEffectiveModelName()))
        {
            long estimatedPromptTokens = (await EstimateHistoryCharsAsync() + EstimateToolSchemaChars()) / CharsPerTokenEstimate;
            if (ExceedsContextBudget(estimatedPromptTokens, _config.ContextLength))
            {
                await CompactChatHistoryAsync();
                preflightNote = "⚠ Conversation neared the context window — older history was compacted into a recap so the model has room to answer.\n\n";
            }
        }

        try
        {
            using var scope = _agentFunctionMiddleware!.BeginScope();

            // pauseDuringPlan: this outer call can run a whole plan (propose_plan). Both outer
            // timers (the stall watchdog and the request-timeout ceiling) pause for the plan's
            // duration so neither can cancel a step and surface as a bogus "Cancelled by user."
            // Each step has its own watchdog + request timeout, so steps stay bounded.
            var result = await ExecuteAgentModelCallAsync(
                _chatHistory,
                retryOperationName: "ChatStreamAsync",
                tokenLabel: "Chat",
                spinnerMessage: "Thinking… (Esc to cancel)",
                pauseDuringPlan: true,
                cancellationToken);

            var rawResponse = string.IsNullOrEmpty(result.Text) ? "No response from AI." : result.Text;
            response = _config.EnableFallbackFunctionParsing
                ? await _fallbackExecutor.ProcessAsync(rawResponse, _kernel, _config.GetEffectiveModelName())
                : rawResponse;

            if (result.DoneStream is { } doneStream
                && string.Equals(doneStream.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                response += BuildLengthCutoffNotice(
                    doneStream.EvalCount,
                    _config.MaxTokens,
                    _config.ContextLength,
                    emptyContent: string.IsNullOrEmpty(result.Text),
                    isCloudModel: MandoCodeConfig.IsCloudModel(_config.GetEffectiveModelName()));
            }

            await _historyLock.WaitAsync();
            try
            {
                AppendAgentTurnToHistory(_chatHistory, result.NewHistoryMessages, response);
            }
            finally { _historyLock.Release(); }

            if (scope.BudgetExhausted
                && _config.EnableAutoContinuation
                && continuationIndex < _config.MaxAutoContinuations)
            {
                needsContinuation = true;
                response += $"\n\n⟳ Auto-continuing ({continuationIndex + 1}/{_config.MaxAutoContinuations}) — tool budget was full; resuming with a fresh budget.\n";

                await _historyLock.WaitAsync();
                try
                {
                    _chatHistory.AddUserMessage(
                        "Continue from where you left off. Your previous response was a progress summary; " +
                        "the tool-call budget has been reset, so you can call tools again to finish the remaining work.");
                }
                finally { _historyLock.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response = "Request cancelled.";
        }
        catch (ModelStallException)
        {
            // Stall watchdog fired — the model went quiet (no tokens, no tool activity) for longer
            // than the per-call budget. Usually a local model stalling as context grows.
            response = $"Error: the model went silent for {_config.ModelResponseTimeoutSeconds}s and was stopped by the stall watchdog.\n\n" +
                       "This usually means a local model stalled as context grew. Try /clear to trim history, a smaller request, " +
                       $"or raise the watchdog: /config set modelResponseTimeout 300.";
        }
        catch (ModelCallTimeoutException)
        {
            response = "Error: Request timed out. The model took too long to respond.\n\n" +
                      "Try breaking your request into smaller parts, or use a faster model.";
        }
        // Provider-side "request too big" rejection in direct chat — covers both model
        // context-window overflow AND transport-level 413 / "request body too large"
        // from Ollama's Go HTTP server. Same recovery for both: compact the persistent
        // _chatHistory into a recap so the next turn fits under the provider's limit.
        catch (Exception ex) when (IsContextOverflowError(ex)
                                    && _config.EnableAutoContinuation
                                    && continuationIndex < _config.MaxAutoContinuations)
        {
            await CompactChatHistoryAsync();
            needsContinuation = true;
            response = $"⚠ Provider rejected request (payload too large). " +
                       $"Compacting conversation history and retrying ({continuationIndex + 1}/{_config.MaxAutoContinuations})...\n";
        }
        catch (HttpRequestException ex)
        {
            response = FormatHttpFailure(ex);
        }
        catch (Exception ex)
        {
            response = FormatErrorMessage(ex);
        }

        if (preflightNote.Length > 0)
            response = preflightNote + response;

        return (response, needsContinuation);
    }

    /// <summary>Rough chars-per-token divisor for pre-flight prompt sizing. Deliberately
    /// conservative for English-plus-JSON payloads (real ratios run ~3.5-4.5).</summary>
    private const int CharsPerTokenEstimate = 4;

    /// <summary>
    /// True when an estimated prompt would leave less than a safe reserve inside the context
    /// window. The reserve is 1/4 of the window clamped to [1024, 4096] tokens, and must cover
    /// TWO things the pre-flight estimate cannot see:
    ///   • Intra-turn tool growth — the check runs once at turn start, but SK's auto-invoke
    ///     loop re-sends the prompt after each tool call with the results appended. Observed
    ///     live: a turn that started ~1k under an 8k window died mid-turn when one web search
    ///     added ~1.2k tokens. A single tool round-trip has to fit inside the reserve.
    ///   • Generation headroom — thinking models (qwen3, minimax) spend output tokens on
    ///     internal reasoning before any visible text, so a prompt that technically "fits"
    ///     with no headroom still yields an empty response.
    /// Static + public for direct unit testing.
    /// </summary>
    public static bool ExceedsContextBudget(long estimatedPromptTokens, int contextLength)
    {
        if (contextLength <= 0) return false;
        var reserve = Math.Clamp(contextLength / 4, 1024, 4096);
        return estimatedPromptTokens > contextLength - reserve;
    }

    /// <summary>
    /// Rough serialized size of the live history in characters. When a message carries
    /// Items (SK's auto-invoke loop puts function calls/results there), the items are
    /// counted INSTEAD of Content — ChatMessageContent.Content mirrors the first
    /// TextContent item, so counting both would double-count assistant turns.
    /// </summary>
    private async Task<long> EstimateHistoryCharsAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            long chars = 0;
            foreach (var msg in _chatHistory)
            {
                if (msg.Items == null || msg.Items.Count == 0)
                {
                    chars += msg.Content?.Length ?? 0;
                    continue;
                }

                foreach (var item in msg.Items)
                {
                    switch (item)
                    {
                        case Microsoft.SemanticKernel.FunctionCallContent fc:
                            chars += fc.FunctionName?.Length ?? 0;
                            if (fc.Arguments != null)
                                foreach (var kv in fc.Arguments)
                                    chars += kv.Key.Length + (kv.Value?.ToString()?.Length ?? 0);
                            break;
                        case Microsoft.SemanticKernel.FunctionResultContent fr:
                            chars += fr.Result?.ToString()?.Length ?? 0;
                            break;
                        case Microsoft.SemanticKernel.TextContent tc:
                            chars += tc.Text?.Length ?? 0;
                            break;
                    }
                }
            }
            return chars;
        }
        finally { _historyLock.Release(); }
    }

    /// <summary>
    /// Rough size of the tool definitions the connector serializes into EVERY request —
    /// they're not in the chat history, but with MCP servers attached they can be most of
    /// a small model's window, so a pre-flight estimate that ignores them undercounts badly.
    /// </summary>
    private long EstimateToolSchemaChars()
    {
        long chars = 0;
        foreach (var plugin in _kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var md = function.Metadata;
                chars += (md.Name?.Length ?? 0) + (md.Description?.Length ?? 0) + 40;
                foreach (var p in md.Parameters)
                    chars += (p.Name?.Length ?? 0) + (p.Description?.Length ?? 0)
                           + (p.Schema?.ToString()?.Length ?? 0) + 20;
            }
        }
        return chars;
    }

    /// <summary>
    /// Composes the warning appended when generation stops with done_reason "length".
    /// Two distinct causes share that reason and need OPPOSITE advice:
    ///   • Output reached the response cap (EvalCount ≈ maxTokens) → say "continue" or raise maxTokens.
    ///   • The CONTEXT WINDOW filled mid-generation (output far below the cap) → num_ctx is the
    ///     bottleneck and raising maxTokens does nothing — the old one-size-fits-all message sent
    ///     users to exactly the wrong knob. Common when the daemon was started outside MandoCode
    ///     (tray app) with Ollama's ~4k default window.
    /// <paramref name="emptyContent"/> flags the worst case: a thinking model (e.g. qwen3,
    /// minimax) spent the whole budget on internal reasoning and produced no visible answer.
    /// <paramref name="isCloudModel"/> swaps the window-filled advice: cloud context lives
    /// server-side at the model's full window, so the desktop-app slider / daemon-restart
    /// guidance is meaningless there — trimming history is the only lever.
    /// Static + public for direct unit testing without standing up the full service.
    /// </summary>
    public static string BuildLengthCutoffNotice(long completionTokens, int maxTokens, int configuredContextLength, bool emptyContent, bool isCloudModel = false)
    {
        // Formatted as markdown — the response path renders through MarkdownHtmlRenderer,
        // so a bold headline + bullet list reads far better than the old wall of text.

        // Ollama can stop a handful of tokens shy of the exact cap — treat anything
        // within 90% of maxTokens (or an unreported count) as a genuine cap hit.
        if (completionTokens <= 0 || completionTokens >= maxTokens * 9L / 10)
        {
            var thinkingCapNote = emptyContent
                ? "\n- Note: thinking models (qwen3, minimax) spend reasoning tokens from this same budget — " +
                  "a small max tokens limit can be consumed entirely by internal reasoning before any visible answer."
                : "";
            return "\n\n⚠ **Response cut off — hit the max response tokens limit.**\n" +
                   "- Say \"continue\" to keep going\n" +
                   "- Or raise max tokens with /config" +
                   thinkingCapNote;
        }

        var thinkingNote = emptyContent
            ? "\nNo visible answer was produced — likely a thinking model (e.g. qwen3, minimax) that spent it all on internal reasoning."
            : "";

        var header = "\n\n⚠ **Response cut off — the model's CONTEXT WINDOW filled.**\n" +
                     $"Only {completionTokens:N0} of your {maxTokens / 1024}k response budget was generated, " +
                     "so raising max tokens won't help." +
                     thinkingNote + "\n";

        if (isCloudModel)
        {
            return header +
                   "\nThe conversation filled the model's server-side context window. How to fix:\n" +
                   "- /clear to trim the conversation history\n" +
                   "- Break the request into smaller pieces";
        }

        // MandoCode stamps contextLength onto every request as num_ctx, so a configured
        // window IS the window that filled — the fix is a bigger value (or /clear), never
        // a daemon restart. Only contextLength 0 defers to the daemon's own default.
        var applyLine = configuredContextLength > 0
            ? $"- Your configured {configuredContextLength / 1024}k window applies to every request — raise it: " +
              "/config set contextLength 32768 (applies from your next message; more window uses more VRAM)"
            : "- No window configured (contextLength 0 = daemon default, often ~4k) — set one: /config set contextLength 16384 " +
              "(applies from your next message; Ollama desktop app users can instead drag Settings → Context length)";

        return header +
               "\nThe context window filled mid-generation. How to fix:\n" +
               applyLine + "\n" +
               "- /clear frees space right now by trimming history";
    }

    /// <summary>
    /// Shared model-call scaffolding for direct chat turns and plan steps: the per-turn
    /// request-timeout ceiling, the stall watchdog, the heartbeat spinner, the retry policy,
    /// and token recording. Cancellation is classified here — while the token sources are
    /// still in scope — into typed exceptions so each caller phrases its own user-facing
    /// message: <see cref="ModelStallException"/> when the watchdog fired,
    /// <see cref="ModelCallTimeoutException"/> when the request ceiling was hit. A
    /// user-initiated cancellation rethrows the original <see cref="OperationCanceledException"/>.
    /// All other exceptions (context overflow, HTTP failures) propagate unwrapped.
    /// </summary>
    private async Task<ChatMessageContent> ExecuteModelCallAsync(
        ChatHistory history,
        OllamaPromptExecutionSettings settings,
        string retryOperationName,
        string tokenLabel,
        string spinnerMessage,
        bool pauseDuringPlan,
        CancellationToken cancellationToken)
    {
        // Two timeouts: requestCts is the generous per-turn ceiling (RequestTimeoutMinutes);
        // responseCts is the stall watchdog (ModelResponseTimeoutSeconds) — a much shorter
        // bound on a single model-silent stretch so a local model that stops streaming once
        // context grows recovers in minutes, not the full ceiling.
        using var requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));
        using var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCts.Token, responseCts.Token);

        // The request ceiling only needs plan-pausing on the outer chat turn (the whole plan
        // runs inside that single model call); plan steps keep their own bounded ceiling.
        using var watchdog = AttachStallWatchdog(
            responseCts,
            pauseDuringPlan,
            requestCts: pauseDuringPlan ? requestCts : null,
            requestTimeout: TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));

        // Heartbeat over the model-generation stretch: keep a ticking spinner alive (the
        // existing one is stopped between tool events) and advertise the escape hatch so a
        // slow/stalled turn never looks dead.
        _spinner.Start(spinnerMessage);

        try
        {
            var result = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await InvokeChatAsync(history, settings, responseCts, linkedCts.Token),
                _config.MaxRetryAttempts,
                retryOperationName,
                linkedCts.Token
            );

            ExtractAndRecordTokens(result, tokenLabel);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (responseCts.IsCancellationRequested)
        {
            throw new ModelStallException();
        }
        catch (OperationCanceledException)
        {
            throw new ModelCallTimeoutException();
        }
    }

    // ============================================================
    // MAF-side model call path (feat/agent-framework-migration — the live cutover). Mirrors
    // ExecuteModelCallAsync/InvokeChatAsync/AttachStallWatchdog exactly, but calls _agent
    // instead of _chatService/_kernel. _chatHistory (SK's ChatHistory) stays the sole mutable
    // source of truth — compaction, export/import, and pre-flight sizing all keep working
    // unmodified — converted to/from MEAI messages only around the actual model call, via
    // ChatHistoryConversion. No AgentSession/stateful accumulation: verified empirically that
    // AIAgent.RunAsync(IEnumerable<ChatMessage>) with no session is a pure function of whatever
    // list you pass, exactly like SK's GetChatMessageContentAsync(history, ...) today.
    //
    // ORIGINAL LIMITATION, since FIXED (see _lastCallPartialTrace below): SK's connector
    // mutates the passed-in ChatHistory with tool-call/result messages DURING a multi-round
    // tool-calling call, so a context-overflow failure mid-call still left the successful
    // earlier rounds in history for SynthesizeHistorySummary to recap. MAF's RunAsync is
    // atomic — on failure, nothing is returned, so ChatHistoryConversion has nothing to append.
    // Fixed by accumulating a partial trace from AgentFunctionMiddleware's per-call events
    // directly (those fire regardless of the outer call's fate) instead of relying on
    // ChatHistory mutation that MAF doesn't do. See ExecuteAgentModelCallAsync's trace
    // accumulator, and _lastCallPartialTrace's doc comment for where it's consumed.
    // ============================================================

    /// <summary>Carries an agent turn's result: the final text (pre-fallback-processing), every
    /// new message the turn produced (for appending to history), and the raw Ollama done-stream
    /// when reachable (for the length-cutoff check) — the MAF-side equivalent of the single
    /// ChatMessageContent ExecuteModelCallAsync returns.</summary>
    private sealed record AgentTurnResult(
        string Text,
        List<ChatMessageContent> NewHistoryMessages,
        OllamaSharp.Models.Chat.ChatDoneResponseStream? DoneStream);

    /// <summary>
    /// Appends an agent turn's messages to <paramref name="history"/>. Intermediate tool-call/
    /// tool-result messages are appended as-is; the trailing assistant-text message is REPLACED
    /// by <paramref name="finalText"/> (which may differ from the raw agent text if fallback
    /// parsing rewrote it) — mirroring SK's own split, where the connector mutates history with
    /// tool activity during the call but leaves the final text for the caller to add explicitly.
    /// </summary>
    private static void AppendAgentTurnToHistory(ChatHistory history, List<ChatMessageContent> newMessages, string finalText)
    {
        var toAppend = newMessages.Count > 0 && newMessages[^1].Role == AuthorRole.Assistant
            ? newMessages.Take(newMessages.Count - 1)
            : newMessages;

        foreach (var m in toAppend)
            history.Add(m);

        if (!string.IsNullOrEmpty(finalText))
            history.AddAssistantMessage(finalText);
    }

    /// <summary>MAF-side sibling of <see cref="ExecuteModelCallAsync"/> — same retry/watchdog/
    /// token-recording scaffolding, calling <see cref="_agent"/> instead of <see
    /// cref="_chatService"/>/<see cref="_kernel"/>.</summary>
    private async Task<AgentTurnResult> ExecuteAgentModelCallAsync(
        ChatHistory history,
        string retryOperationName,
        string tokenLabel,
        string spinnerMessage,
        bool pauseDuringPlan,
        CancellationToken cancellationToken)
    {
        using var requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));
        using var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCts.Token, responseCts.Token);

        using var watchdog = AttachAgentStallWatchdog(
            responseCts,
            pauseDuringPlan,
            requestCts: pauseDuringPlan ? requestCts : null,
            requestTimeout: TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));

        _spinner.Start(spinnerMessage);

        // Partial-trace accumulator: AgentFunctionMiddleware fires these events per tool call,
        // synchronously, regardless of whether the OUTER RunAsync call eventually succeeds or
        // throws — unlike SK's connector, MAF never mutates `history` mid-call, so on a failure
        // (e.g. context overflow mid multi-round tool use) there'd otherwise be NO record of
        // rounds that genuinely completed just before the failure. Reset on entry and cleared on
        // success (response.Messages is authoritative then); populated only when the call throws
        // AND at least one tool call actually completed. See ExecutePlanStepAsync/
        // CompactChatHistoryAsync for where _lastCallPartialTrace gets consumed.
        _lastCallPartialTrace = null;
        var partialTrace = new List<string>();
        var traceLock = new object();
        void OnTraceInvoked(FunctionCall call)
        {
            var args = call.Arguments.Count > 0
                ? string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 40)}"))
                : "";
            lock (traceLock) partialTrace.Add($"called {call.FunctionName}({args})");
        }
        void OnTraceCompleted(FunctionExecutionResult result)
        {
            lock (traceLock) partialTrace.Add($"{result.FunctionName} → {Truncate(result.Result, 80)}");
        }

        _agentFunctionMiddleware!.OnFunctionInvoked += OnTraceInvoked;
        _agentFunctionMiddleware.OnFunctionCompleted += OnTraceCompleted;

        try
        {
            try
            {
                try
                {
                    var meaiMessages = ChatHistoryConversion.ToMeaiMessages(history);

                    var response = await RetryPolicy.ExecuteWithRetryAsync(
                        async () => await InvokeAgentChatAsync(meaiMessages, responseCts, linkedCts.Token),
                        _config.MaxRetryAttempts,
                        retryOperationName,
                        linkedCts.Token
                    );

                    ExtractAndRecordAgentTokens(response, tokenLabel);

                    var doneStream = response.RawRepresentation is Microsoft.Extensions.AI.ChatResponse chatResponse
                        ? chatResponse.RawRepresentation as OllamaSharp.Models.Chat.ChatDoneResponseStream
                        : null;

                    return new AgentTurnResult(response.Text ?? "", ChatHistoryConversion.ToSkMessages(response.Messages), doneStream);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (responseCts.IsCancellationRequested)
                {
                    throw new ModelStallException();
                }
                catch (OperationCanceledException)
                {
                    throw new ModelCallTimeoutException();
                }
            }
            catch
            {
                // Catches everything that escapes the classification above: the rethrown/typed
                // exceptions from it, AND anything that matched none of those inner catches
                // (context overflow, HTTP failures) and would otherwise propagate unclassified.
                // Either way, if any tool call genuinely completed before the failure, preserve
                // that record rather than letting it vanish with the exception.
                lock (traceLock)
                {
                    if (partialTrace.Count > 0)
                        _lastCallPartialTrace = new List<string>(partialTrace);
                }
                throw;
            }
        }
        finally
        {
            _agentFunctionMiddleware.OnFunctionInvoked -= OnTraceInvoked;
            _agentFunctionMiddleware.OnFunctionCompleted -= OnTraceCompleted;
        }
    }

    /// <summary>MAF-side sibling of <see cref="InvokeChatAsync"/> — same streaming-mode routing
    /// and heartbeat contract, via <see cref="_agent"/> instead of <see cref="_chatService"/>.
    /// No session: called stateless, passing the full message list every time (see the class
    /// note above).</summary>
    private async Task<AgentResponse> InvokeAgentChatAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        CancellationTokenSource responseCts,
        CancellationToken linkedToken)
    {
        var useStreaming = _config.StreamingMode switch
        {
            ResponseStreamingMode.All => true,
            ResponseStreamingMode.Cloud => MandoCodeConfig.IsCloudModel(_config.GetEffectiveModelName()),
            _ => false // Off
        };

        if (!useStreaming)
            return await _agent!.RunAsync(messages, session: null, cancellationToken: linkedToken);

        var timeout = TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds);
        return await StreamBuffering.BufferAsync(
            _agent!.RunStreamingAsync(messages, session: null, cancellationToken: linkedToken),
            onChunk: () => { try { responseCts.CancelAfter(timeout); } catch (ObjectDisposedException) { } },
            linkedToken);
    }

    /// <summary>MAF-side sibling of <see cref="AttachStallWatchdog"/> — identical pause/resume
    /// contract, hooking <see cref="_agentFunctionMiddleware"/>'s events instead of <see
    /// cref="_functionFilter"/>'s. <see cref="_planHandoff"/> is unchanged and shared by both
    /// paths — it was already framework-agnostic.</summary>
    private IDisposable AttachAgentStallWatchdog(
        CancellationTokenSource responseCts,
        bool pauseDuringPlan = false,
        CancellationTokenSource? requestCts = null,
        TimeSpan requestTimeout = default)
    {
        var middleware = _agentFunctionMiddleware!;
        var timeout = TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds);

        var planActive = false;

        void Pause() { try { responseCts.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void Resume() { try { responseCts.CancelAfter(timeout); } catch (ObjectDisposedException) { } }

        void OnStarted() => Pause();
        void OnFinished() { if (!planActive && middleware.PendingFunctionCount == 0) Resume(); }

        middleware.OnFunctionStarted += OnStarted;
        middleware.OnFunctionFinished += OnFinished;

        void PauseRequest() { try { requestCts?.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void ResumeRequest() { try { requestCts?.CancelAfter(requestTimeout); } catch (ObjectDisposedException) { } }

        Action? onPlanStart = null, onPlanEnd = null;
        if (pauseDuringPlan && _planHandoff != null)
        {
            onPlanStart = () => { planActive = true; Pause(); PauseRequest(); };
            onPlanEnd = () => { planActive = false; Resume(); ResumeRequest(); };
            _planHandoff.ExecutionStarted += onPlanStart;
            _planHandoff.ExecutionFinished += onPlanEnd;
        }

        return new ActionDisposable(() =>
        {
            middleware.OnFunctionStarted -= OnStarted;
            middleware.OnFunctionFinished -= OnFinished;
            if (onPlanStart != null) _planHandoff!.ExecutionStarted -= onPlanStart;
            if (onPlanEnd != null) _planHandoff!.ExecutionFinished -= onPlanEnd;
        });
    }

    /// <summary>MAF-side sibling of <see cref="ExtractAndRecordTokens"/>. Uses MEAI's
    /// provider-agnostic <see cref="Microsoft.Extensions.AI.UsageDetails"/> for token counts
    /// (verified: matches Ollama's PromptEvalCount/EvalCount exactly) rather than digging into
    /// the raw Ollama type — a robustness improvement, since this keeps working even if the
    /// underlying connector ever changes. Generation-seconds timing has no generic MEAI
    /// equivalent, so that one piece still reaches into the raw done-stream when reachable.</summary>
    private void ExtractAndRecordAgentTokens(AgentResponse response, string label)
    {
        try
        {
            var usage = response.Usage;
            if (usage == null) return;

            var promptTokens = (int)(usage.InputTokenCount ?? 0);
            var completionTokens = (int)(usage.OutputTokenCount ?? 0);
            if (promptTokens <= 0 && completionTokens <= 0) return;

            double? generationSeconds = null;
            if (response.RawRepresentation is Microsoft.Extensions.AI.ChatResponse chatResponse
                && chatResponse.RawRepresentation is OllamaSharp.Models.Chat.ChatDoneResponseStream done
                && done.EvalDuration > 0)
            {
                generationSeconds = done.EvalDuration / 1_000_000_000.0;
            }

            _tokenTracker.RecordModelUsage(promptTokens, completionTokens, label, generationSeconds);
        }
        catch
        {
            // Token extraction is non-critical — never let it break the flow
        }
    }

    /// <summary>
    /// Routes a single model call to either the non-streaming API or a buffered streaming path,
    /// per <see cref="MandoCodeConfig.StreamingMode"/>: <c>All</c> streams every model, <c>Cloud</c>
    /// streams only cloud models (<see cref="MandoCodeConfig.IsCloudModel"/>), <c>Off</c> never
    /// streams. The buffered path resets the stall watchdog on every chunk — so a long-but-healthy
    /// generation never trips the watchdog — then returns a non-streaming-shaped result so the
    /// fallback parser, token recording, and every caller behave identically (the assembled text is
    /// the same either way, so a text-emitted tool call still reaches <see cref="FallbackFunctionCallExecutor"/>
    /// intact). Verified against the live Ollama connector before defaulting to <c>All</c> (the
    /// streaming spikes): structured auto-invoke (qwen2.5/glm-5.2), filter events, token metadata,
    /// and a text-emitted call surviving the stream intact (gemma3) all checked.
    /// </summary>
    private async Task<ChatMessageContent> InvokeChatAsync(
        ChatHistory history,
        OllamaPromptExecutionSettings settings,
        CancellationTokenSource responseCts,
        CancellationToken linkedToken)
    {
        var useStreaming = _config.StreamingMode switch
        {
            ResponseStreamingMode.All => true,
            ResponseStreamingMode.Cloud => MandoCodeConfig.IsCloudModel(_config.GetEffectiveModelName()),
            _ => false // Off
        };

        if (!useStreaming)
            return await _chatService.GetChatMessageContentAsync(history, settings, _kernel, linkedToken);

        // HEARTBEAT: each streamed chunk pushes the stall watchdog forward by the full budget,
        // so it can only fire on a genuine gap (no chunk for ModelResponseTimeoutSeconds). A tool
        // call mid-stream pauses the watchdog via the filter's OnFunctionStarted; the next content
        // chunk resumes the same budget, so the two compose. CancelAfter is thread-safe and a
        // no-op once disposed.
        var timeout = TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds);
        return await StreamBuffering.BufferAsync(
            _chatService.GetStreamingChatMessageContentsAsync(history, settings, _kernel, linkedToken),
            onChunk: () => { try { responseCts.CancelAfter(timeout); } catch (ObjectDisposedException) { } },
            linkedToken);
    }

    /// <summary>The stall watchdog fired: the model went silent (no tokens, no tool activity) past the per-call budget.</summary>
    private sealed class ModelStallException : Exception;

    /// <summary>The per-turn request-timeout ceiling (RequestTimeoutMinutes) was hit.</summary>
    private sealed class ModelCallTimeoutException : Exception;

    /// <summary>
    /// Attaches a stall watchdog to <paramref name="responseCts"/>: it fires after
    /// <see cref="MandoCodeConfig.ModelResponseTimeoutSeconds"/> of pure model-generation time.
    /// Tool calls — and the approval prompts that run inside them — PAUSE the watchdog: while any
    /// function is in flight the timer is disabled, so a long-running tool (e.g. a build) or a user
    /// deliberating at an approval prompt is never mistaken for a stalled model. The watchdog only
    /// counts contiguous stretches where the model is generating with no tool activity. Dispose the
    /// returned handle once the model call completes to detach the hooks.
    /// </summary>
    private IDisposable AttachStallWatchdog(
        CancellationTokenSource responseCts,
        bool pauseDuringPlan = false,
        CancellationTokenSource? requestCts = null,
        TimeSpan requestTimeout = default)
    {
        // Capture the filter locally so subscribe/unsubscribe target the same instance even if the
        // kernel is rebuilt mid-flight. CancelAfter is thread-safe and a no-op once disposed.
        var filter = _functionFilter;
        var timeout = TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds);

        // When a plan is running inside this call, suppress tool-event resumes — see below.
        var planActive = false;

        void Pause() { try { responseCts.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void Resume() { try { responseCts.CancelAfter(timeout); } catch (ObjectDisposedException) { } }

        // A function entering flight pauses the watchdog; the last one leaving flight resumes it.
        // PendingFunctionCount is already decremented before OnFunctionFinished fires, so a reading
        // of 0 means no tool is in flight. AllowConcurrentInvocation is handled: the count only hits
        // 0 when the final concurrent call completes.
        void OnStarted() => Pause();
        void OnFinished() { if (!planActive && filter.PendingFunctionCount == 0) Resume(); }

        filter.OnFunctionStarted += OnStarted;
        filter.OnFunctionFinished += OnFinished;

        // Outer chat turn only: the whole plan executes inside this single model call (the
        // propose_plan tool). Its steps legitimately generate large files with >timeout gaps
        // between tool calls, and this call's token is threaded into the plan — so if the watchdog
        // fired mid-plan it would cancel a step and surface as a bogus "Cancelled by user." Pause
        // it for the plan's entire duration; the plan's own per-step watchdogs cover stalls there.
        // (Plan-step watchdogs pass pauseDuringPlan=false: IsExecuting is already true for them, so
        // honoring it would pin them paused and disable their stall detection.)
        // The request-timeout ceiling (requestCts) ALSO wraps the whole plan and would
        // misfire the same way — a long (or thrashing) plan that crosses RequestTimeoutMinutes
        // got cancelled and mislabeled "Cancelled by user." So pause it for the plan too. Each
        // plan step still has its OWN request timeout, so steps stay bounded; only the redundant
        // outer ceiling is suspended. Resumed (fresh) for any post-plan model wrap-up.
        void PauseRequest() { try { requestCts?.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void ResumeRequest() { try { requestCts?.CancelAfter(requestTimeout); } catch (ObjectDisposedException) { } }

        Action? onPlanStart = null, onPlanEnd = null;
        if (pauseDuringPlan && _planHandoff != null)
        {
            onPlanStart = () => { planActive = true; Pause(); PauseRequest(); };
            onPlanEnd = () => { planActive = false; Resume(); ResumeRequest(); };
            _planHandoff.ExecutionStarted += onPlanStart;
            _planHandoff.ExecutionFinished += onPlanEnd;
        }

        return new ActionDisposable(() =>
        {
            filter.OnFunctionStarted -= OnStarted;
            filter.OnFunctionFinished -= OnFinished;
            if (onPlanStart != null) _planHandoff!.ExecutionStarted -= onPlanStart;
            if (onPlanEnd != null) _planHandoff!.ExecutionFinished -= onPlanEnd;
        });
    }

    /// <summary>Runs an action on Dispose. Used to detach stall-watchdog hooks deterministically.</summary>
    private sealed class ActionDisposable : IDisposable
    {
        private Action? _onDispose;
        public ActionDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    /// <summary>
    /// Formats HttpRequestException specifically. 401 is the most common "weird"
    /// connection failure — the daemon is running fine but the user got signed out
    /// of ollama.com. The default "Make sure Ollama is running: ollama serve"
    /// message misleads users into running `ollama serve` again, which then fails
    /// with "port already in use" because the daemon they're hitting is already up.
    /// </summary>
    private string FormatHttpFailure(HttpRequestException ex)
    {
        if (IsUnauthorizedError(ex))
        {
            // Brief — the auto-launched cloud sign-in walkthrough that fires right
            // after this response covers all the "what to do" guidance inline.
            return "<red>Error: Ollama returned 401 Unauthorized.</red>\n\n" +
                   "You're using a cloud model but the local Ollama daemon isn't authenticated.";
        }

        return "Error: Connection to Ollama failed.\n\n" +
               $"Details: {ex.Message}\n\n" +
               "What to do:\n" +
               "  • Make sure Ollama is running: ollama serve\n" +
               "  • Then type /retry to reconnect, OR\n" +
               "  • Run /setup to walk through setup again.";
    }

    private static bool IsUnauthorizedError(HttpRequestException ex)
        => ex.StatusCode == HttpStatusCode.Unauthorized
           || (ex.Message?.Contains("401") ?? false)
           || (ex.Message?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Formats error messages for display.
    /// </summary>
    private string FormatErrorMessage(Exception ex)
    {
        // 401 surfaces here too when the plan-step path rethrows as a generic Exception.
        if (ex is HttpRequestException http && IsUnauthorizedError(http))
            return FormatHttpFailure(http);
        if (ex.Message.Contains("401")
            || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            // Brief — App.razor auto-launches the sign-in walkthrough right after this.
            return "<red>Error: Ollama returned 401 Unauthorized.</red>\n\n" +
                   "You're using a cloud model but the local Ollama daemon isn't authenticated.";
        }

        // Request-too-big rejection — covers both context-window overflow and
        // transport-level 413 / "request body too large". Actionable message,
        // don't blame Ollama setup.
        if (IsContextOverflowError(ex))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' rejected the request because the payload was too large for its context window or transport limit.\n\n" +
                   $"Details: {ex.Message}\n\n" +
                   "What to do:\n" +
                   "  • Try /clear to start a fresh conversation, OR\n" +
                   $"  • Run /config and lower 'Max response tokens' (currently {_config.MaxTokens / 1024}k) — large limits eat into the context budget, OR\n" +
                   $"  • Lower the tool-result budget: /config set toolBudget 50000, OR\n" +
                   "  • Switch to a model with a larger context window via /config.";
        }

        // Check if the error is about tool support
        if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' does not support tool calling.\n\n" +
                   $"MandoCode uses agentic tool calling to read, write, and manage files.\n" +
                   $"Your current model doesn't support this — you'll need to switch to a tool-enabled model.\n\n" +
                   $"To change your model, run /config and select a model that supports tool use.\n\n" +
                   $"Cloud models (no GPU required):\n" +
                   $"  • glm-5.2:cloud\n" +
                   $"  • minimax-m3:cloud\n" +
                   $"  • kimi-k2.7-code:cloud\n\n" +
                   $"Local models:\n" +
                   $"  • qwen3:8b (recommended, runs on most hardware)\n" +
                   $"  • qwen2.5-coder:7b\n" +
                   $"  • mistral\n" +
                   $"  • llama3.1";
        }

        return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}\n\nOr run /setup to walk through setup again.";
    }

    /// <summary>
    /// Executes a single step of a task plan with function calling enabled.
    /// Uses previous step results as context for continuity.
    /// </summary>
    /// <summary>
    /// Builds the system-prompt context a plan step's fresh chat history is seeded with.
    /// Includes the verbatim user request that produced the plan: steps otherwise only
    /// see the model's distilled goal, which drops details like target folders (observed
    /// live: "@STarfox/ create a game…" → goal "create a game…" → every step wrote to the
    /// project root). Previous-step results are limited to the last 2 and the original
    /// request is capped so step context stays small on local models.
    /// </summary>
    public static string BuildStepContext(string systemPrompt, string? originalUserRequest, List<string> previousResults)
    {
        // Generous enough for a long message plus @folder listings; small enough that a
        // pasted @file of several thousand lines can't flood every step's context. Paths
        // and intent come first in a prompt, so head-truncation keeps what steps need.
        const int MaxOriginalRequestChars = 4000;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(systemPrompt);

        if (!string.IsNullOrWhiteSpace(originalUserRequest))
        {
            var request = originalUserRequest.Trim();
            if (request.Length > MaxOriginalRequestChars)
                request = request[..MaxOriginalRequestChars] + "\n…[truncated]";

            sb.AppendLine("\n--- The User's Original Request ---");
            sb.AppendLine(request);
            sb.AppendLine("--- End of Original Request ---");
            sb.AppendLine("This step is part of a plan fulfilling the request above. The request is " +
                          "authoritative for WHERE work happens: target folders and file paths mentioned " +
                          "in it (including attached @folder/@file references) override any unqualified " +
                          "paths in the step instruction.");
        }

        var recentResults = previousResults.Count > 2
            ? previousResults.Skip(previousResults.Count - 2).ToList()
            : previousResults;

        if (recentResults.Any())
        {
            sb.AppendLine("\n--- Results from Previous Steps ---");
            foreach (var result in recentResults)
            {
                sb.AppendLine(result);
            }
            sb.AppendLine("--- End of Previous Steps ---\n");
        }

        return sb.ToString();
    }

    public async Task<string> ExecutePlanStepAsync(string stepInstruction, List<string> previousResults, CancellationToken cancellationToken = default)
    {
        var contextBuilder = new System.Text.StringBuilder(
            BuildStepContext(_systemPrompt, _currentTurnUserMessage, previousResults));

        // Create a temporary chat history for this step
        var stepHistory = new ChatHistory(contextBuilder.ToString());
        stepHistory.AddUserMessage($"Execute this step now: {stepInstruction}\n\nRemember: Use the available functions to complete this task. Do not describe the function call - actually invoke it.");

        var stepLabel = $"Step {previousResults.Count + 1}";
        var combined = new System.Text.StringBuilder();
        int continuations = 0;

        while (true)
        {
            string processedResponse = "";
            bool needsContinuation = false;
            bool contextOverflowRecovery = false;

            // Each continuation gets a fresh scope so the budget and dedup-set reset.
            using (var scope = _agentFunctionMiddleware!.BeginScope())
            {
                try
                {
                    var result = await ExecuteAgentModelCallAsync(
                        stepHistory,
                        retryOperationName: "ExecutePlanStepAsync",
                        tokenLabel: stepLabel,
                        spinnerMessage: $"Working on {stepLabel} — press Esc to cancel",
                        pauseDuringPlan: false,
                        cancellationToken);

                    var response = string.IsNullOrEmpty(result.Text) ? "Step completed (no response content)." : result.Text;

                    await _completionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(5));

                    processedResponse = _config.EnableFallbackFunctionParsing
                        ? await _fallbackExecutor.ProcessAsync(response, _kernel, _config.GetEffectiveModelName())
                        : response;

                    // Mirrors where SK's connector already mutated stepHistory with this round's
                    // tool-call/result messages during the call — see the class-level note above
                    // ExecuteAgentModelCallAsync for why this must happen even on a plain,
                    // non-continuing step (a LATER continuation or context-overflow recovery
                    // within this same loop needs the full trace).
                    AppendAgentTurnToHistory(stepHistory, result.NewHistoryMessages, processedResponse);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Step cancelled.", cancellationToken);
                }
                catch (ModelStallException)
                {
                    // Stall watchdog fired — the model went quiet for longer than the per-call
                    // budget. Almost always a local model stalling as context grows.
                    throw new Exception(
                        $"The model stopped responding for {_config.ModelResponseTimeoutSeconds}s and was stopped by the stall watchdog. " +
                        "This usually means a local model stalled as context grew. Try a smaller step, /clear to trim history, " +
                        "or raise the watchdog: /config set modelResponseTimeout 300.");
                }
                catch (ModelCallTimeoutException)
                {
                    throw new Exception("Step execution timed out. Try breaking this step into smaller parts.");
                }
                // Provider-side context-window rejection — recoverable via synthetic-summary restart.
                catch (Exception ex) when (IsContextOverflowError(ex)
                                            && _config.EnableAutoContinuation
                                            && continuations < _config.MaxAutoContinuations)
                {
                    contextOverflowRecovery = true;
                }
                catch (HttpRequestException ex)
                {
                    throw new Exception($"Connection to Ollama failed: {ex.Message}");
                }

                // Decide whether to auto-continue (while scope is still live so BudgetExhausted reads correctly).
                if (!contextOverflowRecovery
                    && scope.BudgetExhausted
                    && _config.EnableAutoContinuation
                    && continuations < _config.MaxAutoContinuations)
                {
                    needsContinuation = true;
                }

                // User picked "Cancel plan" from a diff approval mid-step — the filter set the flag.
                // Throw here so ExecutePlanAsync treats it as plan-level cancellation, not just step-level.
                if (scope.PlanCancellationRequested)
                    throw new PlanCancellationRequestedException();
            }

            // Context-overflow recovery: the turn never completed, so the model produced no
            // summary of its own. We build one from the tool-call trace and restart the step
            // with a fresh history seeded by that summary.
            if (contextOverflowRecovery)
            {
                // Prefer the partial trace ExecuteAgentModelCallAsync accumulated during the
                // just-failed call: MAF's atomic RunAsync means stepHistory itself was never
                // touched (unlike SK's connector, which mutated it round by round), so
                // SynthesizeHistorySummary would otherwise find nothing and report "(no prior
                // activity captured)" even when several tool calls genuinely completed moments
                // before the failure.
                var summary = _lastCallPartialTrace is { Count: > 0 }
                    ? string.Join("\n", _lastCallPartialTrace)
                    : SynthesizeHistorySummary(stepHistory);
                continuations++;

                combined.AppendLine();
                combined.AppendLine($"⚠ Provider rejected request (context window full). Restarting step with a compacted summary ({continuations}/{_config.MaxAutoContinuations}).");
                combined.AppendLine();

                stepHistory = new ChatHistory(contextBuilder.ToString());
                stepHistory.AddUserMessage(
                    $"Execute this step: {stepInstruction}\n\n" +
                    $"A previous attempt hit the provider's context-window limit and was aborted. " +
                    $"Here's what was partially completed (tool-call trace; do NOT redo these):\n\n{summary}\n\n" +
                    $"Continue from where it left off. Use the available functions to finish the step.");

                continue;
            }

            combined.AppendLine(processedResponse);

            if (!needsContinuation)
                return combined.ToString().TrimEnd();

            continuations++;
            combined.AppendLine();
            combined.AppendLine($"⟳ Auto-continuing ({continuations}/{_config.MaxAutoContinuations}) — tool budget reset.");
            combined.AppendLine();

            // The turn's own messages (including this response) were already appended above via
            // AppendAgentTurnToHistory — just add the nudge to keep going.
            stepHistory.AddUserMessage(
                "Continue from where you left off. Your previous response was a progress summary; " +
                "the tool-call budget has been reset, so call tools again to finish this step.");
        }
    }

    /// <summary>
    /// Matches provider-side context-window rejections. Delegates to <see cref="RetryPolicy.IsContextOverflowError"/>
    /// so the retry policy and recovery path agree on exactly which errors skip retries and route to recovery.
    /// </summary>
    private static bool IsContextOverflowError(Exception? ex) => RetryPolicy.IsContextOverflowError(ex);

    /// <summary>
    /// Walks a chat history and produces a compact recap the next turn can read as
    /// "what's happened so far." Aggressively truncates content so the summary itself
    /// doesn't reintroduce the overflow. Start/end indices let callers exclude messages
    /// they're about to re-seed fresh (e.g. the system prompt or current user message).
    ///
    /// Tool-call activity often lives in <see cref="ChatMessageContent.Items"/> rather than
    /// <c>Content</c> — SK's auto-invoke loop puts function calls/results there. Skipping
    /// Items would drop most of the trace on a context-overflow recovery. We walk both.
    /// </summary>
    private static string SynthesizeHistorySummary(
        ChatHistory history,
        int startIndex = 2,
        int? endIndexExclusive = null,
        int maxChars = 1500)
    {
        var sb = new System.Text.StringBuilder();
        var end = endIndexExclusive ?? history.Count;
        for (int i = Math.Max(0, startIndex); i < end; i++)
        {
            var msg = history[i];
            var role = msg.Role.Label;
            var line = FormatMessageForSummary(msg);
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Length > 180) line = line[..180] + "...";
            sb.Append('[').Append(role).Append("] ").AppendLine(line);

            if (sb.Length > maxChars)
            {
                sb.AppendLine("... (older entries truncated)");
                break;
            }
        }
        return sb.Length == 0 ? "(no prior activity captured)" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Produces a one-line recap of a single chat message for the summary walker.
    /// Falls back to <c>Items</c> (function calls / results) when <c>Content</c> is empty.
    /// </summary>
    private static string FormatMessageForSummary(Microsoft.SemanticKernel.ChatMessageContent msg)
    {
        var content = msg.Content?.Trim();
        if (!string.IsNullOrEmpty(content)) return content;

        if (msg.Items == null || msg.Items.Count == 0) return "";

        var parts = new List<string>();
        foreach (var item in msg.Items)
        {
            switch (item)
            {
                case Microsoft.SemanticKernel.FunctionCallContent fc:
                {
                    var args = fc.Arguments != null && fc.Arguments.Count > 0
                        ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 40)}"))
                        : "";
                    parts.Add($"called {fc.FunctionName}({args})");
                    break;
                }
                case Microsoft.SemanticKernel.FunctionResultContent fr:
                {
                    var resultText = fr.Result?.ToString() ?? "";
                    parts.Add($"{fr.FunctionName} → {Truncate(resultText, 80)}");
                    break;
                }
                case Microsoft.SemanticKernel.TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                    parts.Add(tc.Text.Trim());
                    break;
            }
        }
        return parts.Count == 0 ? "" : string.Join("; ", parts);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// Collapses the persistent <see cref="_chatHistory"/> into system prompt + recap +
    /// last user message. Used when a direct chat turn hits a provider context-window
    /// rejection — we compact the conversation so the next retry fits.
    /// </summary>
    // ============================================================
    // History persistence — full-fidelity session export/restore
    // ============================================================
    // Consumed by hosts that persist sessions across process restarts (MandoCode.Desktop's
    // session restore; a future CLI --continue). Uses Semantic Kernel's own polymorphic
    // content serialization, so assistant turns that carried function calls/results
    // round-trip too — a restored model genuinely remembers what it read and did, rather
    // than being briefed about it.

    /// <summary>
    /// Serializes the conversation — everything except the system prompt — to JSON.
    /// Null when there is nothing beyond the system prompt or serialization fails;
    /// callers treat null as "nothing to persist".
    /// </summary>
    public string? ExportHistoryJson()
    {
        try
        {
            var messages = _chatHistory.Where(m => m.Role != AuthorRole.System).ToList();
            return messages.Count == 0 ? null : JsonSerializer.Serialize(messages);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restores a previously exported conversation into the live history, after the current
    /// system prompt. Intended for a FRESH session right after construction — it appends,
    /// never replaces. Returns the number of messages restored; 0 means nothing usable
    /// (corrupt/foreign JSON), and callers should fall back to lighter re-brief mechanisms.
    /// </summary>
    public int TryRestoreHistoryJson(string json)
    {
        try
        {
            var messages = JsonSerializer.Deserialize<List<ChatMessageContent>>(json);
            if (messages == null) return 0;

            var restored = 0;
            foreach (var message in messages)
            {
                if (message?.Role is null || message.Role == AuthorRole.System) continue;
                _chatHistory.Add(message);
                restored++;
            }
            return restored;
        }
        catch
        {
            return 0;
        }
    }

    private async Task CompactChatHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            int lastUserIdx = -1;
            for (int i = _chatHistory.Count - 1; i >= 0; i--)
            {
                if (_chatHistory[i].Role == AuthorRole.User)
                {
                    lastUserIdx = i;
                    break;
                }
            }
            if (lastUserIdx < 1) return; // Nothing to compact.

            var lastUserContent = _chatHistory[lastUserIdx].Content ?? "";

            // Summarize everything between the system prompt (0) and the current user turn.
            var recap = SynthesizeHistorySummary(_chatHistory, startIndex: 1, endIndexExclusive: lastUserIdx);

            // The just-failed turn's own tool calls (if any completed before the overflow) are
            // NOT in _chatHistory yet — MAF's atomic RunAsync never touched it — so append them
            // separately rather than losing that work. See _lastCallPartialTrace's doc comment.
            if (_lastCallPartialTrace is { Count: > 0 })
            {
                recap = string.IsNullOrWhiteSpace(recap) || recap == "(no prior activity captured)"
                    ? string.Join("\n", _lastCallPartialTrace)
                    : recap + "\n" + string.Join("\n", _lastCallPartialTrace);
            }

            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(_systemPrompt);
            if (!string.IsNullOrWhiteSpace(recap) && recap != "(no prior activity captured)")
            {
                _chatHistory.AddUserMessage(
                    "[Prior conversation recap — the previous attempt hit the provider's context-window limit and was compacted:]\n" +
                    recap);
            }
            _chatHistory.AddUserMessage(lastUserContent);
        }
        finally { _historyLock.Release(); }
    }

    /// <summary>
    /// Extracts real token counts from a ChatMessageContent response and records them.
    /// Non-critical — failures are silently swallowed.
    /// </summary>
    private void ExtractAndRecordTokens(ChatMessageContent response, string label)
    {
        try
        {
            if (response.InnerContent is OllamaSharp.Models.Chat.ChatDoneResponseStream done)
            {
                var promptTokens = done.PromptEvalCount;
                var completionTokens = done.EvalCount;
                if (promptTokens > 0 || completionTokens > 0)
                {
                    // EvalDuration is in nanoseconds — convert to seconds
                    double? generationSeconds = done.EvalDuration > 0
                        ? done.EvalDuration / 1_000_000_000.0
                        : null;

                    _tokenTracker.RecordModelUsage(promptTokens, completionTokens, label, generationSeconds);
                }
            }
        }
        catch
        {
            // Token extraction is non-critical — never let it break the flow
        }
    }

    /// <summary>
    /// Exposes the token tracker for external consumers (e.g., App.razor display).
    /// </summary>
    public TokenTrackingService TokenTracker => _tokenTracker;

    /// <summary>
    /// Enters learn mode by clearing history and injecting the educator system prompt.
    /// The user can return to normal mode via /clear which restores the original system prompt.
    /// </summary>
    public async Task EnterLearnModeAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(SystemPrompts.LearnModePrompt);
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Clears the chat history and starts a new conversation.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(_systemPrompt);
            _functionFilter.ClearCache();
            _tokenTracker.Reset();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Gets the current chat history.
    /// </summary>
    // Public but currently uncalled anywhere in the repo (confirmed by a repo-wide search) —
    // flagged by the migration survey as an SK type (ChatMessageContent) leaking out of
    // AIService's public API regardless. Left as-is: this is still the live representation
    // (_chatHistory), and it costs nothing to leave a public method around that nothing calls
    // yet. See GetHistoryAsMeaiMessagesAsync below for the MAF-side sibling
    // (feat/agent-framework-migration, Phase 5) — once the live cutover happens and _chatHistory
    // itself goes away, this method is deleted alongside it rather than converted in place.
    public async Task<IReadOnlyList<ChatMessageContent>> GetHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            return _chatHistory.ToList().AsReadOnly();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// MAF-side sibling of <see cref="GetHistoryAsync"/> — returns the same conversation via
    /// <see cref="ChatHistoryConversion.ToMeaiMessages"/> instead of SK's <see
    /// cref="ChatMessageContent"/>. Reads from the same <see cref="_chatHistory"/> (there's no
    /// separate MAF-native history yet — <see cref="_agent"/> isn't live), so this is a
    /// translation view, not an independent source of truth.
    /// </summary>
    public async Task<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> GetHistoryAsMeaiMessagesAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            return ChatHistoryConversion.ToMeaiMessages(_chatHistory).AsReadOnly();
        }
        finally
        {
            _historyLock.Release();
        }
    }
}
