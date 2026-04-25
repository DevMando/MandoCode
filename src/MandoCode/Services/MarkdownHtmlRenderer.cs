using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RazorConsole.Core.Rendering.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>
/// Renders markdown text as Spectre IRenderable by routing through RazorConsole's
/// MarkdownRenderingService (Markdig → HTML) and walking the resulting HTML with
/// HtmlAgilityPack, mirroring the styling choices RazorConsole's &lt;Markdown&gt;
/// component uses. Keeps MandoCode's imperative render call site while matching
/// the native RazorConsole output.
/// </summary>
public static class MarkdownHtmlRenderer
{
    private static readonly MarkdownRenderingService Markdown = new();

    // 2-second ceiling keeps a pathological input from burning the full 60s
    // render budget on regex backtracking before the watchdog in App.razor kicks in.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // Windows absolute paths and common Unix absolute paths — used to linkify bare
    // file references since Markdig only auto-links http(s) URLs. The leading
    // (?<![A-Za-z]) anchor prevents matching `s:` inside `https://…` and similar
    // schemes — without it, my linkifier mangles URLs the LLM already wrote.
    private static readonly Regex AbsoluteFilePathPattern = new(
        @"(?<![A-Za-z])([A-Za-z]:[/\\][^\s""'`<>|*?]+|/(?:mnt|home|usr|tmp|var|etc|opt)/[^\s""'`<>|*?]+)",
        RegexOptions.Compiled,
        RegexTimeout);

    // Relative paths like Games/index.html or src/MandoCode/Program.cs. Requires at
    // least one slash + a file extension to avoid matching version numbers or prose.
    // The lookbehinds keep us from matching inside existing markdown link syntax
    // (…](url) or [text]) and from consuming mid-URL text.
    private static readonly Regex RelativeFilePathPattern = new(
        @"(?<![\w./:])(?<!\[)(?<!\]\()(?:\./)?[\w-]+(?:/[\w.-]+)+\.[A-Za-z0-9]{1,10}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    // Match fenced code blocks so the file-path linkifier skips them.
    private static readonly Regex FencedCodeBlock = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1","h2","h3","h4","h5","h6",
        "p","div","pre","blockquote","hr",
        "ul","ol","li","table","thead","tbody","tfoot","tr","th","td"
    };

    public static void Render(string markdown, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        AnsiConsole.Write(BuildRenderable(markdown, projectRoot));
    }

    public static IRenderable BuildRenderable(string markdown, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new Text("");

        var preprocessed = LinkifyFilePaths(markdown, projectRoot);
        var html = Markdown.ConvertToHtml(preprocessed);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // MarkdownRenderingService wraps output in <div>…</div>. Unwrap.
        var root = doc.DocumentNode.SelectSingleNode("/div") ?? doc.DocumentNode;

        var renderables = new List<IRenderable>();
        foreach (var child in root.ChildNodes)
        {
            var r = TranslateBlock(child);
            if (r != null) renderables.Add(r);
        }

        return renderables.Count switch
        {
            0 => new Text(""),
            1 => renderables[0],
            _ => new Rows(renderables),
        };
    }

    // ── Block-level ──────────────────────────────────────────────────

