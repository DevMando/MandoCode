using MandoCode.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MandoCode.Tests;

public class MarkdownHtmlRendererTests
{
    private const string Osc8Prefix = "]8;;";

    [Theory]
    [InlineData("")]
    [InlineData("text")]
    [InlineData("output")]
    public void Output_blocks_do_not_highlight_numbers(string language)
    {
        var rendered = RenderToString(MarkdownHtmlRenderer.BuildRenderable($"```{language}\nIteration 0\nIteration 1\n```"));
        Assert.Contains("Iteration 0", rendered);
        Assert.DoesNotContain("\u001b[38;2;255;0;255", rendered);
    }

    [Fact]
    public void Csharp_label_is_readable_and_comments_have_explicit_grey()
    {
        var rendered = RenderPlain(MarkdownHtmlRenderer.BuildRenderable("```csharp\n// explanation\nint count = 5;\n```"));
        Assert.Contains("C#", rendered);
        Assert.DoesNotContain("csharp", rendered);
        Assert.Contains("[grey62]// explanation[/]", SyntaxHighlighter.Highlight("// explanation", "csharp"));
    }

    [Fact]
    public void Linkified_absolute_windows_path_renders_without_raw_brackets()
    {
        var markdown = "The file at the absolute path " +
                       @"C:\Users\MandoAdmin\Desktop\MandoCode\src\MandoCode\bin\Debug\net8.0\Games\index.html" +
                       " has been removed.";

        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.DoesNotContain("](file://", rendered);
        Assert.DoesNotContain("[C:\\", rendered);
    }

    [Fact]
    public void Linkified_absolute_path_produces_osc8_hyperlink()
    {
        var markdown = @"See C:\Users\foo\bar.txt for details.";
        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.Contains(Osc8Prefix, rendered);
    }

    [Fact]
    public void Absolute_path_with_project_root_does_not_leak_raw_markdown()
    {
        var markdown = "The file at the absolute path " +
                       @"C:\Users\MandoAdmin\Desktop\MandoCode\src\MandoCode\bin\Debug\net8.0\Games\index.html" +
                       " has been removed.";

        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown, @"C:\Users\MandoAdmin\Desktop\MandoCode");
        var rendered = RenderToString(renderable);

        Assert.DoesNotContain("](file://", rendered);
        Assert.DoesNotContain("[C:\\", rendered);
        Assert.Contains(Osc8Prefix, rendered);
    }

    [Fact]
    public void Path_broken_across_newline_does_not_leak_brackets()
    {
        var markdown = "The file at the absolute path C:\\Users\\MandoAdmin\\Desktop\\MandoCode\\src\\MandoCode\\\n" +
                       "bin\\Debug\\net8.0\\Games\\index.html has been removed.";

        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown, @"C:\Users\MandoAdmin\Desktop\MandoCode");
        var rendered = RenderToString(renderable);

        Assert.DoesNotContain("](file://", rendered);
    }

    [Fact]
    public void Existing_https_link_is_not_re_linkified_as_file_uri()
    {
        // Regression guard: my absolute-path regex used to match `s:` inside
        // `https://…`, which mangled URLs the LLM already wrote as markdown links.
        var markdown = "[IGN article](https://www.ign.com/articles/xbox-confirms-project-helix)";
        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.DoesNotContain("file:///s:", rendered);
        Assert.DoesNotContain("](file://", rendered);
    }

    [Fact]
    public void Bare_https_url_inside_text_is_not_misidentified_as_path()
    {
        var markdown = "Source: https://wccftech.com/roundup/xbox-next-gen-console";
        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.DoesNotContain("file:///s:", rendered);
    }

    [Fact]
    public void Headings_render_without_markdown_markers()
    {
        var markdown = "### Sources\n\nText below.";
        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.Contains("Sources", rendered);
        Assert.DoesNotContain("###", rendered);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(35)]
    public void Wrapped_list_text_stays_beside_marker_and_indents_continuations(int width)
    {
        var lines = RenderPlain(MarkdownHtmlRenderer.BuildRenderable("- Alpha beta gamma delta epsilon zeta eta theta."), width)
            .Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        Assert.StartsWith("• Alpha", lines[0]);
        Assert.True(lines.Length > 1);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("  ", line));
    }

    [Fact]
    public void Paragraphs_have_spacing_but_heading_stays_with_its_text()
    {
        var lines = RenderPlain(MarkdownHtmlRenderer.BuildRenderable("First paragraph.\n\n## Details\n\nSecond paragraph.\n\nThird paragraph."))
            .Split('\n').Select(line => line.TrimEnd()).ToArray();
        var heading = Array.IndexOf(lines, "Details");
        Assert.True(heading > 0);
        Assert.Equal("", lines[heading - 1]);
        Assert.Equal("Second paragraph.", lines[heading + 1]);
        Assert.Equal("", lines[heading + 2]);
    }

    [Fact]
    public void Empty_list_items_do_not_render_orphaned_bullets()
    {
        var lines = RenderPlain(MarkdownHtmlRenderer.BuildRenderable("-\n- Useful text\n-"))
            .Split('\n').Select(line => line.Trim()).ToArray();
        Assert.Contains("• Useful text", lines);
        Assert.DoesNotContain("•", lines);
    }

    [Fact]
    public void Loose_ordered_list_keeps_each_number_on_its_items_first_line()
    {
        // Blank lines between items make the list "loose": Markdig wraps each item's text in <p>,
        // with whitespace-only text around it. That whitespace used to lead the item, pushing the
        // text onto the line below its number and leaving a blank line after every item.
        var markdown = "1. **Record inflows** — ETF money.\n\n2. **Upgrades** — Firedancer.\n\n3. **Growth** — more volume.";
        var lines = RenderPlain(MarkdownHtmlRenderer.BuildRenderable(markdown))
            .Split('\n').Select(l => l.TrimEnd()).ToList();

        Assert.Contains(lines, l => l.StartsWith("1. Record inflows"));
        Assert.Contains(lines, l => l.StartsWith("2. Upgrades"));
        Assert.Contains(lines, l => l.StartsWith("3. Growth"));
        Assert.DoesNotContain(lines, l => l.Trim() is "1." or "2." or "3.");
    }

    [Fact]
    public void Loose_list_item_with_two_paragraphs_keeps_the_break_between_them()
    {
        var markdown = "1. First paragraph.\n\n   Second paragraph.\n\n2. Next item.";
        var lines = RenderPlain(MarkdownHtmlRenderer.BuildRenderable(markdown))
            .Split('\n').Select(l => l.TrimEnd()).ToList();

        var first = lines.FindIndex(l => l.StartsWith("1. First paragraph."));
        Assert.True(first >= 0);
        Assert.Equal("", lines[first + 1].Trim());
        Assert.Contains("Second paragraph.", lines[first + 2]);
    }

    private static string RenderPlain(IRenderable renderable, int width = 100)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Write(renderable);
        return writer.ToString().Replace("\r\n", "\n");
    }

    private static string RenderToString(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        return writer.ToString();
    }
}
