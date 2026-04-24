using System.Text.Json;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Tests;

public class McpConfigTests
{
    [Fact]
    public void DeserializesClaudeDesktopStdioShape()
    {
        // Copy-pasted verbatim from a typical MCP server README
        const string json = @"{
            ""mcpServers"": {
                ""filesystem"": {
                    ""command"": ""npx"",
                    ""args"": [""-y"", ""@modelcontextprotocol/server-filesystem"", ""/path/to/root""],
                    ""env"": { ""FOO"": ""bar"" }
                }
            }
        }";

        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(config);
        Assert.True(config!.McpServers.ContainsKey("filesystem"));
        var fs = config.McpServers["filesystem"];
        Assert.Equal("npx", fs.Command);
        Assert.Equal(new[] { "-y", "@modelcontextprotocol/server-filesystem", "/path/to/root" }, fs.Args);
        Assert.Equal("bar", fs.Env["FOO"]);
        Assert.False(fs.IsHttp);
    }

    [Fact]
    public void DeserializesRemoteHttpShape()
    {
        const string json = @"{
            ""mcpServers"": {
                ""solana"": {
                    ""url"": ""https://mcp.solana.com/mcp"",
                    ""transport"": ""http""
                }
            }
        }";

        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(config);
        var solana = config!.McpServers["solana"];
        Assert.Equal("https://mcp.solana.com/mcp", solana.Url);
        Assert.True(solana.IsHttp);
    }

    [Fact]
    public void HeadersRoundTripForBearerTokenAuth()
    {
        const string json = @"{
            ""mcpServers"": {
                ""github"": {
                    ""url"": ""https://api.githubcopilot.com/mcp/"",
                    ""headers"": { ""Authorization"": ""Bearer ghp_xxx"" }
                }
            }
        }";

        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.Equal("Bearer ghp_xxx", config!.McpServers["github"].Headers["Authorization"]);
    }

    [Fact]
    public void IsHttpIsFalseByDefault()
    {
        var s = new McpServerConfig { Command = "some-binary" };
        Assert.False(s.IsHttp);
    }

    [Fact]
    public void IsHttpIsTrueWhenUrlSet()
    {
        var s = new McpServerConfig { Url = "https://example.com/mcp" };
        Assert.True(s.IsHttp);
    }

    [Fact]
    public void UnknownFieldsAreIgnoredForClaudeDesktopCompat()
    {
        // Claude Desktop may add fields MandoCode doesn't know about — deserialization
        // must not throw so a shared config file stays portable across clients.
        const string json = @"{
            ""mcpServers"": {
                ""filesystem"": {
                    ""command"": ""npx"",
                    ""args"": [""-y"", ""pkg""],
                    ""someFutureField"": ""value""
                }
            }
        }";

        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.Equal("npx", config!.McpServers["filesystem"].Command);
    }

    [Fact]
    public void DefaultConfigHasEmptyMcpServers()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.NotNull(config.McpServers);
        Assert.Empty(config.McpServers);
    }

    [Fact]
    public void ValidateAndClamp_MakesMcpServersCaseInsensitive()
    {
        var config = new MandoCodeConfig
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["Solana"] = new McpServerConfig { Url = "https://x" }
            }
        };

        config.ValidateAndClamp();

        // Case-insensitive lookup now works regardless of how the key was stored
        Assert.True(config.McpServers.ContainsKey("solana"));
        Assert.True(config.McpServers.ContainsKey("SOLANA"));
        Assert.True(config.McpServers.ContainsKey("Solana"));
    }

    [Fact]
    public void ValidateAndClamp_CaseCollision_KeepsFirstDoesNotThrow()
    {
        // Scenario: user hand-edits config.json with both "Solana" and "solana".
        // Validation must not throw — previously this crashed under
        // `new Dictionary<...>(dict, OrdinalIgnoreCase)` and dropped the whole config.
        var server1 = new McpServerConfig { Url = "https://one" };
        var server2 = new McpServerConfig { Url = "https://two" };
        var config = new MandoCodeConfig
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["Solana"] = server1,
                ["solana"] = server2
            }
        };

        var ex = Record.Exception(() => config.ValidateAndClamp());
        Assert.Null(ex);

        // Keep-first semantics — the entry that came first in the JSON wins.
        Assert.Single(config.McpServers);
        Assert.Same(server1, config.McpServers["solana"]);
    }
}