    private static IRenderable? TranslateBlock(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText);
            return string.IsNullOrWhiteSpace(text) ? null : new Markup(Spectre.Console.Markup.Escape(text));
        }

        if (node.NodeType != HtmlNodeType.Element) return null;

        switch (node.Name.ToLowerInvariant())
        {
            case "h1": case "h2": case "h3":
            case "h4": case "h5": case "h6":
                return TranslateHeading(node);

            case "p":
                return TranslateParagraph(node);

            case "ul":
            case "ol":
                return TranslateList(node);

            case "pre":
                return TranslateCodeBlock(node);

            case "blockquote":
                return TranslateBlockquote(node);

            case "hr":
                return new Rule { Style = new Style(Color.Grey) };

            case "table":
                return TranslateTable(node);

            case "div":
                return TranslateContainer(node);

            default:
                var sb = new StringBuilder();
                AppendInlines(node, sb);
                var s = sb.ToString();
                return string.IsNullOrEmpty(s) ? null : new Markup(s);
        }
    }

    private static IRenderable? TranslateContainer(HtmlNode node)
    {
        var items = new List<IRenderable>();
        foreach (var child in node.ChildNodes)
        {
            var r = TranslateBlock(child);
            if (r != null) items.Add(r);
        }

        return items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => new Rows(items),
        };
    }

    private static IRenderable TranslateHeading(HtmlNode node)
    {
        var level = int.Parse(node.Name.AsSpan(1));
        var prefix = new string('#', level) + " ";
        var style = level switch
        {
            1 => new Style(Color.Yellow, decoration: Decoration.Bold),
            2 => new Style(Color.Cyan1, decoration: Decoration.Bold),
            3 => new Style(Color.Green, decoration: Decoration.Bold),
            4 => new Style(Color.Blue, decoration: Decoration.Bold),
            5 => new Style(Color.Magenta1, decoration: Decoration.Bold),
            _ => new Style(Color.Grey, decoration: Decoration.Bold),
        };

        var text = HtmlEntity.DeEntitize(node.InnerText);
        return new Markup(Spectre.Console.Markup.Escape(prefix + text), style);
    }

    private static IRenderable TranslateParagraph(HtmlNode node)
    {
        var sb = new StringBuilder();
        AppendInlines(node, sb);
        return new Markup(sb.ToString());
    }

    private static IRenderable TranslateList(HtmlNode node)
    {
        var isOrdered = string.Equals(node.Name, "ol", StringComparison.OrdinalIgnoreCase);
        var start = 1;
        var startAttr = node.GetAttributeValue("start", string.Empty);
        if (isOrdered && int.TryParse(startAttr, out var parsedStart))
            start = parsedStart;

        var items = new List<IRenderable>();
        var itemIndex = 0;

        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType != HtmlNodeType.Element ||
                !string.Equals(child.Name, "li", StringComparison.OrdinalIgnoreCase))
                continue;

            var prefix = isOrdered ? $"{start + itemIndex}. " : "• ";
            var content = BuildListItemContent(child);

            items.Add(new Columns(new IRenderable[] { new Markup(prefix), content })
            {
                Expand = false,
                Padding = new Padding(0, 0, 0, 0),
            });

            itemIndex++;
        }

        return new Rows(items) { Expand = false };
    }

    private static IRenderable BuildListItemContent(HtmlNode li)
    {
        var blocks = new List<IRenderable>();
        var inlineBuffer = new StringBuilder();

        void FlushInline()
        {
            if (inlineBuffer.Length > 0)
            {
                blocks.Add(new Markup(inlineBuffer.ToString()));
                inlineBuffer.Clear();
            }
        }

        foreach (var child in li.ChildNodes)
        {
            // Unwrap <p> directly inside <li>. Markdig emits <p>-wrapped items
            // whenever a list is "loose" (has blank lines between items), and the
            // default block layout adds visual spacing we don't want for LLM output.
            // Multi-paragraph items get a blank-line separator so a label paragraph
            // doesn't visually collapse onto its explanation paragraph.
            if (child.NodeType == HtmlNodeType.Element &&
                string.Equals(child.Name, "p", StringComparison.OrdinalIgnoreCase))
            {
                if (inlineBuffer.Length > 0) inlineBuffer.Append("\n\n");
                AppendInlines(child, inlineBuffer);
            }
            else if (IsBlockElement(child))
            {
                FlushInline();
                var block = TranslateBlock(child);
                if (block != null) blocks.Add(block);
            }
            else
            {
                AppendInlineNode(child, inlineBuffer);
            }
        }
        FlushInline();

        return blocks.Count switch
        {
            0 => new Markup(string.Empty),
            1 => blocks[0],
            _ => new Rows(blocks),
        };
    }

    private static IRenderable TranslateCodeBlock(HtmlNode pre)
    {
        var codeNode = pre.SelectSingleNode(".//code");
        var code = HtmlEntity.DeEntitize(codeNode?.InnerText ?? pre.InnerText).TrimEnd();

        string? language = null;
        if (codeNode != null)
        {
            var classAttr = codeNode.GetAttributeValue("class", string.Empty);
            if (!string.IsNullOrEmpty(classAttr))
            {
                foreach (var cls in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (cls.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
                    {
                        language = cls.Substring("language-".Length);
                        break;
                    }
                }
            }
        }

        string highlighted;
        try
        {
            highlighted = SyntaxHighlighter.Highlight(code, language ?? string.Empty);
        }
        catch
        {
            highlighted = Spectre.Console.Markup.Escape(code);
        }

        var panel = new Panel(new Markup(highlighted))
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);

        if (!string.IsNullOrEmpty(language))
        {
            panel.Header = new PanelHeader(
                $"[cyan] {Spectre.Console.Markup.Escape(language)} [/]",
                Justify.Left);
        }

        return panel;
    }

    private static IRenderable TranslateBlockquote(HtmlNode node)
    {
        var items = new List<IRenderable>();
        foreach (var child in node.ChildNodes)
        {
            var r = TranslateBlock(child);
            if (r != null) items.Add(r);
        }

        IRenderable content = items.Count switch
        {
            0 => new Text(string.Empty),
            1 => items[0],
            _ => new Rows(items),
        };

        return new Panel(content)
        {
            Border = BoxBorder.None,
            Padding = new Padding(2, 0, 0, 0),
        };
    }

    private static IRenderable TranslateTable(HtmlNode node)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("dim"));

        var thead = node.SelectSingleNode("./thead");
        var tbody = node.SelectSingleNode("./tbody");
        var hasColumns = false;

        if (thead != null)
        {
            var headerRow = thead.SelectSingleNode("./tr");
            if (headerRow != null)
            {
                foreach (var cell in HeaderCells(headerRow))
                {
                    var sb = new StringBuilder();
                    AppendInlines(cell, sb);
                    table.AddColumn(new TableColumn(new Markup(sb.ToString())));
                }
                hasColumns = true;
            }
        }

        var bodyContainer = tbody ?? node;
        foreach (var tr in DirectChildren(bodyContainer, "tr"))
        {
            var cells = new List<IRenderable>();
            foreach (var cell in BodyCells(tr))
            {
                var sb = new StringBuilder();
                AppendInlines(cell, sb);
                cells.Add(new Markup(sb.ToString()));
            }

            if (!hasColumns)
            {
                for (var i = 0; i < cells.Count; i++)
                    table.AddColumn(string.Empty);
                hasColumns = true;
            }

            while (cells.Count < table.Columns.Count)
                cells.Add(new Markup(string.Empty));

            table.AddRow(cells.ToArray());
        }

        return table;
    }

    private static IEnumerable<HtmlNode> DirectChildren(HtmlNode parent, string name)
    {
        foreach (var child in parent.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Element &&
                string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return child;
        }
    }

    private static IEnumerable<HtmlNode> HeaderCells(HtmlNode tr)
    {
        foreach (var child in tr.ChildNodes)
        {
            if (child.NodeType != HtmlNodeType.Element) continue;
            if (string.Equals(child.Name, "th", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.Name, "td", StringComparison.OrdinalIgnoreCase))
                yield return child;
        }
    }

    private static IEnumerable<HtmlNode> BodyCells(HtmlNode tr) => HeaderCells(tr);

    private static bool IsBlockElement(HtmlNode node)
        => node.NodeType == HtmlNodeType.Element && BlockTags.Contains(node.Name);

    // ── Inline rendering ────────────────────────────────────────────

    private static void AppendInlines(HtmlNode parent, StringBuilder sb)
    {
        foreach (var child in parent.ChildNodes)
            AppendInlineNode(child, sb);
    }

    private static void AppendInlineNode(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            sb.Append(Spectre.Console.Markup.Escape(HtmlEntity.DeEntitize(node.InnerText)));
            return;
        }

        if (node.NodeType != HtmlNodeType.Element) return;

        switch (node.Name.ToLowerInvariant())
        {
            case "strong":
            case "b":
                Wrap(node, sb, "[bold]", "[/]");
                break;

            case "em":
            case "i":
                Wrap(node, sb, "[italic]", "[/]");
                break;

            case "del":
            case "s":
                Wrap(node, sb, "[strikethrough]", "[/]");
                break;

            case "ins":
            case "u":
                Wrap(node, sb, "[underline]", "[/]");
                break;

            case "mark":
                Wrap(node, sb, "[black on yellow]", "[/]");
                break;

            case "code":
                // Inline code: soft purple on dark grey, matching the project's
                // synthwave palette. Sits visually distinct from the syntax
                // highlighter's bright [magenta] used for numeric literals inside
                // code blocks. Don't allow nested formatting — escape raw text.
                sb.Append("[mediumpurple1]");
                sb.Append(Spectre.Console.Markup.Escape(HtmlEntity.DeEntitize(node.InnerText)));
                sb.Append("[/]");
                break;

            case "a":
                var href = node.GetAttributeValue("href", string.Empty);
                if (!string.IsNullOrWhiteSpace(href))
                {
                    sb.Append($"[link={Spectre.Console.Markup.Escape(href)}]");
                    AppendInlines(node, sb);
                    sb.Append("[/]");
                }
                else
                {
                    AppendInlines(node, sb);
                }
                break;

            case "br":
                sb.Append('\n');
                break;

            case "small":
            case "sub":
            case "sup":
                Wrap(node, sb, "[dim]", "[/]");
                break;

            default:
                AppendInlines(node, sb);
                break;
        }
    }

    private static void Wrap(HtmlNode node, StringBuilder sb, string open, string close)
    {
        sb.Append(open);
        AppendInlines(node, sb);
        sb.Append(close);
    }

    // ── Preprocessing ───────────────────────────────────────────────

    /// <summary>
    /// Converts bare file paths into markdown autolinks so they render as clickable
    /// OSC 8 hyperlinks. Handles both absolute paths (Windows C:\… and common Unix
    /// roots) and relative paths resolved against <paramref name="projectRoot"/>.
    /// Skips fenced code blocks so intra-code paths aren't mangled. Relative paths
    /// are only linkified when the resolved file actually exists — prevents
    /// false positives on version numbers and sentence fragments.
    /// </summary>
    private static string LinkifyFilePaths(string markdown, string? projectRoot)
    {
        try
        {
            var sb = new StringBuilder();
            var lastIndex = 0;

            foreach (Match fence in FencedCodeBlock.Matches(markdown))
            {
                if (fence.Index > lastIndex)
                    AppendLinkified(markdown, lastIndex, fence.Index - lastIndex, sb, projectRoot);

                sb.Append(markdown, fence.Index, fence.Length);
                lastIndex = fence.Index + fence.Length;
            }

            if (lastIndex < markdown.Length)
                AppendLinkified(markdown, lastIndex, markdown.Length - lastIndex, sb, projectRoot);

            return sb.ToString();
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input — skip linkification and render the raw markdown.
            return markdown;
        }
    }

    private static void AppendLinkified(string source, int start, int length, StringBuilder output, string? projectRoot)
    {
        var segment = source.Substring(start, length);

        segment = AbsoluteFilePathPattern.Replace(segment, match =>
        {
            var path = match.Value;

            // Skip if the match ends in a path separator. This usually means the
            // LLM wrapped a long path mid-line and the regex stopped at the newline
            // with a trailing `\`. If we wrap such a path as `[path\](url)`, Markdig
            // reads `\]` as a CommonMark backslash-escape of `]`, the link text
            // never closes, and the raw markdown leaks into the rendered output.
            if (path.EndsWith("\\", StringComparison.Ordinal) ||
                path.EndsWith("/", StringComparison.Ordinal))
                return path;

            try
            {
                var normalized = path.Replace('\\', '/');
                if (!normalized.StartsWith("/", StringComparison.Ordinal))
                    normalized = "/" + normalized;
                return $"[{path}](file://{normalized})";
            }
            catch
            {
                return path;
            }
        });

        if (!string.IsNullOrEmpty(projectRoot))
        {
            segment = RelativeFilePathPattern.Replace(segment, match =>
            {
                var path = match.Value;
                try
                {
                    var resolved = Path.GetFullPath(Path.Combine(projectRoot, path));
                    if (!File.Exists(resolved))
                        return path;
                    var uri = new Uri(resolved).AbsoluteUri;
                    return $"[{path}]({uri})";
                }
                catch
                {
                    return path;
                }
            });
        }

        output.Append(segment);
    }
}
