using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public class AgentSettingsDraftTests
{
    [Fact]
    public void EditsAreStaged_AndApplyOnlyToTheirTargetAgent()
    {
        var agent = new MandoCodeConfig { Temperature = 0.2, AgentName = "Jetik", DefaultAgentModel = "default:cloud" };
        var other = new MandoCodeConfig { Temperature = 0.3 };
        var draft = new AgentSettingsDraft(agent);
        var field = AgentSettingsDraft.Fields.Single(f => f.Key == "temperature");
        Assert.True(draft.Set(field, 0.7.ToString()).Ok);
        Assert.Equal(0.2, agent.Temperature);
        draft.ApplyTo(agent);
        Assert.Equal(0.7, agent.Temperature);
        Assert.Equal(0.3, other.Temperature);
        Assert.Equal("Jetik", agent.AgentName);
        Assert.Equal("default:cloud", agent.DefaultAgentModel);
    }

    [Fact]
    public void InvalidValueDoesNotChangeTheDraft()
    {
        var draft = new AgentSettingsDraft(new MandoCodeConfig { MaxTokens = 4096 });
        Assert.False(draft.Set(AgentSettingsDraft.Fields.Single(f => f.Key == "maxTokens"), "-10").Ok);
        Assert.Equal(4096, draft.Config.MaxTokens);
    }

    [Fact]
    public void ApplyPreservesAutomaticContextAndDoesNotPromoteSharedSecrets()
    {
        var source = new MandoCodeConfig { ModelName = "qwen2.5:7b", ContextLengthSetByUser = false, TavilyApiKey = "source-secret" };
        var draft = new AgentSettingsDraft(source);
        var target = new MandoCodeConfig { ModelName = source.ModelName, TavilyApiKey = "shared-secret" };
        draft.ApplyTo(target);
        Assert.False(target.ContextLengthSetByUser);
        Assert.Equal("shared-secret", target.TavilyApiKey);
    }

    [Fact]
    public void EveryExposedFieldUsesExistingValidation()
    {
        var draft = new AgentSettingsDraft(new MandoCodeConfig());
        foreach (var field in AgentSettingsDraft.Fields)
            Assert.True(draft.Set(field, field.Read(draft.Config)).Ok, field.Key);
    }

    [Fact]
    public void HistorySettingsRestore_OnlyAcceptsAgentFields()
    {
        var config = new MandoCodeConfig { AgentName = "Jetik", TavilyApiKey = "shared-secret", EnableWebSearch = false };
        AgentSettingsDraft.Restore(config, new() { ["webSearch"] = "true", ["tavilyKey"] = "untrusted", ["agentName"] = "changed", ["maxTokens"] = "-1" });
        Assert.True(config.EnableWebSearch);
        Assert.Equal("shared-secret", config.TavilyApiKey);
        Assert.Equal("Jetik", config.AgentName);
        Assert.True(config.MaxTokens > 0);
    }

    [Fact]
    public void EveryPresetIsValidForItsSetting()
    {
        var draft = new AgentSettingsDraft(new MandoCodeConfig());
        foreach (var setting in AgentSettingsDraft.Fields)
            foreach (var preset in AgentSettingsDraft.Presets(setting))
                Assert.True(draft.Set(setting, preset).Ok, setting.Key + ": " + preset);
    }

    [Fact]
    public void ApplyingAgentEditsDoesNotOverwriteTheSavedDefaultsSnapshot()
    {
        var agent = new MandoCodeConfig { Temperature = 0.2 };
        agent.DefaultAgentOptions = AgentSettingsDraft.Capture(agent);
        var draft = new AgentSettingsDraft(agent);
        Assert.True(draft.Set(AgentSettingsDraft.Fields.Single(f => f.Key == "temperature"), 0.7.ToString()).Ok);
        draft.ApplyTo(agent);
        Assert.Equal(0.7, agent.Temperature);
        Assert.Equal(0.2.ToString(), agent.DefaultAgentOptions["temperature"]);
        Assert.True(ConfigKeySetter.TrySet(agent, "temperature", 0.4.ToString()).Ok);
        Assert.Equal(0.4.ToString(), agent.DefaultAgentOptions["temperature"]);
    }

    [Fact]
    public void NumericPresetsStaySortedWhenTheCurrentValueIsCustom()
    {
        var config = new MandoCodeConfig { Temperature = 0.55, MaxTokens = 6000 };
        foreach (var key in new[] { "temperature", "maxTokens" })
        {
            var setting = AgentSettingsDraft.Fields.Single(f => f.Key == key);
            var values = AgentSettingsDraft.OrderedPresets(setting, config).Select(double.Parse).ToArray();
            Assert.Equal(values.Order(), values);
            Assert.Contains(double.Parse(setting.Read(config)), values);
        }
        Assert.Contains(0.7.ToString(), AgentSettingsDraft.OrderedPresets(AgentSettingsDraft.Fields[0], config));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(30)]
    [InlineData(46)]
    public void DescriptionsWrapWithoutLosingText(int width)
    {
        foreach (var setting in AgentSettingsDraft.Fields)
        {
            var lines = AgentSettingsDraft.Wrap(setting.Description, width);
            Assert.Equal(setting.Description, string.Join(" ", lines));
            Assert.All(lines, line => Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(line) <= width));
        }
    }
}
