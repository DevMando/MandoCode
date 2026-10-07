using System.Diagnostics;
using System.Reflection;
using MandoCode.Services;

// This developer harness always replaces dotnet with a shell function. It cannot
// install or update packages. It shares the production helper implementation.
if (args.Length == 3 && args[0] == "--child")
{
    var directory = Path.GetFullPath(args[1]);
    var exitCode = int.Parse(args[2]);
    if (exitCode is not (0 or 1)) throw new ArgumentException("Only simulated success/failure is supported.");
    var updater = new ToolUpdateService();
    updater.Prepare("0.16.0", directory);
    var scriptPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "update.ps1" : "update.sh");
    var mock = OperatingSystem.IsWindows()
        ? $"function dotnet {{ 'SIMULATION: would run dotnet ' + ($args -join ' '); $global:LASTEXITCODE = {exitCode} }}\n"
        : $"dotnet() {{ echo \"SIMULATION: would run dotnet $*\"; return {exitCode}; }}\n";
    File.WriteAllText(scriptPath, mock + File.ReadAllText(scriptPath));
    updater.Launch();
    // Keep the parent alive: the helper must not execute dotnet until we exit.
    await Task.Delay(2000);
    if (File.Exists(updater.LogPath)) throw new InvalidOperationException("The updater ran before its parent exited.");
    Console.WriteLine("PASS: updater has not started while its parent is running.");
    return;
}

var root = Path.Combine(Environment.CurrentDirectory, "bin", "update-simulation", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
foreach (var exitCode in new[] { 0, 1 })
{
    var directory = Path.Combine(root, exitCode == 0 ? "success" : "failure");
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--child", directory, exitCode.ToString() }) start.ArgumentList.Add(arg);
    using var child = Process.Start(start) ?? throw new IOException("Could not start simulated MandoCode process.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
    var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
    await child.WaitForExitAsync(timeout.Token);
    var output = await stdout;
    var errors = await stderr;
    if (child.ExitCode != 0) throw new InvalidOperationException(output + errors);
    Console.WriteLine(output);
    var logPath = Path.Combine(directory, "update.log");
    string log = "";
    while (!timeout.IsCancellationRequested)
    {
        try { if (File.Exists(logPath)) log = await File.ReadAllTextAsync(logPath, timeout.Token); }
        catch (IOException) { /* The helper may still be writing. */ }
        if (log.Contains("Update complete") || log.Contains("Update failed")) break;
        await Task.Delay(100, timeout.Token);
    }
    if (!log.Contains("SIMULATION: would run dotnet tool update --global MandoCode --version 0.16.0 --configfile"))
        throw new InvalidOperationException("The simulated tool command was not executed: " + log);
    var expected = exitCode == 0 ? "Update complete" : "Update failed";
    var unexpected = exitCode == 0 ? "Update failed" : "Update complete";
    if (!log.Contains(expected) || log.Contains(unexpected)) throw new InvalidOperationException("Unexpected update result: " + log);
    Console.WriteLine($"PASS: simulated {(exitCode == 0 ? "success" : "failure")} after parent exit.");
    Console.WriteLine(log);
}
Console.WriteLine("Simulation complete. No installed tool was changed.");
Console.WriteLine("Logs: " + root);
