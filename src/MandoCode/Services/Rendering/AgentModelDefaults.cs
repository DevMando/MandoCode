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
        var defaults = Snapshot();
        new AgentSettingsDraft(defaults).ApplyTo(agent);
        var previous = agent.GetEffectiveModelName();
        agent.DefaultAgentModel = defaults.DefaultAgentModel ?? defaults.GetEffectiveModelName();
        agent.ModelName = agent.DefaultAgentModel;
        agent.ModelPath = null;
        agent.ApplyRecommendedContextLength(previous, agent.ModelName);
    }

    public MandoCodeConfig Snapshot()
    {
        var saved = new AgentSettingsDraft(configuration).Config;
        AgentSettingsDraft.Restore(saved, saved.DefaultAgentOptions);
        saved.ModelName = saved.DefaultAgentModel ?? saved.GetEffectiveModelName();
        saved.ModelPath = null;
        return saved;
    }

    public bool IsCustomized(MandoCodeConfig agent)
    {
        var defaults = Snapshot();
        return agent.GetEffectiveModelName() != defaults.GetEffectiveModelName()
            || AgentSettingsDraft.Fields.Any(field => field.Read(agent) != field.Read(defaults));
    }

    public void Promote(MandoCodeConfig agent, IEnumerable<MandoCodeConfig> agents)
    {
        var defaults = Snapshot();
        new AgentSettingsDraft(agent).ApplyTo(defaults);
        defaults.DefaultAgentOptions = AgentSettingsDraft.Capture(defaults);
        defaults.DefaultAgentModel = defaults.ModelName = agent.GetEffectiveModelName();
        defaults.AllowPersistence = configuration.AllowPersistence;
        defaults.Save();
        new AgentSettingsDraft(defaults).ApplyTo(configuration);
        configuration.DefaultAgentModel = configuration.ModelName = defaults.ModelName;
        configuration.DefaultAgentOptions = new(defaults.DefaultAgentOptions);
        configuration.ModelPath = null;
        foreach (var existing in agents)
        {
            existing.DefaultAgentModel = defaults.ModelName;
            existing.DefaultAgentOptions = new(defaults.DefaultAgentOptions);
        }
    }

    public void UpdateSettings(MandoCodeConfig source, IEnumerable<MandoCodeConfig> agents)
    {
        new AgentSettingsDraft(source).ApplyTo(configuration);
        configuration.DefaultAgentOptions = AgentSettingsDraft.Capture(source);
        source.DefaultAgentOptions = new(configuration.DefaultAgentOptions);
        foreach (var existing in agents) existing.DefaultAgentOptions = new(configuration.DefaultAgentOptions);
    }

    public void UpdateSetting(string key, string value, IEnumerable<MandoCodeConfig> agents)
    {
        var defaults = Snapshot();
        if (!ConfigKeySetter.TrySet(defaults, key, value).Ok) return;
        var existing = agents.ToArray();
        UpdateSettings(defaults, existing);
        if (key.Equals("model", StringComparison.OrdinalIgnoreCase) || key.Equals("modelName", StringComparison.OrdinalIgnoreCase))
        {
            configuration.ModelName = configuration.DefaultAgentModel = defaults.GetEffectiveModelName();
            foreach (var agent in existing) agent.DefaultAgentModel = configuration.DefaultAgentModel;
        }
    }
}
