using RazorConsole.Core.Abstractions.Rendering;
using RazorConsole.Core.Rendering.Translation.Contexts;
using RazorConsole.Core.Vdom;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

/// <summary>Hide presentation without removing the mounted agent component tree.</summary>
public sealed class WorkspaceVisibilityTranslator : ITranslationMiddleware
{
    public IRenderable Translate(TranslationContext context, TranslationDelegate next, VNode node)
        => node.Attributes.TryGetValue("data-workspace-hidden", out var hidden) && hidden == "true"
            ? new EmptyWorkspace() : next(node);

    private sealed class EmptyWorkspace : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(0, 0);
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => [];
    }
}
