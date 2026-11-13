using System.Diagnostics;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>
/// A fact that is skipped, with the reason shown in the test report, when this machine does not let the test create
/// symbolic links (Windows without developer mode or elevation). xunit 2 has no runtime skip, so the probe runs at discovery.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!LinkProbe.CanCreateSymlinks)
            Skip = "This OS/user cannot create symbolic links (on Windows: enable Developer Mode or run elevated).";
    }
}

/// <summary>A fact for directory junctions: Windows only, and skipped elsewhere with the reason.</summary>
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Junctions exist only on Windows (the symbolic-link test covers the same guard elsewhere).";
        else if (!LinkProbe.CanCreateJunctions)
            Skip = "Could not create a directory junction with mklink /J on this machine.";
    }
}

internal static class LinkProbe
{
    public static readonly bool CanCreateSymlinks = Probe(() =>
    {
        var target = Path.Combine(Path.GetTempPath(), "mandocode-probe-t-" + Guid.NewGuid().ToString("N"));
        var link = target + "-link";
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        finally
        {
            try { Directory.Delete(link); } catch (Exception) { }
            try { Directory.Delete(target); } catch (Exception) { }
        }
    });

    public static readonly bool CanCreateJunctions = OperatingSystem.IsWindows() && Probe(() =>
    {
        var target = Path.Combine(Path.GetTempPath(), "mandocode-probe-j-" + Guid.NewGuid().ToString("N"));
        var link = target + "-link";
        Directory.CreateDirectory(target);
        try
        {
            return CreateJunction(link, target);
        }
        finally
        {
            try { Directory.Delete(link); } catch (Exception) { }
            try { Directory.Delete(target); } catch (Exception) { }
        }
    });

    /// <summary><c>mklink /J</c> needs neither elevation nor developer mode.</summary>
    public static bool CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static bool Probe(Func<bool> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return false; }
    }
}
