using System.Reflection;

namespace MandoCode.Services;

/// <summary>
/// Builds the version string shown under the startup banner.
/// </summary>
/// <remarks>
/// Prefers the informational version so prerelease tags survive. <c>Assembly.GetName().Version</c>
/// is numeric-only and silently drops them, which made a test build indistinguishable from the
/// release it was cut from — the same confusion that makes a stale binary hard to spot.
/// </remarks>
public static class VersionLabel
{
    /// <summary>Version label for the running assembly, e.g. "v0.15.0-plan-test".</summary>
    public static string ForAssembly(Assembly assembly) => Build(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version);

    /// <summary>
    /// Pure formatting, split out so it can be tested without constructing an assembly.
    /// </summary>
    /// <param name="informationalVersion">
    /// e.g. <c>0.15.0-plan-test+a1a0df8</c>. Build metadata after '+' (appended by SourceLink) is
    /// dropped; the prerelease tag is kept, since telling builds apart at a glance is the point.
    /// </param>
    /// <param name="assemblyVersion">Numeric fallback for when the attribute is missing or empty.</param>
    public static string Build(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var trimmed = informationalVersion.Trim();
            var plus = trimmed.IndexOf('+');
            if (plus >= 0) trimmed = trimmed[..plus];
            if (!string.IsNullOrWhiteSpace(trimmed)) return $"v{trimmed}";
        }

        return assemblyVersion != null
            ? $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : "";
    }
}
