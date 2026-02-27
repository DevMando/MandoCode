namespace MandoCode.Services;

/// <summary>
/// Generates OSC 8 clickable file hyperlinks for terminal output.
/// </summary>
public static class FileLinkHelper
{
    /// <summary>
    /// Wraps a relative file path in an OSC 8 hyperlink so clicking it
    /// opens the file in the user's default editor / handler.
    /// </summary>
    public static string FileLink(string projectRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        return $"\u001b]8;;file://{fullPath}\u0007{relativePath}\u001b]8;;\u0007";
    }
}
