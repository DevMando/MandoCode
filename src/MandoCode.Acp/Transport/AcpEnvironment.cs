using System.Collections;

namespace MandoCode.Acp.Transport;

/// <summary>
/// Builds the environment an agent process starts with. A pure function so it can be tested
/// without launching anything.
/// </summary>
public static class AcpEnvironment
{
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";

    /// <summary>
    /// inherited environment, minus <c>ANTHROPIC_API_KEY</c> unless the agent entry opted in,
    /// with the entry's own <c>env</c> applied on top (an explicit value always wins).
    /// </summary>
    /// <remarks>
    /// Keeping the key out by default means the agent authenticates the way the user set it up
    /// (for example a Claude subscription login) instead of silently billing an API key that
    /// happens to be exported in the shell.
    /// </remarks>
    public static Dictionary<string, string> Build(
        IEnumerable<KeyValuePair<string, string>> inherited,
        AcpLaunchSpec spec,
        bool caseInsensitiveNames)
    {
        var comparer = caseInsensitiveNames ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);

        foreach (var (name, value) in inherited)
            result[name] = value;

        if (!spec.PassAnthropicApiKey)
            result.Remove(AnthropicApiKey);

        if (spec.Env is not null)
        {
            foreach (var (name, value) in spec.Env)
                result[name] = value;
        }

        return result;
    }

    /// <summary>The current process environment, in a form <see cref="Build"/> accepts.</summary>
    public static IEnumerable<KeyValuePair<string, string>> CaptureCurrent()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                yield return new KeyValuePair<string, string>(name, value);
        }
    }
}
