using System.Diagnostics;
using System.Text;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ToolUpdateServiceTests
{
    [Fact]
    public void GlobalDetectionRejectsSourceBuildsAndSimilarDirectoryNames()
    {
        var profile = Path.Combine(Path.GetTempPath(), "update-profile");
        var store = Path.Combine(profile, ".dotnet", "tools", ".store");
        Assert.True(ToolUpdateService.IsGlobalInstallation(Path.Combine(store, "mandocode", "0.16.0", "MandoCode.dll"), profile));
        Assert.False(ToolUpdateService.IsGlobalInstallation(Path.Combine(store, "mandocode-other", "MandoCode.dll"), profile));
        Assert.False(ToolUpdateService.IsGlobalInstallation(Path.Combine(profile, "src", "MandoCode.dll"), profile));
    }

    [Fact]
    public void RejectsVersionArgumentsBeforePreparingAnyUpdate()
    {
        var updater = new ToolUpdateService();
        Assert.Throws<ArgumentException>(() => updater.Prepare("0.16.0; echo injected"));
        Assert.Throws<ArgumentException>(() => updater.Prepare("0.16.0-alpha"));
        Assert.False(updater.Pending);
    }

    [Fact]
    public void OnlyOneUpdateFlowCanRunAtATime()
    {
        var updater = new ToolUpdateService();
        Assert.True(updater.TryBegin());
        Assert.False(updater.TryBegin());
        updater.End();
        Assert.True(updater.TryBegin());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HelperRecordsActualOutcomeWithoutCallingRealUpdater(int exitCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mandocode-update-'" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "update.log");
            var windows = OperatingSystem.IsWindows();
            var script = ToolUpdateService.BuildScript(windows, int.MaxValue, "0.16.0", log);
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (windows)
            {
                // Shadow dotnet: this test must never update the developer's installation.
                script = $"function dotnet {{ $global:LASTEXITCODE = {exitCode}; 'mock updater' }}\n" + script;
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            }
            else
            {
                script = $"dotnet() {{ echo 'mock updater'; return {exitCode}; }}\n" + script;
                start.FileName = "/bin/sh";
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(script);
            }
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
            var errors = await stderr;
            Assert.True(File.Exists(log), errors);
            var result = await File.ReadAllTextAsync(log);
            Assert.Contains("mock updater", result);
            if (exitCode == 0)
            {
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("Update complete", result);
                Assert.DoesNotContain("Update failed", result);
            }
            else
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("Update failed", result);
                Assert.DoesNotContain("Update complete", result);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
