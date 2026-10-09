using System.ComponentModel;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Covers the one behavior AIService.BuildAgent's NamedTool helper relies on that the compiler
/// can't check: AIFunctionFactoryOptions.Name actually overriding the default method-name-derived
/// tool name. The snake_case tool name lives only in BuildAgent's NamedTool call now (the plugins'
/// old [KernelFunction] attributes were removed once SK was fully cleaned up), so a silent Name
/// override failure would desync the MAF-side tool name from the system prompt/skills that
/// reference the snake_case name, without any compile error to catch it. See
/// feat/agent-framework-migration, Phase 2.
/// </summary>
[Trait("Category", "Unit")]
public class AgentToolNamingTests
{
    [Description("A method whose real name should never reach the model.")]
    private static string DoNotUseThisName() => "ok";

    [Fact]
    [Trait("Behavior", "DependencyContract")]
    public void NamedTool_style_creation_overrides_the_default_method_derived_name()
    {
        AIFunction function = AIFunctionFactory.Create(
            DoNotUseThisName,
            new AIFunctionFactoryOptions { Name = "list_all_project_files" });

        Assert.Equal("list_all_project_files", function.Name);
        Assert.NotEqual(nameof(DoNotUseThisName), function.Name);
    }

    [Fact]
    public void EngineRegistersTheToolNamesReferencedByItsPrompts()
    {
        var config = new MandoCode.Models.MandoCodeConfig { AllowPersistence = false };
        var root = new MandoCode.Services.ProjectRootAccessor(Path.GetTempPath());
        var ai = new MandoCode.Services.AIService(root, config,
            new MandoCode.Services.TokenTrackingService(), new MandoCode.Services.PlanHandoff(),
            new MandoCode.Services.SkillLoader(config, root), new MandoCode.Services.McpClientManager(config),
            new MandoCode.Services.McpApprovalGate(config), new MandoCode.Services.SpinnerService());
        var tools = (IEnumerable<AIFunction>)typeof(MandoCode.Services.AIService)
            .GetField("_agentFunctions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ai)!;
        var names = tools.Select(tool => tool.Name).ToArray();
        Assert.Contains("list_all_project_files", names);
        Assert.Contains("read_file_contents", names);
        Assert.Contains("execute_command", names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
