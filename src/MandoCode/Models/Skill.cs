using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MandoCode.Models;

/// <summary>
/// Where a skill was loaded from. Project skills override user skills by name.
/// </summary>
public enum SkillSource
{
    User,
    Project
}

/// <summary>
/// A single skill loaded from a SKILL.md file on disk.
/// </summary>
public class Skill
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public SkillSource Source { get; init; }
}

/// <summary>
/// YAML-deserialized frontmatter. Optional fields; name falls back to folder name.
/// </summary>
internal class SkillFrontmatter
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Parses a SKILL.md file into a Skill. The file format is:
///   ---
///   name: my-skill
///   description: one-line description
///   ---
///   (markdown body...)
///
/// Frontmatter is required but both fields are optional inside it.
/// If name is missing, it falls back to the folder name.
/// </summary>
public static class SkillParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses the given SKILL.md file. Returns null if the file is malformed
    /// (missing frontmatter, bad YAML, empty body).
    /// </summary>
    public static Skill? ParseFile(string skillFilePath, SkillSource source, out string? error)
    {
        error = null;
        try
        {
            var raw = File.ReadAllText(skillFilePath);
            var folderName = Path.GetFileName(Path.GetDirectoryName(skillFilePath)) ?? "";

            if (!TrySplitFrontmatter(raw, out var frontmatterYaml, out var body))
            {
                error = "missing or malformed frontmatter";
                return null;
            }

            SkillFrontmatter? fm;
            try
            {
                fm = YamlDeserializer.Deserialize<SkillFrontmatter>(frontmatterYaml);
            }
            catch (Exception ex)
            {
                error = $"YAML parse error: {ex.Message}";
                return null;
            }

            var name = !string.IsNullOrWhiteSpace(fm?.Name) ? fm!.Name!.Trim() : folderName;
            var description = fm?.Description?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "skill has no name (frontmatter or folder)";
                return null;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                error = "skill body is empty";
                return null;
            }

            return new Skill
            {
                Name = name,
                Description = description,
                Body = body.Trim(),
                SourcePath = skillFilePath,
                Source = source
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TrySplitFrontmatter(string raw, out string frontmatter, out string body)
    {
        frontmatter = "";
        body = "";

        var normalized = raw.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
        {
            return false;
        }

        var afterOpen = normalized.Substring(4);
        var closeIdx = afterOpen.IndexOf("\n---");
        if (closeIdx < 0)
        {
            return false;
        }

        frontmatter = afterOpen.Substring(0, closeIdx);
        var afterClose = afterOpen.Substring(closeIdx + 4);
        body = afterClose.TrimStart('\n');
        return true;
    }
}
