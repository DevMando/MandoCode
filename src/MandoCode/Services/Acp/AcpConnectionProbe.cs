using System.Diagnostics;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Transport;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

/// <summary>
/// Launches a configured ACP agent, runs <c>initialize</c> and <c>session/new</c>, shuts it down and
/// reports what it found. Shared by <c>mandocode --acp-probe &lt;name&gt;</c> and <c>/agent-acp test &lt;name&gt;</c>:
/// both resolve the command with <see cref="AcpCommandResolver"/> and go through the same trust check, so
/// neither can start an agent the user has not approved.
/// </summary>
public static class AcpConnectionProbe
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitDeclined = 3;

    public const string NotAcpHint = "This program may not speak ACP; use an ACP adapter (see docs/acp.md).";

    public const string DefaultAuthHint = "Sign in with the agent's own tooling (for Claude Code: `claude auth login`), then try again.";

    /// <summary>Parses what follows <c>--acp-probe</c>: <c>[--cwd &lt;dir&gt;] &lt;name&gt;</c>. Null (after printing why) on bad usage.</summary>
    public static (string Name, string WorkingDirectory)? Parse(IReadOnlyList<string> args, string defaultWorkingDirectory, TextWriter error)
    {
        var workingDirectory = defaultWorkingDirectory;
        string? name = null;

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--cwd" && i + 1 < args.Count)
                workingDirectory = Path.GetFullPath(args[++i]);
            else if (args[i].StartsWith("--", StringComparison.Ordinal) || name is not null)
            {
                error.WriteLine($"Unexpected argument '{args[i]}'.");
                name = null;
                break;
            }
            else
                name = args[i];
        }

        if (name is null)
        {
            error.WriteLine("Usage: mandocode --acp-probe [--cwd <dir>] <agent name>");
            error.WriteLine("  The agent must be configured under \"acpAgents\" in ~/.mandocode/config.json.");
            return null;
        }

        return (name, workingDirectory);
    }

    public static async Task<int> RunAsync(
        string name,
        IReadOnlyDictionary<string, AcpAgentConfig> agents,
        string workingDirectory,
        AcpAgentLauncher launcher,
        Func<AcpLaunchPlan, Task<bool>> confirm,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        string? logDirectory = null)
    {
        if (!agents.TryGetValue(name, out var config))
        {
            error.WriteLine($"No ACP agent named '{name}' is configured.");
            error.WriteLine(agents.Count == 0
                ? "Add one with /agent-acp add, or under \"acpAgents\" in ~/.mandocode/config.json."
                : "Configured: " + string.Join(", ", agents.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
            return ExitUsage;
        }

        if (config.Disabled)
        {
            error.WriteLine($"ACP agent '{name}' is disabled. Enable it with /agent-acp enable {name}.");
            return ExitFailed;
        }

        AcpLaunchPlan plan;
        try
        {
            plan = launcher.Prepare(name, config, workingDirectory);
        }
        catch (AcpLaunchException ex)
        {
            error.WriteLine("Refused: " + ex.Message);
            return ExitFailed;
        }

        if (!await launcher.EnsureTrustedAsync(plan, confirm))
        {
            output.WriteLine("Not started: the launch was not approved.");
            return ExitDeclined;
        }

        var stderr = new StderrLog(filePath: AcpLogPaths.For(name, logDirectory));
        var clock = Stopwatch.StartNew();
        AcpClient? client = null;
        try
        {
            var transport = AcpProcessTransport.Start(plan.Spec, plan.Resolved, stderr);
            client = new AcpClient(transport, new DenyAllClientHandler(), new AcpClientOptions
            {
                ClientName = "MandoCode-probe",
                StartupTimeout = TimeSpan.FromSeconds(config.StartupTimeoutSeconds),
            });

            var initialized = await client.ConnectAsync(cancellationToken);
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
                var session = await client.NewSessionAsync(workingDirectory, cancellationToken);
                output.WriteLine($"session/new: ok ({clock.ElapsedMilliseconds} ms), session {session.SessionId}");
                if (session.Modes is { } modes)
                    output.WriteLine("  modes:        " + string.Join(", ", modes.AvailableModes.Select(m => m.Id)) + $" (current: {modes.CurrentModeId})");
            }
            catch (AcpAuthRequiredException ex)
            {
                error.WriteLine("session/new: the agent is not signed in. " + ex.Message);
                error.WriteLine(config.AuthHint ?? DefaultAuthHint);
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
        catch (AcpTimeoutException ex)
        {
            error.WriteLine("Probe failed: " + ex.Message);
            error.WriteLine(NotAcpHint);
            return ExitFailed;
        }
        catch (Exception ex) when (ex is AcpProtocolVersionException or JsonRpcException or OperationCanceledException)
        {
            error.WriteLine("Probe failed: " + ex.Message);
            return ExitFailed;
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();

            // The tail only: the whole of stderr (which can be very verbose) goes to the log file.
            var tail = stderr.Tail(20);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                error.WriteLine();
                error.WriteLine("Agent stderr (last lines):");
                error.WriteLine(tail);
            }

            if (stderr.FilePath is { } logFile && File.Exists(logFile))
                error.WriteLine("Full agent stderr: " + logFile);
        }
    }

    /// <summary>The terminal version of the trust question: shows the plan and asks y/N. Anything but yes declines.</summary>
    public static Func<AcpLaunchPlan, Task<bool>> ConsoleConfirm(TextWriter output, Func<string?> readLine) =>
        plan =>
        {
            foreach (var line in plan.Describe())
                output.WriteLine(line);
            output.Write("Trust and start it? [y/N] ");
            var answer = readLine()?.Trim();
            return Task.FromResult(
                string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
        };
}

/// <summary>Where agent stderr is kept: <c>~/.mandocode/logs/acp/&lt;agent&gt;-&lt;date&gt;.log</c> (rotated by <see cref="StderrLog"/>).</summary>
public static class AcpLogPaths
{
    public const string TraceEnvironmentVariable = "MANDOCODE_ACP_TRACE";

    /// <summary>
    /// Shows the content of vendor extension notifications (such as <c>_auth/status_update</c>) in the terminal, to learn
    /// what an agent sends. They can carry account details, so this is a separate switch, is never written to a log
    /// or to the trace, and is off unless set.
    /// </summary>
    public const string ExtensionDumpEnvironmentVariable = "MANDOCODE_ACP_DEBUG_EXT";

    public static bool ExtensionDumpEnabled() =>
        Environment.GetEnvironmentVariable(ExtensionDumpEnvironmentVariable) is { Length: > 0 } value
        && !value.Equals("0", StringComparison.Ordinal) && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static bool TraceEnabled(AcpAgentConfig config) =>
        config.Trace || Environment.GetEnvironmentVariable(TraceEnvironmentVariable) is { Length: > 0 } value
                        && !value.Equals("0", StringComparison.Ordinal) && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The trace file; created only when something is traced.</summary>
    public static string TraceFor(string agentName, string? directory = null)
    {
        var stderr = For(agentName, directory);
        return stderr[..^".log".Length] + ".trace.log";
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "logs", "acp");

    public static string For(string agentName, string? directory = null)
    {
        var safe = new string(agentName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return Path.Combine(directory ?? DefaultDirectory(), $"{safe}-{DateTime.Now:yyyyMMdd}.log");
    }
}
