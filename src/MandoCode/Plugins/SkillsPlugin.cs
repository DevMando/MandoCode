using System.ComponentModel;
using Microsoft.SemanticKernel;
using MandoCode.Services;

namespace MandoCode.Plugins;

/// <summary>
/// Exposes the user's skills to the model. The system prompt lists every skill
/// name + short description, and the model calls load_skill(name) when it decides
/// a skill is relevant. The returned body contains the skill's full instructions
/// that the model then follows.
/// </summary>
public class SkillsPlugin
{
    private readonly SkillLoader _loader;

    public SkillsPlugin(SkillLoader loader)
    {
        _loader = loader;
    }

    [KernelFunction("load_skill")]
    [Description("Loads the full instructions for a named skill. Call this when a skill from the 'Available Skills' list matches what the user is trying to accomplish. The returned text contains workflow instructions you must follow for the rest of the turn.")]
    public string LoadSkill(
        [Description("The exact skill name as listed in the 'Available Skills' section of the system prompt.")] string name)
    {
        var skill = _loader.GetByName(name);
        if (skill == null)
        {
            var available = string.Join(", ", _loader.GetAll().Select(s => s.Name));
            return string.IsNullOrEmpty(available)
                ? $"No skill named '{name}' is installed. No skills are currently available."
                : $"No skill named '{name}' is installed. Available skills: {available}";
        }

        return $"# Skill: {skill.Name}\n\n{skill.Body}";
    }
}
