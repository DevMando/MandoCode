using System.Text;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace MandoCode.Services;

/// <summary>Isolated, tool-free Ollama requests generate a handoff without changing the live agent history.</summary>
public static class SnapshotEnhancer
{
    // Fixed-size inputs and pairwise reduction cover large histories without oversized requests.
    private const int MinChunkChars = 6000;    // ~1.5k tokens — comfortable for small models

    // A snapshot recap is Imported and silently prepended to ANOTHER model's next message, so the
    // prompts frame it as a HANDOFF BRIEFING to an AI assistant — not a human-facing summary. They're
    // domain-agnostic (coding, research Q&A, debugging, plain chat), weight the most recent turns
    // (where the current state lives), preserve specifics verbatim, and emit PLAIN PROSE (the card
    // renders the recap as plain text, so markdown/asterisks would leak).
    private const string StyleRules =
        " Write plain, dense prose addressed to the assistant (e.g. \"The user is building…\"). No " +
        "markdown, headings, bullets, asterisks, or backticks. Preserve specifics VERBATIM — file " +
        "paths, names, numbers, versions, URLs, identifiers, exact decisions. Make explicit what is " +
        "already DONE versus still UNFINISHED, so the assistant knows what to work on next. Be " +
        "self-contained: don't refer to \"the conversation above.\" Use only what's actually in the " +
        "transcript — never invent or assume. Don't describe what the conversation was NOT about; " +
        "capture what it WAS about.";

    private const string MapPrompt =
        "Summarize this SEGMENT of a longer conversation as raw material for a later handoff. Capture " +
        "the substantive content: what the user wants, key facts, answers, decisions, code, files, " +
        "values, and errors, plus anything left open. State facts only — no guessing about other " +
        "segments." + StyleRules;

    private const string ReducePrompt =
        "Below are ordered segment summaries of one conversation. Merge them into a single briefing " +
        "that will be handed to another AI assistant so it can continue this conversation seamlessly. " +
        "Cover: what the user is trying to do, the key facts / decisions / code established so far, " +
        "any preferences or constraints the user stated, and the immediate open thread or next step " +
        "(including any unanswered question). Give extra weight to the most recent exchanges — that's " +
        "where things currently stand." + StyleRules + " Keep it to a tight paragraph or two; length " +
        "should match how much actually happened.";

    private const string SinglePrompt =
        "You are writing a briefing that will be silently handed to another AI assistant so it can " +
        "continue this conversation without missing a beat. From the transcript below, write what " +
        "that assistant needs to know: what the user is trying to do, the key facts / answers / " +
        "decisions / code established so far, any preferences or constraints the user stated, and the " +
        "immediate open thread or next step (including any unanswered question). Give extra weight to " +
        "the most recent exchanges — that's where things currently stand." + StyleRules + " Keep it " +
        "to a tight paragraph or two; length should match how much actually happened.";

    /// <summary>Generates an AI recap of <paramref name="rawHistory"/> using the given Ollama model.
    /// Throws on connection/model failure; the caller decides how to surface it.</summary>
    public static async Task<string> SummarizeAsync(
        string endpoint, string model, string rawHistory, CancellationToken ct = default)
    {
        using IChatClient chat = new OllamaApiClient(new Uri(endpoint), model);
        return await SummarizeAsync(chat, rawHistory, ct);
    }

    internal static async Task<string> SummarizeAsync(IChatClient chat, string rawHistory, CancellationToken ct = default)
    {
        var chunks = Chunk(rawHistory);

        // Short conversation — one pass, straight to a final-shaped recap.
        if (chunks.Count <= 1)
            return await SummarizeOneAsync(chat, SinglePrompt, rawHistory, ct);

        // Map: summarize each segment independently.
        var partials = new List<string>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            var part = await SummarizeOneAsync(chat, MapPrompt, chunks[i], ct);
            if (!string.IsNullOrWhiteSpace(part))
                partials.Add($"Segment {i + 1}/{chunks.Count}:\n{part}");
        }

