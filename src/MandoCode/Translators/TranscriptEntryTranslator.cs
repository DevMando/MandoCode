using MandoCode.Services;
using RazorConsole.Core.Abstractions.Rendering;
using RazorConsole.Core.Rendering.Translation.Contexts;
using RazorConsole.Core.Rendering.Vdom;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Translators;

public sealed class TranscriptEntryTranslator(TuiSession session, AgentWorkspace? workspace = null, WorkspaceRegistry? registry = null) : ITranslationMiddleware
{
    public IRenderable Translate(TranslationContext context, TranslationDelegate next, VNode node)
    {
        var id = VdomSpectreTranslator.GetAttribute(node, "data-transcript-entry");
        if (node.Kind != VNodeKind.Element || !long.TryParse(id, out var entryId)) return next(node);
        return registry?.Workspaces.Select(w => w.Find(entryId)).FirstOrDefault(r => r is not null) ?? workspace?.Find(entryId) ?? session.Find(entryId) ?? new Text("");
    }
}
