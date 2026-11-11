using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MandoCode.Acp;
using MandoCode.Acp.Transport;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

/// <summary>Where the configured agents live. The app implements it over the user's config; tests use a fake.</summary>
public interface IAcpAgentCatalog
{
    IReadOnlyDictionary<string, AcpAgentConfig> Agents { get; }

    /// <summary>Replaces the whole set and persists it to the user's config.</summary>
    void Save(IReadOnlyDictionary<string, AcpAgentConfig> agents);
}

/// <summary>What the pane hosting the command line can do for ACP commands.</summary>
public interface IAcpPaneHost
{
    /// <summary>Opens a new pane running the agent. Null on success, otherwise what to tell the user.</summary>
    Task<string?> OpenPaneAsync(string agentName);

    /// <summary>
    /// Switches the current pane to the agent (closing the ACP agent it already runs, if any). The native session stays
    /// alive underneath. Null on success, otherwise what to tell the user (for example when the pane is busy).
    /// </summary>
    Task<string?> SwitchAsync(string agentName);

    /// <summary>Closes the current pane's ACP session and returns it to the native agent. Null on success, otherwise what to tell the user.</summary>
    Task<string?> StopAsync();

    /// <summary>Restarts the agent of the current pane. Null on success, otherwise what to tell the user.</summary>
    Task<string?> RestartAsync();
}

