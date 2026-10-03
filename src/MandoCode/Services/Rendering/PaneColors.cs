using Spectre.Console;
using Spectre.Console.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MandoCode.Models;

namespace MandoCode.Services;

public static class PaneColors
{
    public static bool Muted(AgentPane? pane) => pane is not null && !pane.Selected && pane.Services.GetRequiredService<MandoCodeConfig>().DimUnfocusedAgents;
    public static Color For(AgentPane? pane, Color color) => Muted(pane) && color != Color.Red && color != Color.Red1 && color != Color.Yellow ? Color.Grey62 : color;
    public static IRenderable Render(IRenderable content, AgentPane? pane, bool constrainWidth = true) => new MutedContent(content, pane, constrainWidth);

    private sealed class MutedContent(IRenderable content, AgentPane? pane, bool constrainWidth) : IRenderable
    {
        private int Width(int offered) => constrainWidth ? Math.Max(1, Math.Min(offered, pane is null ? offered : Math.Max(1, pane.Width - 2))) : offered;
        private RenderOptions Options(RenderOptions options, int width)
            => constrainWidth ? options with { ConsoleSize = new Size(width, options.ConsoleSize.Height) } : options;
        public Measurement Measure(RenderOptions options, int maxWidth)
        {
            var width = Width(maxWidth);
            return content.Measure(Options(options, width), width);
        }
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            var width = Width(maxWidth);
            foreach (var segment in content.Render(Options(options, width), width))
            {
                if (!Muted(pane) || segment.IsLineBreak)
                {
                    yield return segment;
                    continue;
                }
                var foreground = segment.Style?.Foreground ?? Color.Default;
                // Error and warning colors remain noticeable in background transcripts.
                if (foreground == Color.Red || foreground == Color.Red1 || foreground == Color.Yellow)
                    yield return segment;
                else
                    yield return new Segment(segment.Text, new Style(Color.Grey62, Color.Default, segment.Style?.Decoration ?? Decoration.None));
            }
        }
    }
}
