using System.Reflection;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public class ExplicitPlanningTests
{
    private static AIService Create()
    {
        var config = new MandoCodeConfig { EnableTaskPlanning = true, AllowPersistence = false };
        var root = new ProjectRootAccessor(Path.GetTempPath());
        return new AIService(root, config, new TokenTrackingService(), new PlanHandoff(),
            new SkillLoader(config, root), new McpClientManager(config), new McpApprovalGate(config), new SpinnerService());
    }

    private static IEnumerable<AIFunction> Tools(AIService ai) =>
        (IEnumerable<AIFunction>)typeof(AIService).GetField("_agentFunctions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;

    [Fact]
    public void DesktopHostsRetainModelInitiatedPlanning()
    {
        var ai = Create();
        Assert.Contains(Tools(ai), tool => tool.Name == "propose_plan");
    }

    [Fact]
    public void CliNormalToolsAndPromptExcludeAutomaticPlanningAcrossRebuilds()
    {
        var ai = Create();
        ai.UseExplicitPlanningOnly();
        ai.SetHostTools([]); // Model/config/tool rebuilds must not re-enable planning.
        Assert.DoesNotContain(Tools(ai), tool => tool.Name == "propose_plan");
        Assert.Contains(Tools(ai), tool => tool.Name == "read_file_contents");
        var prompt = (string)typeof(AIService).GetField("_systemPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ai)!;
        Assert.Contains("Only the user's /plan command", prompt);
        Assert.DoesNotContain("call the propose_plan function BEFORE", prompt);
        Assert.DoesNotContain("use propose_plan (see MULTI-STEP", prompt);
    }
}
