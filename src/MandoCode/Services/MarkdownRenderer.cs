using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using System.Text;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;

namespace MandoCode.Services;

/// <summary>
/// Renders markdown text as rich terminal output using Spectre.Console widgets and ANSI escape codes.
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

        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var block in document)
        {
            RenderBlock(block, indent: 0);
        }
    }

    private static void RenderBlock(Block block, int indent)
    {
        switch (block)
        {
            case ThematicBreakBlock:
                AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));
                Console.WriteLine();
                break;

            case HeadingBlock heading:
                RenderHeading(heading);
                break;

            case ParagraphBlock paragraph:
                RenderParagraph(paragraph, indent);
                break;

            case FencedCodeBlock fenced:
                RenderCodeBlock(fenced);
                break;

            case CodeBlock code:
                RenderCodeBlock(code);
                break;

            case ListBlock list:
                RenderList(list, indent);
                break;

            case QuoteBlock quote:
                RenderQuote(quote);
                break;

            case MarkdigTable table:
                RenderTable(table);
                break;

            case HtmlBlock html:
                Console.Write(Grey);
                Console.Write(GetLeafContent(html));
                Console.WriteLine(Reset);
                Console.WriteLine();
                break;

            default:
                // For any container block, render children
                if (block is ContainerBlock container)
                {
                    foreach (var child in container)
                        RenderBlock(child, indent);
                }
                break;
        }
    }

    // ── Block renderers ─────────────────────────────────────────────

    private static void RenderHeading(HeadingBlock heading)
    {
        Console.Write($"{Bold}{Yellow}");

        if (heading.Level <= 3)
            Console.Write($"{new string('#', heading.Level)} ");

        Console.Write(GetPlainText(heading.Inline));
        Console.WriteLine(Reset);

        if (heading.Level <= 2)
            AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("dim")));

        Console.WriteLine();
    }

    private static void RenderParagraph(ParagraphBlock paragraph, int indent)
    {
        if (indent > 0)
            Console.Write(new string(' ', indent));

        RenderInlines(paragraph.Inline);
        Console.Write(Reset);
        Console.WriteLine();
        Console.WriteLine();
    }

    private static void RenderCodeBlock(LeafBlock codeBlock)
    {
        var language = (codeBlock as FencedCodeBlock)?.Info ?? "";
        var code = GetLeafContent(codeBlock).TrimEnd();

        var text = new Text(code, Style.Parse("grey"));
        var panel = new Panel(text)
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);

        if (!string.IsNullOrEmpty(language))
        {
            panel.Header = new PanelHeader($"[cyan] {language} [/]", Justify.Left);
        }

        AnsiConsole.Write(panel);
        Console.WriteLine();
    }

    private static void RenderList(ListBlock list, int indent)
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
                    Console.Write($"{prefix}{bullet}");
                    RenderInlines(para.Inline);
                    Console.Write(Reset);
                    Console.WriteLine();
                    isFirst = false;
                }
                else if (child is ListBlock nested)
                {
                    RenderList(nested, indent + 4);
                }
                else if (child is ParagraphBlock subPara)
                {
                    Console.Write($"{prefix}{continuation}");
                    RenderInlines(subPara.Inline);
                    Console.Write(Reset);
                    Console.WriteLine();
                }
                else
                {
                    RenderBlock(child, indent + bullet.Length);
                }
            }

            index++;
        }

        // Spacing after top-level lists
        if (indent == 0)
            Console.WriteLine();
    }

    private static void RenderQuote(QuoteBlock quote)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock para)
            {
                Console.Write($"{Grey}  \u2502 {FgReset}");
                RenderInlines(para.Inline);
                Console.Write(Reset);
                Console.WriteLine();
            }
            else if (child is QuoteBlock nested)
            {
                // Nested blockquote — add another │ prefix
                foreach (var nestedChild in nested)
                {
                    if (nestedChild is ParagraphBlock nestedPara)
                    {
                        Console.Write($"{Grey}  \u2502 \u2502 {FgReset}");
                        RenderInlines(nestedPara.Inline);
                        Console.Write(Reset);
                        Console.WriteLine();
                    }
                    else
                    {
                        RenderBlock(nestedChild, 6);
                    }
                }
            }
            else
            {
                Console.Write($"{Grey}  \u2502 {FgReset}");
                RenderBlock(child, 4);
            }
        }
        Console.WriteLine();
    }

    private static void RenderTable(MarkdigTable table)
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
                // Ensure columns exist if there was no explicit header row
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

        AnsiConsole.Write(spectreTable);
        Console.WriteLine();
    }

    // ── Inline rendering ────────────────────────────────────────────

    private static void RenderInlines(ContainerInline? container)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Console.Write(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    var isBold = emphasis.DelimiterCount >= 2;
                    var isItalic = emphasis.DelimiterCount % 2 == 1;

                    if (isBold) Console.Write(Bold);
                    if (isItalic) Console.Write(Italic);

                    RenderInlines(emphasis);

                    if (isItalic) Console.Write(ItalicOff);
                    if (isBold) Console.Write(BoldOff);
                    break;

                case CodeInline code:
                    Console.Write($"{Cyan}`{code.Content}`{FgReset}");
                    break;

                case LinkInline link when link.IsImage:
                    Console.Write($"{Grey}[Image: ");
                    var altText = GetPlainText(link);
                    Console.Write(altText);
                    Console.Write("]");
                    if (!string.IsNullOrEmpty(link.Url))
                        Console.Write($" ({link.Url})");
                    Console.Write(FgReset);
                    break;

                case LinkInline link:
                    var linkText = GetPlainText(link);
                    RenderInlines(link);
                    if (!string.IsNullOrEmpty(link.Url) && link.Url != linkText)
                        Console.Write($"{Grey} ({link.Url}){FgReset}");
                    break;

                case AutolinkInline autolink:
                    Console.Write($"{Cyan}{autolink.Url}{FgReset}");
                    break;

                case LineBreakInline:
                    Console.WriteLine();
                    break;

                case HtmlEntityInline entity:
                    Console.Write(entity.Transcoded.ToString());
                    break;

                case HtmlInline html:
                    Console.Write($"{Grey}{html.Tag}{FgReset}");
                    break;

                default:
                    if (inline is ContainerInline childContainer)
                        RenderInlines(childContainer);
                    break;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
