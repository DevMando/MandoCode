using MandoCode.Models;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MandoCode.Services;

/// <summary>Persists shared MCP definitions and refreshes agents without losing conversation history.</summary>
public sealed class CliIntegrationCoordinator(MandoCodeConfig defaults, string? configPath = null)
{
    public SkillManagementStore Skills => new(defaults.GetEffectiveUserSkillsDirectory());
    public Dictionary<string, McpServerConfig> Servers => Clone(defaults.McpServers);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _updating;
    public bool IsUpdating => Volatile.Read(ref _updating) != 0;
    public IDisposable BeginUpdate()
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) throw new InvalidOperationException("Another integration update is running. Try again when it finishes.");
        return new UpdateLease(() => Volatile.Write(ref _updating, 0));
    }
    private sealed class UpdateLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    internal static Dictionary<string, McpServerConfig> Clone(Dictionary<string, McpServerConfig> servers)
        => new(JsonSerializer.Deserialize<Dictionary<string, McpServerConfig>>(JsonSerializer.Serialize(servers))!, StringComparer.OrdinalIgnoreCase);

    public async Task SaveServersAsync(Dictionary<string, McpServerConfig> servers, IReadOnlyList<AgentPane> agents, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            Persist(servers);
            defaults.McpServers = Clone(servers);
            foreach (var agent in agents) agent.Services.GetRequiredService<MandoCodeConfig>().McpServers = Clone(servers);
            await ReloadMcpAsync(agents, token, readConfiguration: false);
        }
        finally { _gate.Release(); }
    }
    private void Persist(Dictionary<string, McpServerConfig> servers)
    {
        if (!defaults.AllowPersistence) return;
        var path = configPath ?? MandoCodeConfig.GetDefaultConfigPath();
        // Preserve other global options and fail visibly if an existing config cannot be read.
        var saved = File.Exists(path)
            ? JsonSerializer.Deserialize<MandoCodeConfig>(File.ReadAllText(path), ConfigJsonOptions.ReadOptions) ?? throw new IOException("Configuration is empty.")
            : JsonSerializer.Deserialize<MandoCodeConfig>(JsonSerializer.Serialize(defaults))!;
        saved.McpServers = Clone(servers);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(saved, ConfigJsonOptions.WriteOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task ReloadMcpAsync(IReadOnlyList<AgentPane> agents, CancellationToken token = default, bool readConfiguration = true)
    {
        var path = configPath ?? MandoCodeConfig.GetDefaultConfigPath();
        if (readConfiguration && defaults.AllowPersistence && File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<MandoCodeConfig>(File.ReadAllText(path), ConfigJsonOptions.ReadOptions) ?? throw new IOException("Configuration is empty.");
            defaults.McpServers = Clone(saved.McpServers);
            foreach (var agent in agents) agent.Services.GetRequiredService<MandoCodeConfig>().McpServers = Clone(saved.McpServers);
        }
        foreach (var agent in agents)
        {
            agent.Services.GetRequiredService<McpApprovalGate>().ResetSession();
            await agent.Services.GetRequiredService<McpClientManager>().ReloadAsync(token);
            // Rebuild even after cancellation so closed clients cannot remain bound as tools.
            await agent.Services.GetRequiredService<AIService>().RefreshMcpConnectionsAsync(token);
        }
    }
    public async Task ReloadSkillsAsync(IReadOnlyList<AgentPane> agents)
    {
        foreach (var agent in agents)
        {
            agent.Services.GetRequiredService<SkillLoader>().Reload();
            await agent.Services.GetRequiredService<AIService>().RefreshSettingsAsync(agent.Services.GetRequiredService<MandoCodeConfig>());
        }
    }
    internal static Dictionary<string, string> ParsePairs(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Replace("\r", "").Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0 || line[..equals].Trim().Length == 0) throw new ArgumentException("Use one KEY=value entry per line.");
            result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return result;
    }
    internal static void ValidateServer(string name, McpServerConfig server)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Server names can contain letters, numbers, hyphens, and underscores.");
        if (server.IsHttp)
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) throw new ArgumentException("Enter an absolute HTTP or HTTPS server URL.");
        }
        else if (string.IsNullOrWhiteSpace(server.Command)) throw new ArgumentException("Enter the executable used to start this MCP server.");
    }
}
