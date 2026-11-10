using System.Text;
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

/// <summary>
/// The text commands: <c>/agent-acp [list]</c>, <c>add</c>, <c>test</c>, <c>remove</c>, <c>enable</c>, <c>disable</c>.
/// Nothing here starts an agent except <c>test</c>, which goes through <see cref="AcpConnectionProbe"/> and the trust check.
/// </summary>
public sealed class AcpAgentCommands
{
    public const string Usage =
        "Usage: /agent-acp [list] | add <name> <command> [args...] | test <name> | remove <name> | enable <name> | disable <name>";

    private readonly IAcpAgentCatalog _catalog;
    private readonly AcpAgentLauncher _launcher;
    private readonly IAcpResolverEnvironment? _resolverEnvironment;

    public AcpAgentCommands(IAcpAgentCatalog catalog, AcpAgentLauncher launcher, IAcpResolverEnvironment? resolverEnvironment = null)
    {
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
        if (tokens.Count == 0 || tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            List(output);
            return;
        }

        var verb = tokens[0].ToLowerInvariant();
        var rest = tokens.Skip(1).ToList();
        switch (verb)
        {
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
            case "test":
                if (rest.Count != 1)
                    error.WriteLine("Usage: /agent-acp test <name>");
                else
                    await AcpConnectionProbe.RunAsync(rest[0], _catalog.Agents, workingDirectory, _launcher, confirm, output, error, cancellationToken);
                break;
            default:
                if (_catalog.Agents.ContainsKey(tokens[0]))
                {
                    output.WriteLine("Opening an ACP agent pane is not available yet. Use /agent-acp test " + tokens[0] + " to check the connection.");
                }
                else
                {
                    error.WriteLine($"Unknown /agent-acp command '{tokens[0]}'.");
                    error.WriteLine(Usage);
                }

                break;
        }
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
        if (!IsValidName(name))
        {
            error.WriteLine("Agent names are 1-40 characters: letters, digits, '-', '_' or '.'.");
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
        _catalog.Save(agents);
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
        _catalog.Save(agents);
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
        _catalog.Save(agents);
        output.WriteLine($"ACP agent '{tokens[0]}' is now {(disabled ? "disabled" : "enabled")}.");
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
        AuthHint = source.AuthHint,
    };

    private static bool IsValidName(string name) =>
        name.Length is >= 1 and <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

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
/// The catalog backed by the user's config. Saving updates every open pane's config copy (so a later save
/// from another pane cannot drop the change) and writes the user's config file.
/// </summary>
public sealed class ConfigAcpAgentCatalog : IAcpAgentCatalog
{
    private readonly MandoCodeConfig _config;
    private readonly IReadOnlyList<MandoCodeConfig> _allConfigs;

    public ConfigAcpAgentCatalog(MandoCodeConfig config, IReadOnlyList<MandoCodeConfig> allConfigs)
    {
        _config = config;
        _allConfigs = allConfigs;
    }

    public IReadOnlyDictionary<string, AcpAgentConfig> Agents => _config.AcpAgents;

    public void Save(IReadOnlyDictionary<string, AcpAgentConfig> agents)
    {
        foreach (var config in _allConfigs.Append(_config).Distinct())
            config.AcpAgents = new Dictionary<string, AcpAgentConfig>(agents, StringComparer.OrdinalIgnoreCase);

        _config.Save();
    }
}