        if (partials.Count == 0) return "";

        // Reduce in pairs so large conversations never become one oversized model request.
        while (partials.Count > 1)
        {
            var reduced = new List<string>();
            for (var i = 0; i < partials.Count; i += 2)
                reduced.Add(i + 1 == partials.Count ? partials[i] :
                    await SummarizeOneAsync(chat, ReducePrompt, partials[i] + "\n\n" + partials[i + 1], ct));
            partials = reduced;
        }
        return partials[0];
    }

    private const string NamePrompt =
        "Give this saved conversation a short title so it's recognizable in a list later. 3 to 6 " +
        "words, Title Case, naming the actual subject (a feature, file, bug, topic, or decision) — " +
        "not generic filler like \"Coding Session\" or \"Conversation Summary\". Output ONLY the " +
        "title: no quotes, no trailing punctuation, no explanation.";

    /// <summary>Suggests a short, human-recognizable title for a snapshot from its recap. Best-effort:
    /// returns null (caller falls back to the origin model as the card title) on any failure or an
    /// unusable result. <paramref name="avoid"/> is passed to the model to discourage near-duplicates;
    /// the caller still enforces true uniqueness deterministically — an LLM can't be trusted to.</summary>
    public static async Task<string?> SuggestNameAsync(
        string endpoint, string model, string recap, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(recap)) return null;

            using IChatClient chat = new OllamaApiClient(new Uri(endpoint), model);

            var instruction = NamePrompt;
            if (avoid.Count > 0)
                instruction += " These titles are already taken, so pick something clearly different: "
                             + string.Join("; ", avoid.Take(40)) + ".";

            // A touch of warmth so titles aren't all phrased alike, but still grounded in the recap.
            var raw = await SummarizeOneAsync(chat, instruction, recap, ct, temperature: 0.4f);
            return SnapshotNaming.Clean(raw);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>One chat round: system instruction + the text to summarize. Stateless — a fresh
    /// history each call, so nothing leaks between chunks.</summary>
    private static async Task<string> SummarizeOneAsync(
        IChatClient chat, string instruction, string text, CancellationToken ct,
        float temperature = 0.2f)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, instruction),
            new(ChatRole.User, text)
        };

        // Low temperature by default — a recap should be faithful, not creative. Naming nudges higher.
        // Ollama's generation budget also includes thinking. A small answer-sized cap can
        // stop a reasoning model before it emits any final text. Keep the requested recap
        // short in the prompt, but give reasoning room and retry once with more headroom.
        foreach (var budget in new[] { 4096, 16384 })
        {
            ct.ThrowIfCancellationRequested();
            var options = new ChatOptions { Temperature = temperature, MaxOutputTokens = budget };
            var result = await chat.GetResponseAsync(history, options, ct);
            var textResult = result.Text?.Trim() ?? "";
            if (textResult.Length > 0) return textResult;
            // The next request uses the same transcript, not the model's private reasoning.
        }
        throw new IOException("The model produced no summary after retrying with a larger generation budget. " +
            "Try another summarizer model. Your conversation has been kept.");
    }

    /// <summary>Splits the entire history into bounded chunks, including long individual lines.</summary>
    internal static List<string> Chunk(string history)
    {
        history = history?.Trim() ?? "";
        if (history.Length <= MinChunkChars) return new List<string> { history };

        const int chunkSize = MinChunkChars;
        var chunks = new List<string>();
        var sb = new StringBuilder(chunkSize + 256);
        foreach (var line in history.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > chunkSize)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            // Very long tool output lines must also fit in a bounded request.
            for (var offset = 0; offset < line.Length;)
            {
                var length = Math.Min(chunkSize - sb.Length, line.Length - offset);
                sb.Append(line, offset, length);
                offset += length;
                if (sb.Length == chunkSize) { chunks.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length == chunkSize) { chunks.Add(sb.ToString()); sb.Clear(); }
            sb.Append('\n');
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
