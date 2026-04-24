using System.Collections.Concurrent;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Gates MCP tool invocations behind a user approval prompt on first use.
/// MCP tools are opaque — we can't tell a read from a write by inspecting arguments —
/// so the safest UX is a one-time prompt per <c>(server, tool)</c> pair per session,
/// with an opt-out for tools the user has pre-approved in <c>mcpServers[name].autoApprove</c>.
/// Works in concert with <see cref="FunctionInvocationFilter"/>, which consults the gate
/// for any function whose plugin name starts with <c>"mcp_"</c>.
/// </summary>
public sealed class McpApprovalGate
{
    private readonly MandoCodeConfig _config;
    // Case-insensitive so a stored "Solana::tool" approval still matches a later
    // "solana::tool" lookup if the server's casing drifts mid-session.
    private readonly ConcurrentDictionary<string, bool> _sessionDecisions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Callback into the UI layer to render the approval prompt. If null, MCP tools run
    /// without prompting (used only in tests). Parameters: (serverName, toolName, description).
    /// </summary>
    public Func<string, string, string?, Task<DiffApprovalResult>>? OnApprovalRequested { get; set; }

    public McpApprovalGate(MandoCodeConfig config)
    {
        _config = config;
    }

    /// <summary>Clears remembered session approvals. Called on <c>/mcp-reload</c> and <c>/clear</c>.</summary>
    public void ResetSession() => _sessionDecisions.Clear();

    /// <summary>
    /// Gate the invocation of a single MCP tool. Returns an approval result so the filter
    /// can either let the call proceed or short-circuit it with a denial message.
    /// Never throws — an absent UI callback is treated as implicit approval.
    /// </summary>
    public async Task<DiffApprovalResult> RequestAsync(string serverName, string toolName, string? description = null)
    {
        // Config-level autoApprove list on the server entry bypasses the gate entirely.
        if (_config.McpServers.TryGetValue(serverName, out var serverConfig) &&
            serverConfig.AutoApprove.Any(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        // Session-level "approve for the rest of the session" decision, made by the user
        // via ApprovedNoAskAgain on an earlier prompt for the same tool.
        var key = $"{serverName}::{toolName}";
        if (_sessionDecisions.TryGetValue(key, out var approved))
        {
            return new DiffApprovalResult
            {
                Response = approved ? DiffApprovalResponse.Approved : DiffApprovalResponse.Denied
            };
        }

        if (OnApprovalRequested is null)
        {
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var result = await OnApprovalRequested(serverName, toolName, description);

        if (result.Response == DiffApprovalResponse.ApprovedNoAskAgain)
        {
            _sessionDecisions[key] = true;
        }
        // NOTE: Denials are intentionally NOT cached — the user may later change their mind
        // and re-asking once per call is the safer default than a sticky deny.

        return result;
    }
}
