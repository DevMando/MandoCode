using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MandoCode.Translators;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;

namespace MandoCode.Services;

/// <summary>
/// Renders markdown text as rich terminal output using Spectre.Console widgets and ANSI escape codes.
/// Produces IRenderable objects that compose AnsiPassthroughRenderable (for inline ANSI/OSC 8 text)
/// with native Spectre.Console widgets (Rule, Panel, Table) for block-level elements.
/// </summary>
public static class MarkdownRenderer
{
    // ANSI escape codes — used for inline styling to avoid Spectre bracket-escaping issues
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Italic = "\u001b[3m";
    private const string BoldOff = "\u001b[22m";
    private const string ItalicOff = "\u001b[23m";
    private const string Cyan = "\u001b[36m";
    private const string Yellow = "\u001b[33m";
    private const string Grey = "\u001b[90m";
    private const string FgReset = "\u001b[39m"; // reset foreground only (preserves bold/italic)

    // Regex for detecting bare URLs and bare domain names in literal text
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"(https?://[^\s)\]>""]+|(?<![@\w])[\w][\w.-]*\.(?:com|org|net|io|dev|edu|gov|co|app|ai|us|uk|de|fr|jp|au|ca|ru|br|in|xyz|tech|info|biz|me|tv|cc)\b(?:/[^\s)\]>""]*)?)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Regex for detecting file paths (Windows absolute paths and Unix absolute paths)
    private static readonly System.Text.RegularExpressions.Regex FilePathPattern = new(
        @"([A-Za-z]:[/\\][^\s""'`<>|*?]+|/(?:mnt|home|usr|tmp|var|etc|opt)/[^\s""'`<>|*?]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    /// <summary>
    /// Parses markdown text and renders it to the terminal with rich formatting.
    /// </summary>
    public static void Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        AnsiConsole.Write(BuildRenderable(markdown));
    }

    /// <summary>
    /// Parses markdown text and returns a composed IRenderable containing
    /// native Spectre.Console widgets and AnsiPassthroughRenderable for inline text.
    /// </summary>
    public static IRenderable BuildRenderable(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new Text("");

        var document = Markdown.Parse(markdown, Pipeline);
        var renderables = new List<IRenderable>();

        foreach (var block in document)
        {
            BuildBlockRenderables(block, indent: 0, renderables);
        }

        return renderables.Count switch
        {
            0 => new Text(""),
            1 => renderables[0],
            _ => new Rows(renderables)
        };
    }

    // ── Block builders ──────────────────────────────────────────────

    private static void BuildBlockRenderables(Block block, int indent, List<IRenderable> renderables)
    {
        switch (block)
        {
            case ThematicBreakBlock:
                renderables.Add(new Rule().RuleStyle(Style.Parse("dim")));
                renderables.Add(new AnsiPassthroughRenderable("\n"));
                break;

            case HeadingBlock heading:
                BuildHeadingRenderables(heading, renderables);
                break;

            case ParagraphBlock paragraph:
                BuildParagraphRenderable(paragraph, indent, renderables);
                break;

            case FencedCodeBlock fenced:
                BuildCodeBlockRenderable(fenced, renderables);
                break;

            case CodeBlock code:
                BuildCodeBlockRenderable(code, renderables);
                break;

            case ListBlock list:
                BuildListRenderable(list, indent, renderables);
                break;

            case QuoteBlock quote:
                BuildQuoteRenderable(quote, renderables);
                break;

            case MarkdigTable table:
                BuildTableRenderable(table, renderables);
                break;

            case HtmlBlock html:
                var sb = new StringBuilder();
                sb.Append(Grey);
                sb.Append(GetLeafContent(html));
                sb.AppendLine(Reset);
                sb.AppendLine();
                renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
                break;

            default:
                if (block is ContainerBlock container)
                {
                    foreach (var child in container)
                        BuildBlockRenderables(child, indent, renderables);
                }
                break;
        }
    }

