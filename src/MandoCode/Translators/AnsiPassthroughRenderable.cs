using Spectre.Console.Rendering;

namespace MandoCode.Translators;

/// <summary>
/// An IRenderable that outputs a pre-built ANSI string verbatim.
/// The terminal interprets all escape codes (colors, OSC links, etc.) as normal.
/// </summary>
public sealed class AnsiPassthroughRenderable : IRenderable
{
    private readonly string _ansiContent;

    public AnsiPassthroughRenderable(string ansiContent)
    {
        _ansiContent = ansiContent;
    }

    /// <summary>
    /// The raw ANSI content string, exposed for components that need the string directly.
    /// </summary>
    public string Content => _ansiContent ?? "";

    public Measurement Measure(RenderOptions options, int maxWidth)
        => new(0, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (!string.IsNullOrEmpty(_ansiContent))
            yield return new Segment(_ansiContent);
    }
}
