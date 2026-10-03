using System.Text.Json;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

public static class FileReferenceToken
{
    public static string Format(string path) => "@" + (Regex.IsMatch(path, @"^[\w.\-/\\]+$") ? path : JsonSerializer.Serialize(path));
    public static IEnumerable<string> Paths(string input)
    {
        foreach (Match match in Regex.Matches(input, "@(?:(?<quoted>\"(?:\\\\.|[^\"\\\\])*\")|(?<plain>[\\w.\\-/\\\\]+))"))
        {
            if (match.Groups["quoted"].Success)
            {
                string? path;
                try { path = JsonSerializer.Deserialize<string>(match.Groups["quoted"].Value); }
                catch (JsonException) { continue; }
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
            else yield return match.Groups["plain"].Value;
        }
    }
}