    private static void BuildHeadingRenderables(HeadingBlock heading, List<IRenderable> renderables)
    {
        var sb = new StringBuilder();
        sb.Append($"{Bold}{Yellow}");

        if (heading.Level <= 3)
            sb.Append($"{new string('#', heading.Level)} ");

        sb.Append(GetPlainText(heading.Inline));
        sb.AppendLine(Reset);

        renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));

        if (heading.Level <= 2)
            renderables.Add(new Rule().RuleStyle(Style.Parse("dim")));

        renderables.Add(new AnsiPassthroughRenderable("\n"));
    }

    private static void BuildParagraphRenderable(ParagraphBlock paragraph, int indent, List<IRenderable> renderables)
    {
        var sb = new StringBuilder();
        if (indent > 0)
            sb.Append(new string(' ', indent));

        BuildInlines(sb, paragraph.Inline);
        sb.Append(Reset);
        sb.AppendLine();
        sb.AppendLine();
        renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
    }

    private static void BuildCodeBlockRenderable(LeafBlock codeBlock, List<IRenderable> renderables)
    {
        var language = (codeBlock as FencedCodeBlock)?.Info ?? "";
        var code = GetLeafContent(codeBlock).TrimEnd();

        var highlighted = SyntaxHighlighter.Highlight(code, language);
        var content = new Markup(highlighted);
        var panel = new Panel(content)
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);

        if (!string.IsNullOrEmpty(language))
        {
            panel.Header = new PanelHeader($"[cyan] {language} [/]", Justify.Left);
        }

        renderables.Add(panel);
        renderables.Add(new AnsiPassthroughRenderable("\n"));
    }

    private static void BuildListRenderable(ListBlock list, int indent, List<IRenderable> renderables)
    {
        var index = 1;
        if (list.IsOrdered && !string.IsNullOrEmpty(list.OrderedStart))
            int.TryParse(list.OrderedStart, out index);

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
                continue;

            var bullet = list.IsOrdered ? $"{index}. " : "  \u2022 ";
            var continuation = new string(' ', bullet.Length);
            var prefix = new string(' ', indent);
            var isFirst = true;

            foreach (var child in listItem)
            {
                if (isFirst && child is ParagraphBlock para)
                {
                    var sb = new StringBuilder();
                    sb.Append($"{prefix}{bullet}");
                    BuildInlines(sb, para.Inline);
                    sb.Append(Reset);
                    sb.AppendLine();
                    renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
                    isFirst = false;
                }
                else if (child is ListBlock nested)
                {
                    BuildListRenderable(nested, indent + 4, renderables);
                }
                else if (child is ParagraphBlock subPara)
                {
                    var sb = new StringBuilder();
                    sb.Append($"{prefix}{continuation}");
                    BuildInlines(sb, subPara.Inline);
                    sb.Append(Reset);
                    sb.AppendLine();
                    renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
                }
                else
                {
                    BuildBlockRenderables(child, indent + bullet.Length, renderables);
                }
            }

            index++;
        }

        // Spacing after top-level lists
        if (indent == 0)
            renderables.Add(new AnsiPassthroughRenderable("\n"));
    }

    private static void BuildQuoteRenderable(QuoteBlock quote, List<IRenderable> renderables)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock para)
            {
                var sb = new StringBuilder();
                sb.Append($"{Grey}  \u2502 {FgReset}");
                BuildInlines(sb, para.Inline);
                sb.Append(Reset);
                sb.AppendLine();
                renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
            }
            else if (child is QuoteBlock nested)
            {
                foreach (var nestedChild in nested)
                {
                    if (nestedChild is ParagraphBlock nestedPara)
                    {
                        var sb = new StringBuilder();
                        sb.Append($"{Grey}  \u2502 \u2502 {FgReset}");
                        BuildInlines(sb, nestedPara.Inline);
                        sb.Append(Reset);
                        sb.AppendLine();
                        renderables.Add(new AnsiPassthroughRenderable(sb.ToString()));
                    }
                    else
                    {
                        BuildBlockRenderables(nestedChild, 6, renderables);
                    }
                }
            }
            else
            {
                var prefixSb = new StringBuilder();
                prefixSb.Append($"{Grey}  \u2502 {FgReset}");
                renderables.Add(new AnsiPassthroughRenderable(prefixSb.ToString()));
                BuildBlockRenderables(child, 4, renderables);
            }
        }
        renderables.Add(new AnsiPassthroughRenderable("\n"));
    }

    private static void BuildTableRenderable(MarkdigTable table, List<IRenderable> renderables)
    {
        var spectreTable = new Spectre.Console.Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("dim"));

        var headerProcessed = false;

        foreach (var row in table)
        {
            if (row is not MarkdigTableRow tableRow)
                continue;

            if (tableRow.IsHeader && !headerProcessed)
            {
                foreach (var cell in tableRow)
                {
                    if (cell is MarkdigTableCell tableCell)
                    {
                        var text = GetCellText(tableCell);
                        spectreTable.AddColumn(new TableColumn(
                            Spectre.Console.Markup.Escape(text)));
                    }
                }
                headerProcessed = true;
            }
            else
            {
                if (!headerProcessed)
                {
                    foreach (var cell in tableRow)
                        spectreTable.AddColumn("");
                    headerProcessed = true;
                }

                var cells = new List<string>();
                foreach (var cell in tableRow)
                {
                    if (cell is MarkdigTableCell tableCell)
                        cells.Add(Spectre.Console.Markup.Escape(GetCellText(tableCell)));
                }

                while (cells.Count < spectreTable.Columns.Count)
                    cells.Add("");

                spectreTable.AddRow(cells.ToArray());
            }
        }

        renderables.Add(spectreTable);
        renderables.Add(new AnsiPassthroughRenderable("\n"));
    }

    // ── Inline rendering (to StringBuilder) ─────────────────────────

    private static void BuildInlines(StringBuilder sb, ContainerInline? container)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    BuildLiteralWithLinks(sb, literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    var isBold = emphasis.DelimiterCount >= 2;
                    var isItalic = emphasis.DelimiterCount % 2 == 1;

                    if (isBold) sb.Append(Bold);
                    if (isItalic) sb.Append(Italic);

                    BuildInlines(sb, emphasis);

                    if (isItalic) sb.Append(ItalicOff);
                    if (isBold) sb.Append(BoldOff);
                    break;

                case CodeInline code:
                    var fileUri = TryGetFileUri(code.Content);
                    if (fileUri != null)
                    {
                        sb.Append($"\u001b]8;;{fileUri}\u0007{Cyan}`{code.Content}`{FgReset}\u001b]8;;\u0007");
                    }
                    else
                    {
                        sb.Append($"{Cyan}`{code.Content}`{FgReset}");
                    }
                    break;

                case LinkInline link when link.IsImage:
                    sb.Append($"{Grey}[Image: ");
                    sb.Append(GetPlainText(link));
                    sb.Append(']');
                    if (!string.IsNullOrEmpty(link.Url))
                        sb.Append($" ({link.Url})");
                    sb.Append(FgReset);
                    break;

                case LinkInline link:
                    var linkText = GetPlainText(link);
                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        sb.Append($"\u001b]8;;{link.Url}\u0007");
                        sb.Append(Cyan);
                        BuildInlines(sb, link);
                        if (link.Url != linkText)
                            sb.Append($" ({link.Url})");
                        sb.Append($"{FgReset}\u001b]8;;\u0007");
                    }
                    else
                    {
                        BuildInlines(sb, link);
                    }
                    break;

                case AutolinkInline autolink:
                    BuildHyperlink(sb, autolink.Url, autolink.Url);
                    break;

                case LineBreakInline:
                    sb.AppendLine();
                    break;

                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;

                case HtmlInline html:
                    sb.Append($"{Grey}{html.Tag}{FgReset}");
                    break;

                default:
                    if (inline is ContainerInline childContainer)
                        BuildInlines(sb, childContainer);
                    break;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void BuildHyperlink(StringBuilder sb, string url, string displayText, string color = Cyan)
    {
        sb.Append($"\u001b]8;;{url}\u0007{color}{displayText}{FgReset}\u001b]8;;\u0007");
    }

    /// <summary>
    /// Extracts plain text from an inline tree (stripping all formatting).
    /// </summary>
    private static string GetPlainText(ContainerInline? container)
    {
        if (container == null) return "";

        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline child:
                    sb.Append(GetPlainText(child));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the text content of a leaf block (code blocks, HTML blocks, etc.).
    /// </summary>
    private static string GetLeafContent(LeafBlock block)
    {
        var sb = new StringBuilder();
        var lines = block.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds literal text into StringBuilder, detecting bare URLs and wrapping them in OSC 8 hyperlinks.
    /// </summary>
    private static void BuildLiteralWithLinks(StringBuilder sb, string text)
    {
        var matches = new List<(int Index, int Length, string Display, string Url)>();

        foreach (System.Text.RegularExpressions.Match match in UrlPattern.Matches(text))
        {
            var display = match.Value;
            var url = display.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? display
                : $"https://{display}";
            matches.Add((match.Index, match.Length, display, url));
        }

        foreach (System.Text.RegularExpressions.Match match in FilePathPattern.Matches(text))
        {
            var uri = TryGetFileUri(match.Value);
            if (uri != null)
                matches.Add((match.Index, match.Length, match.Value, uri));
        }

        matches.Sort((a, b) => a.Index.CompareTo(b.Index));

        var lastIndex = 0;
        foreach (var (index, length, display, url) in matches)
        {
            if (index < lastIndex) continue;

            if (index > lastIndex)
                sb.Append(text[lastIndex..index]);

            BuildHyperlink(sb, url, display);
            lastIndex = index + length;
        }

        if (lastIndex < text.Length)
            sb.Append(text[lastIndex..]);
    }

    /// <summary>
    /// Checks if text looks like a file path and returns a file:// URI, or null.
    /// </summary>
    private static string? TryGetFileUri(string text)
    {
        if (!FilePathPattern.IsMatch(text))
            return null;

        try
        {
            var normalized = text.Replace('\\', '/');
            if (!normalized.StartsWith("/"))
                normalized = "/" + normalized;

            return "file://" + normalized;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts plain text from a table cell's child blocks.
    /// </summary>
    private static string GetCellText(MarkdigTableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var child in cell)
        {
            if (child is ParagraphBlock para)
                sb.Append(GetPlainText(para.Inline));
        }
        return sb.ToString();
    }
}