/// <summary>
/// The text commands: <c>/agent-acp [list]</c>, <c>add</c>, <c>test</c>, <c>remove</c>, <c>enable</c>, <c>disable</c>.
/// Nothing here starts an agent except <c>test</c>, which goes through <see cref="AcpConnectionProbe"/> and the trust check.
/// </summary>
public sealed class AcpAgentCommands
{
    public const string Usage =
        "Usage: /agent-acp [list] | <name> [--new-panel] | restart | stop | status [name] | add <name> <command> [args...] | test <name> | remove <name> | enable <name> | disable <name>";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "status", "add", "test", "remove", "enable", "disable", "restart", "stop",
    };

    private readonly IAcpAgentCatalog _catalog;
    private readonly AcpAgentLauncher _launcher;
    private readonly IAcpResolverEnvironment? _resolverEnvironment;
    private readonly string? _logDirectory;
    private readonly IAcpPaneHost? _host;

    public AcpAgentCommands(IAcpAgentCatalog catalog, AcpAgentLauncher launcher,
        IAcpResolverEnvironment? resolverEnvironment = null, string? logDirectory = null, IAcpPaneHost? host = null)
    {
        _logDirectory = logDirectory;
        _host = host;
        _catalog = catalog;
        _launcher = launcher;
        _resolverEnvironment = resolverEnvironment;
    }

    public async Task ExecuteAsync(
        string arguments,
        string workingDirectory,
        Func<AcpLaunchPlan, Task<bool>> confirm,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var tokens = Tokenize(arguments);
        if (tokens.Count > 0 && tokens[0].StartsWith("--", StringComparison.Ordinal))
        {
            // Flags may come first: /agent-acp --new-panel claude
            await OpenAsync(tokens, error, output);
            return;
        }

        if (tokens.Count == 0 || tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            List(output);
            return;
        }

        var verb = tokens[0].ToLowerInvariant();
        var rest = tokens.Skip(1).ToList();
        switch (verb)
        {
            case "status":
                Status(rest, output, error);
                break;
            case "stop":
                if (_host is null)
                    error.WriteLine("Stop works inside an ACP agent pane.");
                else if (await _host.StopAsync() is { } stopError)
                    error.WriteLine(stopError);
                break;
            case "add":
                Add(rest, output, error);
                break;
            case "remove":
                Remove(rest, output, error);
                break;
            case "enable":
                SetDisabled(rest, disabled: false, output, error);
                break;
            case "disable":
                SetDisabled(rest, disabled: true, output, error);
                break;
            case "restart":
                if (_host is null)
                    error.WriteLine("Restart works inside an ACP agent pane.");
                else if (await _host.RestartAsync() is { } restartError)
                    error.WriteLine(restartError);
                break;
            case "test":
                if (rest.Count != 1)
                    error.WriteLine("Usage: /agent-acp test <name>");
                else
                    await AcpConnectionProbe.RunAsync(rest[0], _catalog.Agents, workingDirectory, _launcher, confirm, output, error, cancellationToken, _logDirectory);
                break;
            default:
                await OpenAsync(tokens, error, output);
                break;
        }
    }

    /// <summary><c>&lt;name&gt; [--new-panel]</c> (the flag may also come first): switch this pane, or open a new one.</summary>
    private async Task OpenAsync(List<string> tokens, TextWriter error, TextWriter output)
    {
        var newPanel = false;
        var names = new List<string>();
        foreach (var token in tokens)
        {
            if (!token.StartsWith("--", StringComparison.Ordinal))
                names.Add(token);
            else if (token.Equals("--new-panel", StringComparison.Ordinal))
                newPanel = true;
            else
            {
                error.WriteLine($"Unknown option '{token}'.");
                error.WriteLine(Usage);
                return;
            }
        }

        if (names.Count != 1)
        {
            error.WriteLine(Usage);
            return;
        }

        var name = names[0];
        if (!_catalog.Agents.TryGetValue(name, out var agent))
        {
            error.WriteLine($"Unknown /agent-acp command '{name}'.");
            error.WriteLine(Usage);
            return;
        }

        if (agent.Disabled)
            error.WriteLine($"ACP agent '{name}' is disabled. Enable it with /agent-acp enable {name}.");
        else if (_host is null)
            error.WriteLine("Opening an agent pane is not available here.");
        else if (newPanel)
        {
            if (await _host.OpenPaneAsync(name) is { } openError)
                error.WriteLine(openError);
            else
                output.WriteLine($"Opened ACP agent '{name}' in a new pane.");
        }
        else if (await _host.SwitchAsync(name) is { } switchError)
            error.WriteLine(switchError);
    }

    private void List(TextWriter output)
    {
        if (_catalog.Agents.Count == 0)
        {
            output.WriteLine("No ACP agents are configured.");
            output.WriteLine("Add one: /agent-acp add <name> <command> [args...]");
            output.WriteLine(@"  Node adapter: /agent-acp add myagent node ""C:\path\to\adapter\dist\index.js""");
            output.WriteLine("  Claude Code: /agent-acp add claude (shows how to set it up)");
            return;
        }

        foreach (var (name, agent) in _catalog.Agents.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var state = agent.Disabled ? "disabled" : "enabled";
            var trust = "not trusted yet";
            try
            {
                var plan = _launcher.Prepare(name, agent, Environment.CurrentDirectory);
                trust = _launcher.IsTrusted(plan) ? "trusted" : "not trusted yet";
            }
            catch (AcpLaunchException)
            {
                trust = "command refused";
            }

            output.WriteLine($"{name}  [{state}, {trust}]  {agent.Command} {string.Join(' ', agent.Args.Select(Quote))}".TrimEnd());
        }
    }

    private void Status(List<string> tokens, TextWriter output, TextWriter error)
    {
        if (tokens.Count > 1)
        {
            error.WriteLine("Usage: /agent-acp status [name]");
            return;
        }

        var agents = _catalog.Agents.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (tokens.Count == 1)
        {
            agents = agents.Where(kv => kv.Key.Equals(tokens[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (agents.Count == 0)
            {
                error.WriteLine($"No ACP agent named '{tokens[0]}'.");
                return;
            }
        }

        if (agents.Count == 0)
        {
            output.WriteLine("No ACP agents are configured.");
            return;
        }

        foreach (var (name, agent) in agents)
        {
            var trust = "not trusted yet";
            try
            {
                trust = _launcher.IsTrusted(_launcher.Prepare(name, agent, Environment.CurrentDirectory)) ? "trusted" : "not trusted yet";
            }
            catch (AcpLaunchException)
            {
                trust = "command refused";
            }

            output.WriteLine($"{name}  [{(agent.Disabled ? "disabled" : "enabled")}, {trust}]");
            output.WriteLine($"  stderr log: {Describe(AcpLogPaths.For(name, _logDirectory))}");
            output.WriteLine(AcpLogPaths.TraceEnabled(agent)
                ? $"  trace: on, {Describe(AcpLogPaths.TraceFor(name, _logDirectory))}"
                : $"  trace: off (set \"trace\": true on the entry, or {AcpLogPaths.TraceEnvironmentVariable}=1; the trace has no message content)");
        }
    }

    private static string Describe(string path) =>
        File.Exists(path) ? $"{path} ({new FileInfo(path).Length} bytes)" : $"{path} (not created yet)";

    private void Add(List<string> tokens, TextWriter output, TextWriter error)
    {
        if (tokens.Count == 1 && tokens[0].Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            // No preset: the adapter package, its pinned version and the way it finds the Claude Code binary
            // have not been validated against a real install yet (see docs/acp.md), so none is invented here.
            output.WriteLine("There is no built-in setup for Claude Code yet.");
            output.WriteLine("Install an ACP adapter yourself (a pinned version, no npx) and add it with its real path:");
            output.WriteLine(@"  /agent-acp add claude node ""<absolute path to the installed adapter>\dist\index.js""");
            output.WriteLine("or, if the adapter is a native program:");
            output.WriteLine("  /agent-acp add claude <absolute path to the adapter executable>");
            output.WriteLine("Sign in to Claude Code separately (`claude auth login`); MandoCode never handles its tokens.");
            return;
        }

        if (tokens.Count < 2)
        {
            error.WriteLine("Usage: /agent-acp add <name> <command> [args...]");
            return;
        }

        var name = tokens[0];
        if (!IsValidName(name) || Reserved.Contains(name))
        {
            error.WriteLine("Agent names are 1-40 characters: letters, digits, '-', '_' or '.' (and not a command word such as list, add, test, remove, enable, disable, restart).");
            return;
        }

        if (_catalog.Agents.ContainsKey(name))
        {
            error.WriteLine($"An agent named '{name}' already exists. Remove it first with /agent-acp remove {name}.");
            return;
        }

        var entry = new AcpAgentConfig { Command = tokens[1], Args = tokens.Skip(2).ToList() };
        try
        {
            // Refuse unsafe commands at add time; this only resolves, nothing is started.
            AcpCommandResolver.Resolve(entry.Command, entry.Args, _resolverEnvironment);
        }
        catch (AcpLaunchException ex)
        {
            error.WriteLine("Not added: " + ex.Message);
            return;
        }

        var agents = Copy(_catalog.Agents);
        agents[name] = entry;
        if (!TrySave(agents, error)) return;
        _launcher.Forget(name);
        output.WriteLine($"Added ACP agent '{name}'. Check it with /agent-acp test {name}; you will be asked to approve the exact command before it first runs.");
    }

    private void Remove(List<string> tokens, TextWriter output, TextWriter error)
    {
        if (tokens.Count != 1)
        {
            error.WriteLine("Usage: /agent-acp remove <name>");
            return;
        }

        if (!_catalog.Agents.ContainsKey(tokens[0]))
        {
            error.WriteLine($"No ACP agent named '{tokens[0]}'.");
            return;
        }

        var agents = Copy(_catalog.Agents);
        agents.Remove(tokens[0]);
        if (!TrySave(agents, error)) return;
        _launcher.Forget(tokens[0]);
        output.WriteLine($"Removed ACP agent '{tokens[0]}' and its trust record.");
    }

    private void SetDisabled(List<string> tokens, bool disabled, TextWriter output, TextWriter error)
    {
        var verb = disabled ? "disable" : "enable";
        if (tokens.Count != 1)
        {
            error.WriteLine($"Usage: /agent-acp {verb} <name>");
            return;
        }

        if (!_catalog.Agents.TryGetValue(tokens[0], out var existing))
        {
            error.WriteLine($"No ACP agent named '{tokens[0]}'.");
            return;
        }

        var agents = Copy(_catalog.Agents);
        agents[tokens[0]] = Clone(existing, disabled);
        if (!TrySave(agents, error)) return;
        output.WriteLine($"ACP agent '{tokens[0]}' is now {(disabled ? "disabled" : "enabled")}.");
    }

    private bool TrySave(IReadOnlyDictionary<string, AcpAgentConfig> agents, TextWriter error)
    {
        try
        {
            _catalog.Save(agents);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine("Could not save the ACP agent list: " + ex.Message);
            return false;
        }
    }

    private static Dictionary<string, AcpAgentConfig> Copy(IReadOnlyDictionary<string, AcpAgentConfig> source) =>
        new(source.ToDictionary(kv => kv.Key, kv => Clone(kv.Value, kv.Value.Disabled)), StringComparer.OrdinalIgnoreCase);

    private static AcpAgentConfig Clone(AcpAgentConfig source, bool disabled) => new()
    {
        Command = source.Command,
        Args = [.. source.Args],
        Env = new Dictionary<string, string>(source.Env),
        Disabled = disabled,
        PassAnthropicApiKey = source.PassAnthropicApiKey,
        StartupTimeoutSeconds = source.StartupTimeoutSeconds,
        StartMode = source.StartMode,
        AuthHint = source.AuthHint,
    };

    private static bool IsValidName(string name) =>
        name.Length is >= 1 and <= 40 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;

    /// <summary>Splits on whitespace; double quotes group a token (no escapes, so Windows paths keep their backslashes).</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
            tokens.Add(current.ToString());
        return tokens;
    }
}

/// <summary>
/// The catalog backed by the user's config file. Saving re-reads the file from disk, replaces only its
/// <c>acpAgents</c> section and writes it back under a process-wide lock, so other settings (changed by another
/// pane, or by hand while MandoCode runs) are never overwritten with stale in-memory values. Then every open
/// pane's config copy is updated, so a later save from any pane carries the change.
/// </summary>
public sealed class ConfigAcpAgentCatalog : IAcpAgentCatalog
{
    private static readonly object FileLock = new();

    private readonly MandoCodeConfig _config;
    private readonly IReadOnlyList<MandoCodeConfig> _allConfigs;
    private readonly string _path;

    public ConfigAcpAgentCatalog(MandoCodeConfig config, IReadOnlyList<MandoCodeConfig> allConfigs, string? configPath = null)
    {
        _config = config;
        _allConfigs = allConfigs;
        _path = configPath ?? MandoCodeConfig.GetDefaultConfigPath();
    }

    public IReadOnlyDictionary<string, AcpAgentConfig> Agents => _config.AcpAgents;

    public void Save(IReadOnlyDictionary<string, AcpAgentConfig> agents)
    {
        if (_config.AllowPersistence)
            WriteSection(agents);

        foreach (var config in _allConfigs.Append(_config).Distinct())
            config.AcpAgents = new Dictionary<string, AcpAgentConfig>(agents, StringComparer.OrdinalIgnoreCase);
    }

    private void WriteSection(IReadOnlyDictionary<string, AcpAgentConfig> agents)
    {
        lock (FileLock)
        {
            JsonObject root;
            if (File.Exists(_path))
            {
                // A file that cannot be parsed is not overwritten: that would destroy the user's other settings.
                root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                       ?? throw new InvalidDataException($"{_path} is not a JSON object; fix it by hand first.");
            }
            else
            {
                root = new JsonObject();
            }

            root["acpAgents"] = JsonSerializer.SerializeToNode(
                agents.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), ConfigJsonOptions.WriteOptions);

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(ConfigJsonOptions.WriteOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
