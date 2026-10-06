using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Spectre.Console;

namespace MandoCode.Services;

public static class CliAgentPresentation
{
    private static readonly string[] Avatars = ["🤖", "🛰️", "🧬", "💡", "🧠", "👾", "🚀", "🔬", "🔮", "💻", "🛸", "🔭", "🦾", "🧩", "💎", "🌐"];
    private static readonly Dictionary<string, string> Assigned = new(StringComparer.OrdinalIgnoreCase)
        { ["Quartz"] = "🤖", ["Lumen"] = "🛰️", ["Fusion"] = "🧬", ["Topaz"] = "💡", ["Bandit"] = "🛸" };
    public static string Avatar(string name)
    {
        lock (Assigned)
        {
            if (Assigned.TryGetValue(name, out var avatar)) return avatar;
            uint hash = 2166136261;
            foreach (var ch in name.ToUpperInvariant()) hash = unchecked((hash ^ ch) * 16777619);
            var index = (int)(hash % (uint)Avatars.Length);
            for (var offset = 0; offset < Avatars.Length; offset++)
            {
                avatar = Avatars[(index + offset) % Avatars.Length];
                if (!Assigned.Values.Contains(avatar)) return Assigned[name] = avatar;
            }
            return Assigned[name] = Avatars[index];
        }
    }
    public static string PlainName(string name) => $"{Avatar(name)} {name}";
    public static string Name(string name) => $"{Avatar(name)} [#c678dd]{Markup.Escape(name)}[/]";
    private static Regex? Pattern(IEnumerable<string> names)
    {
        var tokens = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)
            .Select(n => Regex.Escape(FileReferenceToken.Format(n))).ToArray();
        return tokens.Length == 0 ? null : new Regex(@"(?<![\w@/\\])(?:" + string.Join("|", tokens) + @")(?![\w/\\-]|\.[\w])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }
    public static string Inline(string text, IEnumerable<string> names)
    {
        var pattern = Pattern(names);
        if (pattern is null) return Markup.Escape(text);
        var result = new StringBuilder();
        var start = 0;
        foreach (Match match in pattern.Matches(text))
        {
            result.Append(Markup.Escape(text[start..match.Index]));
            result.Append(Name(FileReferenceToken.Paths(match.Value).First()));
            start = match.Index + match.Length;
        }
        return result.Append(Markup.Escape(text[start..])).ToString();
    }
    public static void Annotate(HtmlDocument document, IEnumerable<string> names)
    {
        var pattern = Pattern(names);
        if (pattern is null) return;
        foreach (var node in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Text).ToArray())
        {
            if (node.Ancestors().Any(n => n.Name is "code" or "pre" or "a" or "script" or "style")) continue;
            var text = HtmlEntity.DeEntitize(node.InnerText);
            var matches = pattern.Matches(text);
            if (matches.Count == 0) continue;
            var start = 0;
            foreach (Match match in matches)
            {
                node.ParentNode.InsertBefore(document.CreateTextNode(HtmlEntity.Entitize(text[start..match.Index])), node);
                var span = document.CreateElement("span");
                span.SetAttributeValue("data-cli-agent", "true");
                span.AppendChild(document.CreateTextNode(HtmlEntity.Entitize(FileReferenceToken.Paths(match.Value).First())));
                node.ParentNode.InsertBefore(span, node);
                start = match.Index + match.Length;
            }
            node.ParentNode.InsertBefore(document.CreateTextNode(HtmlEntity.Entitize(text[start..])), node);
            node.Remove();
        }
    }
}
