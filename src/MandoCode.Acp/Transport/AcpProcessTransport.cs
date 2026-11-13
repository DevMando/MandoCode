using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MandoCode.Acp.Transport;

public sealed class AcpProcessOptions
{
    /// <summary>How long a polite shutdown (closing stdin) may take before the process tree is killed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the exit code after stdout reached EOF when describing a closed connection.</summary>
    public TimeSpan ExitCodeWait { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Longest stderr line kept; the rest of a longer line is dropped.</summary>
    public int MaxStderrLineLength { get; init; } = 8192;
}

/// <summary>
/// Runs an agent as a child process and exposes its stdio as streams.
/// <list type="bullet">
/// <item>Started with <c>UseShellExecute = false</c> and <c>ArgumentList</c>; no shell is ever involved.</item>
/// <item>stdout carries protocol messages only; stderr goes to a <see cref="StderrLog"/>, never to the console.</item>
/// <item>Shutdown closes stdin, waits a grace period, then kills the whole process tree.</item>
/// </list>
/// </summary>
public sealed class AcpProcessTransport : IAcpTransport
{
    private static readonly ConcurrentDictionary<int, Process> Live = new();
    private static int _exitHookInstalled;

    private readonly Process _process;
    private readonly StderrLog _stderr;
    private readonly AcpProcessOptions _options;
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stderrPump;
    private int _disposed;

    private AcpProcessTransport(Process process, StderrLog stderr, AcpProcessOptions options)
    {
        _process = process;
        _stderr = stderr;
        _options = options;

        Input = process.StandardOutput.BaseStream;
        Output = process.StandardInput.BaseStream;
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public Task<int?> Exited => _exited.Task;
    public string StderrTail => _stderr.Tail();
    public int ProcessId { get; private set; }

    /// <summary>
    /// Starts the resolved command. <paramref name="command"/> must come from <see cref="AcpCommandResolver"/>:
    /// its absolute path is what gets executed.
    /// </summary>
    public static AcpProcessTransport Start(
        AcpLaunchSpec spec,
        ResolvedCommand command,
        StderrLog stderr,
        AcpProcessOptions? options = null)
    {
        options ??= new AcpProcessOptions();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in command.Args)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        var environment = AcpEnvironment.Build(
            AcpEnvironment.CaptureCurrent(), spec, caseInsensitiveNames: OperatingSystem.IsWindows());
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        AcpProcessTransport? transport = null;
        process.Exited += (_, _) => transport?.OnProcessExited();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new AcpLaunchException(AcpLaunchFailure.StartFailed,
                $"Could not start `{command.ExecutablePath}`: {ex.Message}", ex);
        }

        transport = new AcpProcessTransport(process, stderr, options) { ProcessId = process.Id };
        Live[process.Id] = process;
        InstallExitHook();

        // The process can have exited before the handler above saw `transport`.
        if (process.HasExited)
            transport.OnProcessExited();

        return transport;
    }

    private void OnProcessExited()
    {
        int? code = null;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { }
        _exited.TrySetResult(code);
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            var buffer = new char[4096];
            var line = new StringBuilder();
            var truncated = false;

            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        Flush();
                    }
                    else if (c != '\r')
                    {
                        if (line.Length < _options.MaxStderrLineLength)
                            line.Append(c);
                        else
                            truncated = true;
                    }
                }
            }

            Flush();

            void Flush()
            {
                if (line.Length == 0 && !truncated)
                    return;

                _stderr.Append(truncated ? line + " [truncated]" : line.ToString());
                line.Clear();
                truncated = false;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone; whatever was read is already in the log.
        }
    }

    public async Task<AcpConnectionClosedException> DescribeCloseAsync()
    {
        int? exitCode = null;
        var finished = await Task.WhenAny(_exited.Task, Task.Delay(_options.ExitCodeWait)).ConfigureAwait(false);
        if (finished == _exited.Task)
            exitCode = await _exited.Task.ConfigureAwait(false);

        // Give the stderr pump a moment to deliver the final lines (they often explain the exit).
        await Task.WhenAny(_stderrPump, Task.Delay(200)).ConfigureAwait(false);

        var message = exitCode is { } code
            ? $"The agent process exited with code {code}."
            : "The agent closed the connection.";

        return new AcpConnectionClosedException(message, exitCode, _stderr.Tail(20));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            // Closing stdin is the polite request to stop; agents are expected to exit on EOF.
            Output.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        if (!HasExited())
            await Task.WhenAny(_exited.Task, Task.Delay(_options.ShutdownGrace)).ConfigureAwait(false);

        if (!HasExited())
            KillTree();

        await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        await Task.WhenAny(_stderrPump, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);

        Live.TryRemove(ProcessId, out _);
        _stderr.Close();
        _process.Dispose();
    }

    /// <summary>Kills the agent and everything it started, without the grace period.</summary>
    public void KillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private bool HasExited()
    {
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static void InstallExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0)
            return;

        // Last resort when MandoCode itself exits without disposing its panes (Ctrl+C, crash):
        // never leave agent process trees running.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var process in Live.Values)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
            }
        };
    }
}
