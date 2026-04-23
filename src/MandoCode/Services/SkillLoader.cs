using System.Diagnostics;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Scans user + project skill directories, parses SKILL.md files, and exposes
/// the loaded skills. Project skills override user skills when names collide.
/// Each skill lives in its own folder containing a SKILL.md file; folders without
/// a SKILL.md are silently skipped (lets users stash drafts).
/// </summary>
public class SkillLoader
{
    private readonly MandoCodeConfig _config;
    private readonly ProjectRootAccessor _projectRoot;
    private readonly object _lock = new();
    private Dictionary<string, Skill> _skillsByName = new(StringComparer.OrdinalIgnoreCase);
    private string _resolvedUserDir = "";
    private string _resolvedProjectDir = "";

    /// <summary>Absolute path to the user-skills directory that was scanned last.</summary>
    public string ResolvedUserSkillsDirectory
    {
        get { lock (_lock) { return _resolvedUserDir; } }
    }

    /// <summary>Absolute path to the project-skills directory that was scanned last.</summary>
    public string ResolvedProjectSkillsDirectory
    {
        get { lock (_lock) { return _resolvedProjectDir; } }
    }

    public SkillLoader(MandoCodeConfig config, ProjectRootAccessor projectRoot)
    {
        _config = config;
        _projectRoot = projectRoot;
        Reload();
    }

    /// <summary>
    /// Re-scans both skill directories and rebuilds the internal index.
    /// Project skills are loaded after user skills so they overwrite on name collision.
    /// </summary>
    public void Reload()
    {
        var index = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        var userDir = _config.GetEffectiveUserSkillsDirectory();
        LoadDirectory(userDir, SkillSource.User, index);

        var projectDir = ResolveProjectSkillsDirectory();
        LoadDirectory(projectDir, SkillSource.Project, index);

        lock (_lock)
        {
            _skillsByName = index;
            _resolvedUserDir = userDir;
            _resolvedProjectDir = projectDir;
        }
    }

    /// <summary>
    /// Resolves the project skills directory. If the user set ProjectSkillsDirectory in
    /// config, that value is honored verbatim. Otherwise we walk up the ancestor chain
    /// from the project root looking for an existing ".mandocode/skills" directory —
    /// same heuristic git uses to find ".git" no matter where inside the tree you are.
    /// Falls back to the default path at the project root if nothing is found.
    /// </summary>
    private string ResolveProjectSkillsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_config.ProjectSkillsDirectory))
        {
            return _config.GetEffectiveProjectSkillsDirectory(_projectRoot.ProjectRoot);
        }

        var current = _projectRoot.ProjectRoot;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
        {
            var candidate = Path.Combine(current, ".mandocode", "skills");
            if (Directory.Exists(candidate)) return candidate;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return _config.GetEffectiveProjectSkillsDirectory(_projectRoot.ProjectRoot);
    }

    /// <summary>All loaded skills, ordered by name.</summary>
    public IReadOnlyList<Skill> GetAll()
    {
        lock (_lock)
        {
            return _skillsByName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Look up a skill by name (case-insensitive). Returns null if missing.</summary>
    public Skill? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
        {
            return _skillsByName.TryGetValue(name.Trim(), out var skill) ? skill : null;
        }
    }

    private static void LoadDirectory(string directory, SkillSource source, Dictionary<string, Skill> index)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkillLoader] Cannot enumerate {directory}: {ex.Message}");
            return;
        }

        foreach (var folder in folders)
        {
            var skillFile = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            var skill = SkillParser.ParseFile(skillFile, source, out var error);
            if (skill == null)
            {
                Debug.WriteLine($"[SkillLoader] Skipped {skillFile}: {error}");
                continue;
            }

            if (index.TryGetValue(skill.Name, out var existing))
            {
                Debug.WriteLine($"[SkillLoader] {source} skill '{skill.Name}' overrides {existing.Source} skill at {existing.SourcePath}");
            }
            index[skill.Name] = skill;
        }
    }
}
