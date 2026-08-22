// AgentPreview — a KEPT interactive tool (not a throwaway spike) for hands-on testing of
// feat/agent-framework-migration. Drives the REAL production classes: FileSystemPlugin,
// PlanningPlugin, SkillsPlugin, and AgentFunctionMiddleware — not a reimplementation. Only the
// wiring (tool list assembly, agent construction) is duplicated from AIService.BuildAgent(),
// since those fields are private; if BuildAgent's tool list changes, update the list below too.
//
// Usage:
//   dotnet run --project tools/AgentPreview                     (sandboxed scratch directory)
//   dotnet run --project tools/AgentPreview -- --project <path> (point at a real directory)
//   dotnet run --project tools/AgentPreview -- --model <name>   (default: qwen2.5:1.5b)
//
// Type prompts at the "You:" prompt. Ctrl+C or "exit" to quit. Approval prompts print a diff
// preview and read y/n/s (skip = reject with "user rejected, try something else") from stdin.

using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

string? projectDir = null;
var model = "qwen2.5:1.5b";
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--project" && i + 1 < args.Length) projectDir = args[++i];
    else if (args[i] == "--model" && i + 1 < args.Length) model = args[++i];
}

var sandboxed = projectDir == null;
projectDir ??= Path.Combine(Path.GetTempPath(), "mandocode-agent-preview-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(projectDir);
if (sandboxed)
{
    await File.WriteAllTextAsync(Path.Combine(projectDir, "hello.txt"), "hello from AgentPreview\n");
    Console.WriteLine($"[sandboxed scratch dir — pass --project <path> for a real one] {projectDir}");
}
else
{
    Console.WriteLine($"[REAL project dir — writes/deletes here are real] {projectDir}");
}

Console.WriteLine($"[model] {model}");
Console.WriteLine();

var projectRootAccessor = new ProjectRootAccessor(projectDir);
var spinner = new SpinnerService();

var middleware = new AgentFunctionMiddleware(defaultDeduplicationWindowSeconds: 5, projectRootAccessor);
middleware.OnWriteApprovalRequested = (path, oldContent, newContent) => AskDiffApproval("write/edit", path, oldContent, newContent);
middleware.OnDeleteApprovalRequested = (path, existingContent) => AskDiffApproval("delete", path, existingContent, null);
middleware.OnCommandApprovalRequested = command => AskCommandApproval(command);
middleware.OnFunctionInvoked += call => Console.WriteLine($"  → {call.FunctionName}: {call.Description}");
middleware.OnFunctionCompleted += result => Console.WriteLine($"  ← {result.FunctionName} [{(result.Success ? "ok" : "FAILED")}] {Truncate(result.Result, 150)}");

var fileSystemPlugin = new FileSystemPlugin(projectRootAccessor, spinner);
var skillLoader = new SkillLoader(new MandoCodeConfig(), projectRootAccessor);
var skillsPlugin = new SkillsPlugin(skillLoader);
var planningPlugin = new PlanningPlugin();

AIFunction Named(Delegate d, string name) => AIFunctionFactory.Create(d, new AIFunctionFactoryOptions { Name = name });

List<AITool> tools =
[
    Named(fileSystemPlugin.ListAllProjectFiles, "list_all_project_files"),
    Named(fileSystemPlugin.ListFiles, "list_files_match_glob_pattern"),
    Named(fileSystemPlugin.ReadFile, "read_file_contents"),
    Named(fileSystemPlugin.CreateFolder, "create_folder"),
    Named(fileSystemPlugin.WriteFile, "write_file"),
    Named(fileSystemPlugin.EditFile, "edit_file"),
    Named(fileSystemPlugin.GrepFiles, "grep_files"),
    Named(fileSystemPlugin.DeleteFile, "delete_file"),
    Named(fileSystemPlugin.DeleteFolder, "delete_folder"),
    Named(fileSystemPlugin.FindInFiles, "search_text_in_files"),
    Named(fileSystemPlugin.GetAbsolutePath, "get_absolute_path"),
    Named(fileSystemPlugin.ExecuteCommand, "execute_command"),
    Named(planningPlugin.ProposePlan, "propose_plan"), // no PlanHandoff wired — runs as a plain tool, returns its sentinel
    Named(skillsPlugin.LoadSkill, "load_skill"),
    // WebSearchPlugin intentionally omitted — needs a real Tavily key this tool won't prompt for.
];

var ollamaClient = new OllamaApiClient(new Uri("http://localhost:11434"), model);
var baseAgent = ollamaClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "AgentPreview",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a coding assistant with tool access. Use tools directly rather than describing what you would do.",
        Tools = tools,
    },
});
var agent = baseAgent.AsBuilder().Use(middleware.InterceptAsync).Build();

var session = await agent.CreateSessionAsync();

Console.WriteLine();
Console.WriteLine("Ready. Type a prompt, or 'exit' to quit.");
while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();
    if (input == null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (input.Trim().Length == 0) continue;

    using var scope = middleware.BeginScope();
    try
    {
        var response = await agent.RunAsync(input, session);
        Console.WriteLine($"\nAgent: {response.Text}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine("Bye.");
return;

static Task<DiffApprovalResult> AskDiffApproval(string kind, string path, string? before, string? after)
{
    Console.WriteLine();
    Console.WriteLine($"--- approval requested: {kind} '{path}' ---");
    if (before != null) Console.WriteLine($"before:\n{Truncate(before, 500)}");
    if (after != null) Console.WriteLine($"after:\n{Truncate(after, 500)}");
    Console.Write("Approve? (y/n/s=skip-with-new-instructions): ");
    var answer = (Console.ReadLine() ?? "n").Trim().ToLowerInvariant();

    return Task.FromResult(answer switch
    {
        "y" => new DiffApprovalResult { Response = DiffApprovalResponse.Approved },
        "s" => AskNewInstructions(),
        _ => new DiffApprovalResult { Response = DiffApprovalResponse.Denied }
    });
}

static DiffApprovalResult AskNewInstructions()
{
    Console.Write("New instructions for the model: ");
    var msg = Console.ReadLine() ?? "";
    return new DiffApprovalResult { Response = DiffApprovalResponse.NewInstructions, UserMessage = msg };
}

static Task<DiffApprovalResult> AskCommandApproval(string command)
{
    Console.WriteLine();
    Console.WriteLine($"--- approval requested: execute_command '{command}' ---");
    Console.Write("Approve? (y/n): ");
    var answer = (Console.ReadLine() ?? "n").Trim().ToLowerInvariant();
    return Task.FromResult(new DiffApprovalResult
    {
        Response = answer == "y" ? DiffApprovalResponse.Approved : DiffApprovalResponse.Denied
    });
}

static string Truncate(string? s, int max)
{
    s ??= "";
    return s.Length <= max ? s : s[..max] + "...";
}
