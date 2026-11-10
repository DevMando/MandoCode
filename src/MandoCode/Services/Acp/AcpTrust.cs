using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Transport;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

/// <summary>Remembers which launch of which agent the user has approved (one hash per agent name).</summary>
public interface ITrustStore
{
    string? GetHash(string agentName);
    void Set(string agentName, string hash);
    void Remove(string agentName);
}

/// <summary>
/// <c>~/.mandocode/acp-trust.json</c>: agent name → hash. Kept apart from config.json so that editing the
/// config can never grant trust by itself.
/// </summary>
public sealed class FileTrustStore : ITrustStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileTrustStore(string? path = null) => _path = path ?? DefaultPath();

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "acp-trust.json");

    public string? GetHash(string agentName)
    {
        lock (_gate)
            return Read().TryGetValue(agentName, out var hash) ? hash : null;
    }

    public void Set(string agentName, string hash)
    {
        lock (_gate)
        {
            var map = Read();
            map[agentName] = hash;
            Write(map);
        }
    }

    public void Remove(string agentName)
    {
        lock (_gate)
        {
            var map = Read();
            if (map.Remove(agentName))
                Write(map);
        }
    }

    private Dictionary<string, string> Read()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } stored)
            {
                foreach (var (name, hash) in stored)
                    map[name] = hash;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable trust file means nothing is trusted: the user is simply asked again.
        }

        return map;
    }

    private void Write(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Everything the user is asked to approve before an agent first runs.</summary>
public sealed record AcpLaunchPlan(
    string AgentName,
    AcpLaunchSpec Spec,
    ResolvedCommand Resolved,
    AcpAgentConfig Config,
    string Hash)
{
    /// <summary>The lines of the confirmation: resolved path, every argument on its own line, folder, env names only.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"ACP agent '{AgentName}' will be started as:",
            "  program: " + Resolved.ExecutablePath,
        };

        if (Resolved.Args.Count == 0)
            lines.Add("  arguments: (none)");
        for (var i = 0; i < Resolved.Args.Count; i++)
            lines.Add($"  argument {i + 1}: {Resolved.Args[i]}");

        lines.Add("  working directory: " + Spec.WorkingDirectory);
        lines.Add(Config.Env.Count == 0
            ? "  extra environment variables: (none)"
            : "  extra environment variables: " + string.Join(", ", Config.Env.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) + " (values not shown)");
        lines.Add(Config.PassAnthropicApiKey
            ? "  ANTHROPIC_API_KEY: PASSED to the agent (passAnthropicApiKey is set)"
            : "  ANTHROPIC_API_KEY: removed from the agent's environment");
        lines.Add("The agent runs with your permissions and may start further programs.");
        lines.Add("Changing the command, arguments, environment or the resolved program asks again.");
        return lines;
    }
}

/// <summary>Resolves an <c>acpAgents</c> entry into a launch plan and applies trust-on-first-use. Used by every way of starting an agent.</summary>
public sealed class AcpAgentLauncher
{
    private readonly ITrustStore _trust;
    private readonly IAcpResolverEnvironment? _resolverEnvironment;

    public AcpAgentLauncher(ITrustStore trust, IAcpResolverEnvironment? resolverEnvironment = null)
    {
        _trust = trust;
        _resolverEnvironment = resolverEnvironment;
    }

    /// <summary>Throws <see cref="AcpLaunchException"/> (with a user-readable message) when the command is refused.</summary>
    public AcpLaunchPlan Prepare(string name, AcpAgentConfig config, string workingDirectory)
    {
        var resolved = AcpCommandResolver.Resolve(config.Command, config.Args, _resolverEnvironment);
        var spec = new AcpLaunchSpec(name, resolved.ExecutablePath, resolved.Args, config.Env, config.PassAnthropicApiKey, workingDirectory);
        return new AcpLaunchPlan(name, spec, resolved, config, ComputeHash(resolved, config));
    }

    public bool IsTrusted(AcpLaunchPlan plan) =>
        string.Equals(_trust.GetHash(plan.AgentName), plan.Hash, StringComparison.Ordinal);

    /// <summary>
    /// True when the launch may go ahead: it was approved before (same hash), or the user approves it now.
    /// There is no way around the question; a declined or changed launch does not start.
    /// </summary>
    public async Task<bool> EnsureTrustedAsync(AcpLaunchPlan plan, Func<AcpLaunchPlan, Task<bool>> confirm)
    {
        if (IsTrusted(plan))
            return true;

        if (!await confirm(plan).ConfigureAwait(false))
            return false;

        _trust.Set(plan.AgentName, plan.Hash);
        return true;
    }

    public void Forget(string agentName) => _trust.Remove(agentName);

    /// <summary>SHA-256 over the canonical form of {resolved path, arguments, environment, passAnthropicApiKey}. The working directory is deliberately left out.</summary>
    public static string ComputeHash(ResolvedCommand resolved, AcpAgentConfig config)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("resolvedPath", resolved.ExecutablePath);
            writer.WriteStartArray("args");
            foreach (var argument in resolved.Args)
                writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WriteStartArray("env");
            foreach (var (key, value) in config.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("passAnthropicApiKey", config.PassAnthropicApiKey);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
