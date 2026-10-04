using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Services;

/// <summary>
/// Result of a successful update check when a newer stable release exists on NuGet.
/// </summary>
public sealed record UpdateInfo(string CurrentVersion, string LatestVersion)
{
    /// <summary>The exact command the user should run to upgrade the global tool.</summary>
    public string UpdateCommand => "dotnet tool update -g MandoCode";
}

/// <summary>
/// Checks NuGet for a newer published version of MandoCode and, when one exists, hands back
/// an <see cref="UpdateInfo"/> the UI can surface ("run dotnet tool update -g MandoCode").
///
/// Design (mirrors how dotnet/gh/npm nag about updates):
///   • Non-blocking — callers fire-and-forget; startup is never gated on the network.
///   • Throttled  — the NuGet query runs at most once per <see cref="CheckInterval"/>; the
///                  last result is cached in ~/.mandocode/update-check.json and reused
///                  (so we keep nagging between checks without re-hitting the network).
///   • Fail-silent — any error (offline, DNS, malformed JSON) returns null. No nag, no crash.
///   • Opt-out    — MANDOCODE_NO_UPDATE_CHECK set to anything disables it entirely (CI, etc.).
/// </summary>
public sealed class UpdateCheckService
{
    // Lowercase package id — the flat-container API is case-sensitive on the path segment.
    private const string PackageId = "mandocode";
    private const string FlatContainerUrl = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json";
    private const string OptOutEnvVar = "MANDOCODE_NO_UPDATE_CHECK";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns an <see cref="UpdateInfo"/> if a newer stable version is available, otherwise
    /// null (up to date, opted out, throttled-and-up-to-date, or any failure). Never throws.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutEnvVar)))
                return null;

            var current = GetCurrentVersion();
            if (current is null)
                return null;

            var latest = await GetLatestVersionAsync(cancellationToken);
            if (latest is null)
                return null;

            return latest > current
                ? new UpdateInfo(VersionToString(current), VersionToString(latest))
                : null;
        }
        catch
        {
            // Fail silent — an update nag is never worth surfacing an error for.
            return null;
        }
    }

    /// <summary>
    /// Resolves the latest stable version, preferring the 24h cache and only hitting NuGet
    /// when the cache is missing or stale. A fresh network result is written back to the cache.
    /// </summary>
    private async Task<Version?> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath();
        var cached = ReadCache(cachePath);
        if (cached is not null && DateTime.UtcNow - cached.LastCheckUtc < CheckInterval)
            return ParseStable(cached.LatestVersion);

        var fetched = await FetchLatestFromNuGetAsync(cancellationToken);
        if (fetched is null)
            // Network failed — fall back to whatever we last cached so we still nag if a
            // newer version was already known. Null if we've never had a successful check.
            return cached is null ? null : ParseStable(cached.LatestVersion);

        WriteCache(cachePath, new UpdateCheckCache
        {
            LastCheckUtc = DateTime.UtcNow,
            LatestVersion = VersionToString(fetched)
        });
        return fetched;
    }

    private static async Task<Version?> FetchLatestFromNuGetAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = HttpTimeout };
        var json = await client.GetStringAsync(FlatContainerUrl, cancellationToken);
        var doc = JsonSerializer.Deserialize<FlatContainerIndex>(json, JsonOptions);
        if (doc?.Versions is not { Count: > 0 })
            return null;

        // Stable releases only — skip prerelease tags (anything with a '-' suffix).
        Version? max = null;
        foreach (var v in doc.Versions)
        {
            var parsed = ParseStable(v);
            if (parsed is not null && (max is null || parsed > max))
                max = parsed;
        }
        return max;
    }

    /// <summary>
    /// Reads the running assembly's version, stripping any "+commit" build metadata that
    /// SourceLink appends to the informational version. Returns null if it can't be parsed.
    /// </summary>
    private static Version? GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            if (plus >= 0) info = info[..plus];
            var parsed = ParseStable(info);
            if (parsed is not null) return parsed;
        }
        return asm.GetName().Version;
    }

    /// <summary>
    /// Parses a NuGet version to <see cref="Version"/>, rejecting prerelease tags. The
    /// prerelease/build suffix (after '-' or '+') is dropped so a stable "1.2.3" parses and
    /// a prerelease "1.2.3-beta" is rejected (returns null) so it never wins the max.
    /// </summary>
    private static Version? ParseStable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Contains('-')) return null; // prerelease — ignore
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return Version.TryParse(s, out var v) ? v : null;
    }

    // Normalize to the form NuGet/users expect ("0.13.0"), dropping an absent/zero revision.
    // Version leaves absent components as -1: "0.13.0" → Build=0, Revision=-1.
    private static string VersionToString(Version v)
    {
        if (v.Revision > 0) return v.ToString(4);
        if (v.Build >= 0) return v.ToString(3);
        return v.ToString(2);
    }

    private static string GetCachePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".mandocode", "update-check.json");
    }

    private static UpdateCheckCache? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string path, UpdateCheckCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch
        {
            // A cache we can't persist just means we re-check next launch — not fatal.
        }
    }

    private sealed class FlatContainerIndex
    {
        [JsonPropertyName("versions")]
        public List<string>? Versions { get; set; }
    }

    private sealed class UpdateCheckCache
    {
        [JsonPropertyName("lastCheckUtc")]
        public DateTime LastCheckUtc { get; set; }

        [JsonPropertyName("latestVersion")]
        public string LatestVersion { get; set; } = "";
    }
}
