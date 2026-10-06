using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

public static class CliAgentExchangeRenderer
{
    public static IRenderable Result(string heading, string answer, bool success, string? projectRoot = null, bool headingIsMarkup = false, IEnumerable<string>? agentNames = null)
    {
        IRenderable body;
        try { body = MarkdownHtmlRenderer.BuildRenderable(answer, projectRoot, agentNames); }
        catch { body = new Text(answer); }
        return new Panel(body)
        {
            Header = new PanelHeader(headingIsMarkup ? heading : Markup.Escape(heading)),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(success ? Color.Green : Color.Yellow),
            Padding = new Padding(1, 1, 1, 1),
            Expand = true
        };
    }
}
