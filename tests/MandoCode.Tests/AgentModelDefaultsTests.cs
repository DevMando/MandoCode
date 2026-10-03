using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class AgentModelDefaultsTests
{
    [Fact]
    public async Task ModelSelection_OnlyOneConcurrentFlowCanEnterPerAgent()
    {
        var gate = new AgentModelSelectionGate();
        var entered = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(gate.TryEnter)));
        Assert.Single(entered, value => value);
        Assert.True(new AgentModelSelectionGate().TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void AgentOnlySwitch_DoesNotChangeModelForNewAgents()
    {
        var saved = new MandoCodeConfig { ModelName = "original:cloud", AllowPersistence = false };
        var defaults = new AgentModelDefaults(saved);
        var current = new MandoCodeConfig { ModelName = "agent-only:cloud" };
        var next = new MandoCodeConfig { ModelName = current.ModelName };
        defaults.Apply(next);
        Assert.Equal("original:cloud", next.ModelName);
        Assert.Equal("agent-only:cloud", current.ModelName);
    }

    [Fact]
    public void SelectingDefault_UpdatesFutureAgentsButPreservesExistingModels()
    {
        var saved = new MandoCodeConfig { ModelName = "original:cloud", AllowPersistence = false };
        var defaults = new AgentModelDefaults(saved);
        var existing = new MandoCodeConfig { ModelName = "other:cloud" };
        defaults.Select("selected:cloud", new[] { existing });
        var next = new MandoCodeConfig { ModelName = existing.ModelName };
        defaults.Apply(next);
        Assert.Equal("selected:cloud", next.ModelName);
        Assert.Equal("other:cloud", existing.ModelName);
        Assert.Equal("selected:cloud", existing.DefaultAgentModel);
    }

    [Fact]
    public void SavedDefault_SurvivesAnAgentSavingItsOwnModel()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var config = new MandoCodeConfig { ModelName = "agent-only:cloud", DefaultAgentModel = "default:cloud" };
            config.Save(path);
            var loaded = MandoCodeConfig.Load(path);
            var next = new MandoCodeConfig();
            new AgentModelDefaults(loaded).Apply(next);
            Assert.Equal("default:cloud", next.ModelName);
        }
        finally { File.Delete(path); }
    }
}
