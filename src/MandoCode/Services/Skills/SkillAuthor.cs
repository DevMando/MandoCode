using Microsoft.Extensions.AI;
using OllamaSharp;
using YamlDotNet.Serialization;

namespace MandoCode.Services;

/// <summary>A model-drafted skill, split into the three fields the editor binds to.</summary>
public sealed record DraftSkill(string Name, string Description, string Body);

/// <summary>
/// One-shot LLM helper that drafts and refines skills for the Skills editor. Like
/// the Desktop skill editor, it builds a separate Ollama client — no plugins, tools, filters, or
/// shared history — so authoring can never touch a live agent's conversation or fire a tool call.
/// The draft is returned to the editor for the user to review and edit before anything is saved;
/// nothing here writes to disk.
///
/// Generation asks the model for a complete SKILL.md (frontmatter + body) and splits it back into
/// name / description / body. Drafts are validated when the user saves them.
/// </summary>
public static class SkillAuthor
{
    private const string GenerateSystem =
        "You write SKILL.md files for an AI coding assistant. A skill is a named, reusable set of " +
        "instructions the assistant loads on demand when a user's request matches the skill's " +
        "description. Given the user's intent, output ONE complete SKILL.md and NOTHING else: no " +
        "commentary, no code fences. Format EXACTLY:\n" +
        "---\n" +
        "name: <short-kebab-case-name>\n" +
        "description: <one sentence, written so the assistant can match it to a request>\n" +
        "---\n" +
        "<markdown body: the concrete steps/rules the assistant should follow when this skill loads>\n\n" +
        "Keep the body focused and actionable — numbered steps or short sections. Don't restate the " +
        "description. Don't invent tools or file paths that weren't implied by the intent.";

    private const string RefineSystem =
        "You improve the instructions body of a SKILL.md for an AI coding assistant. Apply the user's " +
        "requested change to the instructions below and return ONLY the revised markdown body — no " +
        "frontmatter, no commentary, no code fences. Keep what already works; change what was asked. " +
        "Keep it concrete and actionable.";

    /// <summary>Drafts a new skill from a one-line intent. Throws on connection/model failure.</summary>
    public static async Task<DraftSkill> GenerateAsync(
        string endpoint, string model, string intent, CancellationToken ct = default)
    {
        var raw = await CompleteAsync(endpoint, model, GenerateSystem, intent.Trim(), ct);
        return ParseDraft(StripFences(raw));
    }

    /// <summary>Rewrites an existing instructions body against an instruction. Returns the new body.</summary>
    public static async Task<string> RefineAsync(
        string endpoint, string model, string currentBody, string instruction, CancellationToken ct = default)
    {
        var user = $"Requested change:\n{instruction.Trim()}\n\nCurrent instructions:\n{currentBody.Trim()}";
        var raw = await CompleteAsync(endpoint, model, RefineSystem, user, ct);
        var body = StripFences(raw).Trim();
        if (body.Length == 0) throw new IOException("The model returned an empty skill. Your existing instructions were kept.");
        return body;
    }

    private static async Task<string> CompleteAsync(
        string endpoint, string model, string system, string user, CancellationToken ct)
    {
        using IChatClient chat = new OllamaApiClient(new Uri(endpoint), model);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        };

        // A touch of latitude helps phrasing without drifting off-spec.
        var options = new ChatOptions { Temperature = 0.4f };
        var result = await chat.GetResponseAsync(history, options, ct);
        return result.Text?.Trim() ?? "";
    }

    /// <summary>Strips a wrapping ```fenced block if the model added one despite instructions.</summary>
    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;

        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return t;
        t = t.Substring(firstNewline + 1);              // drop the opening ```lang line
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) t = t.Substring(0, lastFence);
        return t.Trim();
    }

    /// <summary>Splits a generated SKILL.md into name/description/body. Mirrors the engine's
    /// frontmatter shape; on anything unexpected it degrades to "whole thing is the body" so the
    /// user still gets something editable rather than an error.</summary>
    internal static DraftSkill ParseDraft(string raw)
    {
        var norm = StripFences(raw).Replace("\r\n", "\n").Trim();
        string name = "", description = "", body = norm;

        if (norm.StartsWith("---\n"))
        {
            var after = norm.Substring(4);
            var close = after.IndexOf("\n---", StringComparison.Ordinal);
            if (close >= 0)
            {
                var frontmatter = after.Substring(0, close);
                body = after.Substring(close + 4).TrimStart('\n');

                try
                {
                    var fields = new DeserializerBuilder().Build().Deserialize<Dictionary<string, string>>(frontmatter);
                    name = fields?.GetValueOrDefault("name")?.Trim() ?? "";
                    description = fields?.GetValueOrDefault("description")?.Trim() ?? "";
                }
                catch (YamlDotNet.Core.YamlException) { return new DraftSkill("", "", norm); }
            }
        }

        return new DraftSkill(name, description, body.Trim());
    }
}
