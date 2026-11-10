using System.Diagnostics;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Transport;

namespace MandoCode.Services.Acp;

/// <summary>What <c>mandocode --acp-probe</c> was asked to launch.</summary>
public sealed record AcpProbeRequest(
    string Command,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    bool AssumeYes,
    bool PassAnthropicApiKey = false);

/// <summary>
/// Launches an ACP agent, runs <c>initialize</c> and <c>session/new</c>, shuts it down and prints what it found.
/// Used for manual validation of an agent before it is configured (nothing is stored or trusted here).
/// </summary>
public static class AcpConnectionProbe
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitDeclined = 3;

    /// <summary>Parses the arguments that follow <c>--acp-probe</c>. Returns null (after printing why) on bad usage.</summary>
    public static AcpProbeRequest? Parse(IReadOnlyList<string> args, string defaultWorkingDirectory, TextWriter error)
    {
        var workingDirectory = defaultWorkingDirectory;
        var assumeYes = false;
        var passKey = false;

        var i = 0;
        for (; i < args.Count && args[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            switch (args[i])
            {
                case "--yes":
                    assumeYes = true;
                    break;
                case "--pass-anthropic-api-key":
                    passKey = true;
                    break;
                case "--cwd" when i + 1 < args.Count:
                    workingDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--":
                    i++;
                    goto done;
                default:
                    error.WriteLine($"Unknown option '{args[i]}'.");
                    return null;
            }
        }

        done:
        if (i >= args.Count)
        {
            error.WriteLine("Usage: mandocode --acp-probe [--yes] [--cwd <dir>] [--pass-anthropic-api-key] [--] <command> [args...]");
            error.WriteLine("  <command> must be a native executable (.exe/ELF); for Node adapters use: node <absolute path to the adapter script>");
            return null;
        }

        return new AcpProbeRequest(args[i], args.Skip(i + 1).ToArray(), workingDirectory, assumeYes, passKey);
    }

    public static async Task<int> RunAsync(
        AcpProbeRequest request,
        TextWriter output,
        TextWriter error,
        Func<string?> readLine,
        CancellationToken cancellationToken = default)
    {
        ResolvedCommand resolved;
        try
        {
            resolved = AcpCommandResolver.Resolve(request.Command, request.Args);
        }
        catch (AcpLaunchException ex)
        {
            error.WriteLine("Refused: " + ex.Message);
            return ExitFailed;
        }

        output.WriteLine("The agent will be started as:");
        output.WriteLine("  " + resolved.ExecutablePath);
        foreach (var argument in resolved.Args)
            output.WriteLine("    arg: " + argument);
        output.WriteLine("  working directory: " + request.WorkingDirectory);
        output.WriteLine(request.PassAnthropicApiKey
            ? "  ANTHROPIC_API_KEY: passed through (explicitly requested)"
            : "  ANTHROPIC_API_KEY: removed from the agent's environment");
        output.WriteLine("It runs with your permissions and may start further processes.");

        if (!request.AssumeYes)
        {
            output.Write("Start it? [y/N] ");
            var answer = readLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("Not started.");
                return ExitDeclined;
            }
        }

        var stderr = new StderrLog();
        var spec = new AcpLaunchSpec(
            Path.GetFileNameWithoutExtension(resolved.ExecutablePath), resolved.ExecutablePath, resolved.Args,
            null, request.PassAnthropicApiKey, request.WorkingDirectory);

        var clock = Stopwatch.StartNew();
        AcpClient? client = null;
        try
        {
            var transport = AcpProcessTransport.Start(spec, resolved, stderr);
            client = new AcpClient(transport, new DenyAllClientHandler(), new AcpClientOptions { ClientName = "MandoCode-probe" });

            var initialized = await client.ConnectAsync(cancellationToken);
            output.WriteLine();
            output.WriteLine($"initialize: ok in {clock.ElapsedMilliseconds} ms");
            output.WriteLine($"  agent:        {initialized.AgentInfo?.Name ?? "(not reported)"} {initialized.AgentInfo?.Version}".TrimEnd());
            output.WriteLine($"  protocol:     v{initialized.ProtocolVersion}");
            output.WriteLine($"  loadSession:  {initialized.AgentCapabilities.LoadSession}");
            var prompt = initialized.AgentCapabilities.PromptCapabilities;
            output.WriteLine($"  prompt:       image={prompt.Image} audio={prompt.Audio} embeddedContext={prompt.EmbeddedContext}");
            output.WriteLine("  authMethods:  " + (initialized.AuthMethods.Count == 0
                ? "(none)"
                : string.Join(", ", initialized.AuthMethods.Select(m => $"{m.Id} ({m.Name})"))));

            try
            {
                var session = await client.NewSessionAsync(request.WorkingDirectory, cancellationToken);
                output.WriteLine($"session/new: ok ({clock.ElapsedMilliseconds} ms), session {session.SessionId}");
                if (session.Modes is { } modes)
                    output.WriteLine("  modes:        " + string.Join(", ", modes.AvailableModes.Select(m => m.Id)) + $" (current: {modes.CurrentModeId})");
            }
            catch (AcpAuthRequiredException ex)
            {
                error.WriteLine("session/new: the agent is not signed in. " + ex.Message);
                error.WriteLine("Sign in with the agent's own tooling (for Claude Code: `claude auth login`), then probe again.");
                return ExitFailed;
            }

            return ExitOk;
        }
        catch (AcpLaunchException ex)
        {
            error.WriteLine("Failed to start: " + ex.Message);
            return ExitFailed;
        }
        catch (AcpConnectionClosedException ex)
        {
            error.WriteLine("The agent went away: " + ex.Message);
            return ExitFailed;
        }
        catch (Exception ex) when (ex is AcpTimeoutException or AcpProtocolVersionException or JsonRpcException or OperationCanceledException)
        {
            error.WriteLine("Probe failed: " + ex.Message);
            return ExitFailed;
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();

            var tail = stderr.Tail(15);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                error.WriteLine();
                error.WriteLine("Agent stderr (last lines):");
                error.WriteLine(tail);
            }
        }
    }
}
