using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>Shares the model preference without changing running agents.</summary>
public sealed class AgentModelDefaults(MandoCodeConfig configuration)
{
    public string Model => configuration.DefaultAgentModel ?? configuration.GetEffectiveModelName();

    public void Select(string model, IEnumerable<MandoCodeConfig> agents)
    {
        configuration.DefaultAgentModel = model;
        configuration.ModelName = model;
        configuration.ModelPath = null;
        foreach (var agent in agents) agent.DefaultAgentModel = model;
        configuration.Save();
    }

    public void Apply(MandoCodeConfig agent)
    {
        var previous = agent.GetEffectiveModelName();
        agent.DefaultAgentModel = Model;
        agent.ModelName = Model;
        agent.ModelPath = null;
        agent.ApplyRecommendedContextLength(previous, Model);
    }
}
