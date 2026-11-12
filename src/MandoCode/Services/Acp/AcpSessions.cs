using System.Text.Json;

namespace MandoCode.Services.Acp;

/// <summary>What the connected agent can do with sessions (from its <c>initialize</c> answer).</summary>
public sealed record AcpSessionSupport(bool CanList, bool CanResume, bool CanLoad, bool CanClose)
{
    public static AcpSessionSupport None { get; } = new(false, false, false, false);

    /// <summary>The agent can continue an earlier session, by resuming it or by loading it (which replays history).</summary>
    public bool CanContinue => CanResume || CanLoad;
}

/// <summary>How the pane's first session was obtained.</summary>
public enum AcpSessionOrigin
{
    /// <summary>A fresh <c>session/new</c>.</summary>
    New,

    /// <summary>An earlier session continued with <c>session/resume</c> (nothing replayed).</summary>
    Resumed,

    /// <summary>An earlier session loaded with <c>session/load</c> (the agent replayed the conversation).</summary>
    Loaded,
}

/// <summary>An earlier session to continue when the agent starts.</summary>
public sealed record AcpResumeRequest(string SessionId);

/// <summary>One remembered session: which agent, which folder, which id. No conversation content is ever stored.</summary>
public sealed record AcpStoredSession(string SessionId, string Cwd, DateTimeOffset UpdatedAt);

/// <summary>
/// <c>~/.mandocode/acp-sessions.json</c>: the last session of each agent for each folder. Only ids, folders and a
/// timestamp are kept; the agent holds the conversation itself.
/// </summary>
public sealed class AcpSessionStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AcpSessionStore(string? path = null) => _path = path ?? DefaultPath();

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "acp-sessions.json");

    public AcpStoredSession? Get(string agentName, string folder)
    {
        lock (_gate)
            return Read().TryGetValue(Key(agentName, folder), out var stored) ? stored : null;
    }

    public void Set(string agentName, string folder, string sessionId)
    {
        lock (_gate)
        {
            var map = Read();
            map[Key(agentName, folder)] = new AcpStoredSession(sessionId, AcpPaths.Normalize(folder), DateTimeOffset.UtcNow);
            Write(map);
        }
    }

    public void Remove(string agentName, string folder)
    {
        lock (_gate)
        {
            var map = Read();
            if (map.Remove(Key(agentName, folder)))
                Write(map);
        }
    }

    private static string Key(string agentName, string folder) =>
        agentName.ToLowerInvariant() + "|" + AcpPaths.Normalize(folder).ToLowerInvariantOnWindows();

    private Dictionary<string, AcpStoredSession> Read()
    {
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<Dictionary<string, AcpStoredSession>>(File.ReadAllText(_path)) is { } stored)
                return new Dictionary<string, AcpStoredSession>(stored, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file means nothing is remembered: the next start is simply a fresh session.
        }

        return new Dictionary<string, AcpStoredSession>(StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, AcpStoredSession> map)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not save the ACP session list: {0}", ex.Message);
        }
    }
}

public static class AcpPaths
{
    /// <summary>The full path without a trailing separator, for comparing folders.</summary>
    public static string Normalize(string folder) =>
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } trimmed
            ? trimmed
            : Path.GetFullPath(folder);

    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Normalize(a), Normalize(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string ToLowerInvariantOnWindows(this string path) =>
        OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
}
