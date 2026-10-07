using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>Display-only command formatting; Exact is never modified or executed here.</summary>
public sealed record CommandPreview(string Exact, string Directory, string Shell, string Script)
{
    public bool HasScriptView => Script != Exact;
    public static CommandPreview Create(string command, string directory)
    {
        var match = Regex.Match(command, "^\\s*(?:powershell|pwsh)(?:\\.exe)?\\s+.*?-Command\\s+([\"'])([\\s\\S]*)\\1\\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return new(command, directory, "Command", command);
        var script = match.Groups[2].Value;
        // Leave complex quoting/here-strings untouched; the exact invocation is always available.
        if (script.Contains("@'", StringComparison.Ordinal) || script.Contains("@\"", StringComparison.Ordinal))
            return new(command, directory, "PowerShell", script);
        var formatted = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            formatted.Append(c);
            if (c == '`' && i + 1 < script.Length) { formatted.Append(script[++i]); continue; }
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < script.Length && script[i + 1] == quote) formatted.Append(script[++i]);
                    else quote = '\0';
                }
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == ';') { formatted.Append('\n'); while (i + 1 < script.Length && script[i + 1] == ' ') i++; }
        }
        return new(command, directory, "PowerShell", formatted.ToString());
    }
    public IRenderable Code(bool exact = false) => new Markup(SyntaxHighlighter.Highlight(exact ? Exact : Script, Shell == "PowerShell" ? "powershell" : "bash"));
    // Persist a complete, inspectable fallback for saved transcripts and non-component clients.
    public IRenderable Fallback() => new Panel(new Rows(HasScriptView ? [new Text("Working directory: " + Directory, new Style(Color.Grey)), Code(), new Text("Exact invocation:", new Style(Color.Grey)), new Text(Exact)] : new IRenderable[] { new Text("Working directory: " + Directory, new Style(Color.Grey)), Code() }))
        .Border(BoxBorder.Rounded).BorderStyle(new Style(new Color(86, 182, 194))).Header(Shell);
}