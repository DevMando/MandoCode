namespace MandoCode.Services;

internal static class ResponseLimitNotice
{
    public static string BuildLengthCutoffNotice(long completionTokens, int maxTokens, int configuredContextLength, bool emptyContent, bool isCloudModel = false)
    {
        // Formatted as markdown — the response path renders through MarkdownHtmlRenderer,
        // so a bold headline + bullet list reads far better than the old wall of text.

        // Ollama can stop a handful of tokens shy of the exact cap — treat anything
        // within 90% of maxTokens (or an unreported count) as a genuine cap hit.
        if (completionTokens <= 0 || completionTokens >= maxTokens * 9L / 10)
        {
            var thinkingCapNote = emptyContent
                ? "\n- Note: thinking models (qwen3, minimax) spend reasoning tokens from this same budget — " +
                  "a small max tokens limit can be consumed entirely by internal reasoning before any visible answer."
                : "";
            return "\n\n⚠ **Response cut off — hit the max response tokens limit.**\n" +
                   "- Say \"continue\" to keep going\n" +
                   "- Or raise max tokens with /config" +
                   thinkingCapNote;
        }

        var thinkingNote = emptyContent
            ? "\nNo visible answer was produced — likely a thinking model (e.g. qwen3, minimax) that spent it all on internal reasoning."
            : "";

        var header = "\n\n⚠ **Response cut off — the model's CONTEXT WINDOW filled.**\n" +
                     $"Only {completionTokens:N0} of your {maxTokens / 1024}k response budget was generated, " +
                     "so raising max tokens won't help." +
                     thinkingNote + "\n";

        if (isCloudModel)
        {
            return header +
                   "\nThe conversation filled the model's server-side context window. How to fix:\n" +
                   "- /clear to trim the conversation history\n" +
                   "- Break the request into smaller pieces";
        }

        // MandoCode stamps contextLength onto every request as num_ctx, so a configured
        // window IS the window that filled — the fix is a bigger value (or /clear), never
        // a daemon restart. Only contextLength 0 defers to the daemon's own default.
        var applyLine = configuredContextLength > 0
            ? $"- Your configured {configuredContextLength / 1024}k window applies to every request — raise it: " +
              "/config set contextLength 32768 (applies from your next message; more window uses more VRAM)"
            : "- No window configured (contextLength 0 = daemon default, often ~4k) — set one: /config set contextLength 16384 " +
              "(applies from your next message; Ollama desktop app users can instead drag Settings → Context length)";

        return header +
               "\nThe context window filled mid-generation. How to fix:\n" +
               applyLine + "\n" +
               "- /clear frees space right now by trimming history";
    }
}
