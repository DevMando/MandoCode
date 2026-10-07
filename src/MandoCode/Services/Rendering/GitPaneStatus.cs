using System.Collections.Concurrent;
using System.Diagnostics;

namespace MandoCode.Services;

public sealed record GitPaneSnapshot(string Branch, bool Dirty, bool Conflicted, int Ahead, int Behind)
{
    public string Label => Branch + (Ahead > 0 ? $" ↑{Ahead}" : "") + (Behind > 0 ? $" ↓{Behind}" : "");
}

/// <summary>Local, bounded Git queries, shared by panes working in the same folder.</summary>
public static class GitPaneStatus
{
    private sealed class Cache
    {
        public readonly SemaphoreSlim Gate = new(1);
        public DateTime Checked;
        public GitPaneSnapshot? Snapshot;
    }
    private static readonly ConcurrentDictionary<string, Cache> Entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async Task<GitPaneSnapshot?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var entry = Entries.GetOrAdd(Path.GetFullPath(root), _ => new Cache());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - entry.Checked < TimeSpan.FromSeconds(2)) return entry.Snapshot;
            entry.Snapshot = await QueryAsync(root, cancellationToken);
            entry.Checked = DateTime.UtcNow;
            return entry.Snapshot;
        }
        finally { entry.Gate.Release(); }
    }

    private static async Task<GitPaneSnapshot?> QueryAsync(string root, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var arg in new[] { "--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z", "--", "." })
            process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            return process.ExitCode == 0 ? Parse(await output) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static GitPaneSnapshot? Parse(string output)
    {
        string branch = "", oid = "";
        bool dirty = false, conflicted = false, skipOriginalPath = false;
        int ahead = 0, behind = 0;
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (skipOriginalPath) { skipOriginalPath = false; continue; }
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = record[14..].Trim();
            else if (record.StartsWith("# branch.oid ", StringComparison.Ordinal)) oid = record[13..].Trim();
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                foreach (var value in record[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (value[0] == '+') int.TryParse(value[1..], out ahead);
                    else if (value[0] == '-') int.TryParse(value[1..], out behind);
                }
            }
            else if (record.StartsWith("1 ") || record.StartsWith("2 ") || record.StartsWith("? ") || record.StartsWith("u "))
            {
                dirty = true;
                conflicted |= record.StartsWith("u ");
                skipOriginalPath = record.StartsWith("2 ");
            }
        }
        if (branch == "(detached)") branch = oid.Length >= 7 ? oid[..7] : oid;
        return branch.Length == 0 ? null : new(branch, dirty, conflicted, ahead, behind);
    }
}
