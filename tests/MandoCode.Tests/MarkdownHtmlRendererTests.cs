using MandoCode.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MandoCode.Tests;

public class MarkdownHtmlRendererTests
{
    private const string Osc8Prefix = "]8;;";

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
    public void Headings_render_with_hash_prefix()
    {
        // Hash prefix is kept so bold-prose pseudo-headings (which the LLM often
        // emits instead of real `###` headings) are visually distinguishable from
        // actual headings — they show up plain bold, headings show up with hashes.
        var markdown = "### Sources\n\nText below.";
        var renderable = MarkdownHtmlRenderer.BuildRenderable(markdown);
        var rendered = RenderToString(renderable);

        Assert.Contains("### Sources", rendered);
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
