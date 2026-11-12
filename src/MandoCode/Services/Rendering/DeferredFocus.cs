using System.Runtime.CompilerServices;
using RazorConsole.Core.Focus;

namespace MandoCode.Services;

/// <summary>
/// Keeps focus requests made from OnAfterRender out of the render batch that raised them.
/// </summary>
/// <remarks>
/// In RazorConsole 0.6, FocusManager.FocusAsync dispatched focusin/focusout as soon as the key was a known focus
/// target. OnAfterRender runs while the renderer is still inside its batch, so that dispatch
/// starts the event's own render pass before the batch has been cleared. The renderer then hands
/// the same edits to the display a second time and every component the batch created is inserted
/// twice. At startup that batch is the one that creates the prompt, which then drew as rows of
/// bottom borders and could not take input. Whether the key was already a known target depended
/// on a thread-pool observer, which made the failure intermittent.
/// Event handlers are not affected: they run outside a batch and may call FocusAsync directly.
/// RazorConsole 0.7 also defers reentrant dispatch upstream. Keep this lifecycle safeguard
/// so MandoCode's after-render focus requests explicitly run after their batch completes.
/// </remarks>
public static class DeferredFocus
{
    /// <summary>Resumes on the renderer's dispatcher once the batch in progress has finished.</summary>
    public static YieldAwaitable AfterRenderBatch() => Task.Yield();

    /// <summary>Requests focus after the batch in progress has finished. Use from OnAfterRender.</summary>
    public static async Task<bool> FocusDeferredAsync(this FocusManager focus, string key, CancellationToken token = default)
    {
        await AfterRenderBatch();
        return await focus.FocusAsync(key, token);
    }
}
