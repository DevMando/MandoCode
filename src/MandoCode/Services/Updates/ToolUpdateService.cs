using System.Diagnostics;
using System.Reflection;

namespace MandoCode.Services;

/// <summary>Prepares a global-tool update; a shell helper performs it after this process exits.</summary>
public sealed class ToolUpdateService
{
    private int _active;
    private string? _script;
    public string? LogPath { get; private set; }
    public bool Pending => _script is not null;
    public bool TryBegin() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;
    public void End() { if (!Pending) Interlocked.Exchange(ref _active, 0); }

    internal static bool IsGlobalInstallation(string assemblyPath, string profile)
    {
        var store = Path.GetFullPath(Path.Combine(profile, ".dotnet", "tools", ".store", "mandocode")) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(assemblyPath).StartsWith(store,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public async Task<string?> CheckInstallationAsync()
    {
        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(assembly) || !IsGlobalInstallation(assembly, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            return "Automatic updates support global .NET tool installations. This is a source build, local tool, or custom installation; update it using its original installation method.";
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--list-sdks");
            using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(true); } catch { } throw; }
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(await stdout))
                return "Updating requires the .NET SDK. Install the SDK, then run dotnet tool update -g MandoCode.";
            await stderr;
            return null;
        }
        catch (Exception ex) { return "Could not check the .NET SDK: " + ex.Message; }
    }

    public void Prepare(string version) => Prepare(version,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "updates", Guid.NewGuid().ToString("N")));

    // An isolated destination lets the simulation exercise the real prepare/launch
    // code without writing to the user's update history.
    internal void Prepare(string version, string directory)
    {
        if (!Version.TryParse(version, out _) || version.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new ArgumentException("Expected a stable numeric version.", nameof(version));
        Directory.CreateDirectory(directory);
        // --configfile is supported by both .NET 8 and .NET 10 SDKs. Isolate this
        // update from project-specific/private feeds; the release was checked on NuGet.
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"), "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>");
        LogPath = Path.Combine(directory, "update.log");
        var script = Path.Combine(directory, OperatingSystem.IsWindows() ? "update.ps1" : "update.sh");
        File.WriteAllText(script, BuildScript(OperatingSystem.IsWindows(), Environment.ProcessId, version, LogPath));
        _script = script;
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    internal static string BuildScript(bool windows, int processId, string version, string logPath)
    {
        var configPath = Path.Combine(Path.GetDirectoryName(logPath)!, "NuGet.Config");
        if (windows)
            return $$"""
                $ErrorActionPreference = 'Stop'
                $log = '{{logPath.Replace("'", "''")}}'
                try {
                    $parent = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
                    if ($parent -and -not $parent.WaitForExit(120000)) { throw 'MandoCode did not exit; update cancelled.' }
                    'Updating MandoCode to {{version}}' | Out-File -LiteralPath $log
                    & dotnet tool update --global MandoCode --version {{version}} --configfile '{{configPath.Replace("'", "''")}}' *>> $log
                    if ($LASTEXITCODE -ne 0) { throw "dotnet tool update failed (exit $LASTEXITCODE)." }
                    'Update complete. Run mandocode to start the new version.' | Out-File -LiteralPath $log -Append
                } catch {
                    $_.Exception.Message | Out-File -LiteralPath $log -Append
                    'Update failed. Run manually: dotnet tool update -g MandoCode' | Out-File -LiteralPath $log -Append
                    exit 1
                }
                """;
        return $$"""
            #!/bin/sh
            exec > {{ShellQuote(logPath)}} 2>&1
            remaining=120
            while kill -0 {{processId}} 2>/dev/null; do
                if [ "$remaining" -le 0 ]; then echo 'MandoCode did not exit; update cancelled.'; exit 1; fi
                remaining=$((remaining - 1))
                sleep 1
            done
            echo 'Updating MandoCode to {{version}}'
            if dotnet tool update --global MandoCode --version {{version}} --configfile {{ShellQuote(configPath)}}; then
                echo 'Update complete. Run mandocode to start the new version.'
            else
                echo 'Update failed. Run manually: dotnet tool update -g MandoCode'
                exit 1
            fi
            """;
    }

    /// <summary>Called only after host disposal, with normal terminal output restored.</summary>
    public void Launch()
    {
        if (_script is null) return;
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(File.ReadAllText(_script))) }) start.ArgumentList.Add(arg);
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("nohup /bin/sh " + ShellQuote(_script) + " > /dev/null 2>&1 < /dev/null &");
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the updater.");
        Console.WriteLine("MandoCode will update after exit. Run mandocode again once the update completes.");
        Console.WriteLine("Update progress and result: " + LogPath);
    }
}
