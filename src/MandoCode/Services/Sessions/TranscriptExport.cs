using System.Net;
using System.Text;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Standalone HTML export of the selected agent's full rendered conversation.</summary>
public static class TranscriptExport
{
    public static string BuildHtml(TuiSession session, string agent, string project, string model, DateTimeOffset savedAt)
    {
        var blocks = AgentArchiveStore.Capture(session);
        // The command invoking this export is not part of the saved conversation.
        if (blocks.LastOrDefault()?.UserPrompt is { } last && IsSaveCommand(last)) blocks.RemoveAt(blocks.Count - 1);
        if (blocks.Count == 0) throw new IOException("There is no conversation to save yet.");
        var html = new StringBuilder("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<title>").Append(E(agent)).Append(" — MandoCode transcript</title><style>");
        html.Append("body{margin:0;background:#15191e;color:#ddd;font:15px/1.5 system-ui,sans-serif}main{max-width:1200px;margin:auto;padding:24px}h1{font-size:24px}header{border-bottom:1px solid #414852;padding-bottom:16px;margin-bottom:24px}.meta{color:#aaa;overflow-wrap:anywhere}pre{font:14px/1.5 ui-monospace,Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;margin:0}.block{margin:0 0 6px}.spaced{margin-bottom:20px}.prompt{color:#ffc850;background:#20252c;border-left:3px solid #ffc850;padding:12px;margin:20px 0}.activity{color:#c678dd;margin:12px 0 4px}@media print{body{background:white;color:black}main{max-width:none;padding:0}.prompt{background:#f4f4f4;color:black}span{color:inherit!important;background:transparent!important}}</style></head><body><main><header><h1>");
        html.Append(E(agent)).Append(" — Conversation transcript</h1><div class=\"meta\">Project: ").Append(E(project));
        html.Append("<br>Model: ").Append(E(model)).Append("<br>Saved: ").Append(E(savedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))).Append("</div></header>");
        foreach (var block in blocks)
        {
            if (block.UserPrompt is { } prompt) { html.Append("<section class=\"prompt\"><strong>User</strong><pre>").Append(E(prompt)).Append("</pre></section>"); continue; }
            if (block.AgentActivity is { } exchange) html.Append("<div class=\"activity\">").Append(E(exchange.Title)).Append(" · ").Append(E(exchange.Status)).Append("</div>");
            if (block.ToolActivity is { } tools) html.Append("<div class=\"activity\">").Append(E(tools.Summary)).Append("</div>");
            if (block.Spans.Count == 0) continue;
            html.Append(block.SpaceAfter ? "<pre class=\"block spaced\">" : "<pre class=\"block\">");
            foreach (var span in block.Spans)
            {
                var css = new StringBuilder();
                if (Style.TryParse(span.Style, out var style) && style is not null) {
                    if (style.Foreground != Color.Default) css.Append($"color:#{style.Foreground.R:x2}{style.Foreground.G:x2}{style.Foreground.B:x2};");
                    if (style.Background != Color.Default) css.Append($"background:#{style.Background.R:x2}{style.Background.G:x2}{style.Background.B:x2};");
                    if (style.Decoration.HasFlag(Decoration.Bold)) css.Append("font-weight:bold;");
                    if (style.Decoration.HasFlag(Decoration.Italic)) css.Append("font-style:italic;");
                    if (style.Decoration.HasFlag(Decoration.Underline)) css.Append("text-decoration:underline;");
                }
                html.Append("<span style=\"").Append(css).Append("\">").Append(E(span.Text)).Append("</span>");
            }
            html.Append("</pre>");
        }
        return html.Append("</main></body></html>").ToString();
    }
    public static async Task<string> SaveAsync(TuiSession session, string agent, string project, string model, string argument = "", TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetLocalNow();
        var html = BuildHtml(session, agent, project, model, now);
        var name = $"mandocode-transcript-{now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid():N}.html";
        var raw = argument.Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length > 1) raw = raw[1..^1];
        var path = Path.GetFullPath(Path.Combine(project, raw.Length == 0 ? name : raw));
        if (Directory.Exists(path)) path = Path.Combine(path, name);
        if (Path.GetExtension(path).Length == 0) path += ".html";
        if (Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm")) throw new IOException("Use an .html file path for the transcript.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".mandocode-transcript-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            await File.WriteAllTextAsync(temporary, html, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path)) { throw new IOException("A transcript file already exists at " + path + ". Choose a new filename; existing files are not overwritten."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    private static bool IsSaveCommand(string text) => text.Trim().Equals("/transcript-save", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("/transcript-save ", StringComparison.OrdinalIgnoreCase);
    private static string E(string text) => WebUtility.HtmlEncode(text);
}