using RazorConsole.Core.Input;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>Fixed artwork rows: clip at cell boundaries, never wrap or parse markup.</summary>
public sealed class AsciiArtRenderable(IReadOnlyList<string> lines) : IRenderable
{
    private static readonly Color[] Stops =
    [new(0, 210, 178), new(0, 200, 255), new(60, 120, 255), new(160, 80, 255), new(210, 50, 210)];
    private readonly string[] _lines = lines.ToArray();
    private readonly int _width = lines.Select(TextSelectionState.CellWidth).DefaultIfEmpty().Max();
    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        var width = Math.Max(0, Math.Min(_width, maxWidth));
        return new Measurement(width, width);
    }
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var limit = Math.Max(0, Math.Min(maxWidth, options.ConsoleSize.Width));
        for (var row = 0; row < _lines.Length; row++)
        {
            var column = 0;
            foreach (var rune in _lines[row].EnumerateRunes())
            {
                var text = rune.ToString();
                var cells = TextSelectionState.CellWidth(text);
                if (column + cells > limit) break;
                yield return new Segment(text, new Style(Gradient(column)));
                column += cells;
            }
            if (row < _lines.Length - 1) yield return Segment.LineBreak;
        }
    }
    private Color Gradient(int column)
    {
        var position = (_width <= 1 ? 0 : (double)column / (_width - 1)) * (Stops.Length - 1);
        var index = Math.Min(Stops.Length - 2, (int)position);
        var fraction = position - index;
        var start = Stops[index];
        var end = Stops[index + 1];
        return new Color((byte)(start.R + (end.R - start.R) * fraction),
            (byte)(start.G + (end.G - start.G) * fraction), (byte)(start.B + (end.B - start.B) * fraction));
    }
}
