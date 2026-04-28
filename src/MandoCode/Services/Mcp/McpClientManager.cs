using System.Diagnostics;
using MandoCode.Models;
using ModelContextProtocol.Client;

namespace MandoCode.Services;

/// <summary>
/// Owns the lifecycle of all configured MCP client connections. Reads
/// <see cref="MandoCodeConfig.McpServers"/>, spawns one <see cref="McpClient"/> per
/// enabled entry (stdio or HTTP), and exposes them for kernel-plugin registration.
/// A single server failing to start does not abort the others — the failure is logged
/// and that server is skipped so the app stays usable.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private readonly MandoCodeConfig _config;
    // Case-insensitive so /mcp tools Solana and /mcp tools solana both match the same
    // entry — InputStateMachine lowercases the command string, and users hand-edit config
    // with any casing they like.
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _startupErrors = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    public McpClientManager(MandoCodeConfig config)
    {
        _config = config;
    }

    /// <summary>Active clients keyed by the server name from config.</summary>
    public IReadOnlyDictionary<string, McpClient> ActiveClients => _clients;

    /// <summary>Per-server startup errors (keyed by server name) for display via <c>/mcp</c>.</summary>
    public IReadOnlyDictionary<string, string> StartupErrors => _startupErrors;

    /// <summary>Connect to every enabled server in config. Idempotent when already started.</summary>
    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _started = true;

        if (!_config.EnableMcp || _config.McpServers.Count == 0) return;

        foreach (var (name, serverConfig) in _config.McpServers)
        {
            if (serverConfig.Disabled) continue;

            try
            {
                var client = await CreateClientAsync(name, serverConfig, cancellationToken);
                _clients[name] = client;
            }
            catch (Exception ex)
            {
                _startupErrors[name] = ex.Message;
                Debug.WriteLine($"[MCP] Failed to start server '{name}': {ex}");
            }
        }
    }

    /// <summary>Tear down all clients and clear startup errors. Used by <c>/mcp-reload</c>.</summary>
    public async Task StopAllAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[MCP] Dispose failed: {ex.Message}"); }
        }
        _clients.Clear();
        _startupErrors.Clear();
        _started = false;
    }

    /// <summary>Stop then restart every server. Safe to call while the app is running.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await StopAllAsync();
        await StartAllAsync(cancellationToken);
    }

    private static async Task<McpClient> CreateClientAsync(string name, McpServerConfig config, CancellationToken cancellationToken)
    {
        if (config.IsHttp)
        {
            if (string.IsNullOrWhiteSpace(config.Url))
                throw new InvalidOperationException($"MCP server '{name}' is HTTP but has no url");

            var httpOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url),
                Name = name
            };
            if (config.Headers.Count > 0)
            {
                httpOptions.AdditionalHeaders = config.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            var httpTransport = new HttpClientTransport(httpOptions);
            return await McpClient.CreateAsync(httpTransport, cancellationToken: cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(config.Command))
            throw new InvalidOperationException($"MCP server '{name}' has neither command nor url");

        var stdioOptions = new StdioClientTransportOptions
        {
            Name = name,
            Command = config.Command,
            Arguments = config.Args.ToArray()
        };
        if (config.Env.Count > 0)
        {
            stdioOptions.EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        }
        var stdioTransport = new StdioClientTransport(stdioOptions);
        return await McpClient.CreateAsync(stdioTransport, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }
}
