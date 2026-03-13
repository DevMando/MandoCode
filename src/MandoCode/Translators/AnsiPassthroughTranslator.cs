using RazorConsole.Core.Abstractions.Rendering;
using RazorConsole.Core.Rendering.Translation.Contexts;
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

/// <summary>
/// Translates &lt;div class="ansi-region" data-content="..."&gt; nodes into
/// AnsiPassthroughRenderable instances that replay captured ANSI output.
/// </summary>
public sealed class AnsiPassthroughTranslator : ITranslationMiddleware
{
    public IRenderable Translate(TranslationContext context, TranslationDelegate next, VNode node)
    {
        if (node.Kind != VNodeKind.Element)
            return next(node);

        if (!string.Equals(node.TagName, "div", StringComparison.OrdinalIgnoreCase))
            return next(node);

        if (!VdomSpectreTranslator.HasClass(node, "ansi-region"))
            return next(node);

        var content = VdomSpectreTranslator.GetAttribute(node, "data-content");
        if (string.IsNullOrEmpty(content))
            return next(node);

        return new AnsiPassthroughRenderable(content);
    }
}
