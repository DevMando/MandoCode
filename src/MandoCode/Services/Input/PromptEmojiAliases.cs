using RazorConsole.Core.Input;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Expands a completed, caret-local emoji alias without moving the remaining text.</summary>
public static class PromptEmojiAliases
{
    // Keep these aliases aligned with Desktop Controls/ChatTabView.Input.cs.
    private static readonly Dictionary<string, string> DesktopAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grinning"] = "😀", ["smile"] = "😄", ["joy"] = "😂", ["rofl"] = "🤣",
        ["blush"] = "😊", ["wink"] = "😉", ["heart_eyes"] = "😍", ["smiling_hearts"] = "🥰",
        ["sunglasses"] = "😎", ["coolglasses"] = "😎", ["nerd"] = "🤓", ["thinking"] = "🤔",
        ["upside_down"] = "🙃", ["sweat_smile"] = "😅", ["grimacing"] = "😬", ["sob"] = "😭",
        ["partying"] = "🥳", ["mind_blown"] = "🤯", ["sleeping"] = "😴", ["eye_roll"] = "🙄",
        ["triumph"] = "😤", ["scream"] = "😱", ["melting"] = "🫠", ["hugs"] = "🤗",
        ["salute"] = "🫡", ["thumbsup"] = "👍", ["+1"] = "👍", ["thumbsdown"] = "👎",
        ["-1"] = "👎", ["ok_hand"] = "👌", ["pray"] = "🙏", ["clap"] = "👏",
        ["muscle"] = "💪", ["handshake"] = "🤝", ["victory"] = "✌️", ["crossed_fingers"] = "🤞",
        ["eyes"] = "👀", ["brain"] = "🧠", ["100"] = "💯", ["fire"] = "🔥",
        ["sparkles"] = "✨", ["rocket"] = "🚀", ["tada"] = "🎉", ["party_popper"] = "🎉",
        ["dart"] = "🎯", ["bulb"] = "💡", ["idea"] = "💡", ["zap"] = "⚡",
        ["star"] = "⭐", ["heart"] = "❤️", ["broken_heart"] = "💔", ["check"] = "✅",
        ["white_check_mark"] = "✅", ["x"] = "❌", ["cross"] = "❌", ["warning"] = "⚠️",
        ["question"] = "❓", ["exclamation"] = "❗", ["speech_balloon"] = "💬", ["bug"] = "🐛",
        ["wrench"] = "🔧", ["lock"] = "🔒", ["key"] = "🔑", ["memo"] = "📝",
        ["note"] = "📝", ["pushpin"] = "📌", ["pin"] = "📌", ["folder"] = "📁",
        ["desktop"] = "🖥️", ["coffee"] = "☕", ["pizza"] = "🍕", ["video_game"] = "🎮",
        ["robot"] = "🤖",
    };

    public static IReadOnlyList<string> PickerEmojis { get; } = DesktopAliases.Values.Distinct().ToArray();

    public static (int Start, string Name)? Fragment(TextSelectionState buffer)
    {
        if (buffer.Selection.Length != 0 || buffer.Cursor == 0) return null;
        var start = buffer.Text.LastIndexOf(':', buffer.Cursor - 1);
        if (start < 0 || buffer.Cursor - start > 66 ||
            (start > 0 && !char.IsWhiteSpace(buffer.Text[start - 1]) && !"([{".Contains(buffer.Text[start - 1]))) return null;
        var name = buffer.Text[(start + 1)..buffer.Cursor];
        return name.All(IsAliasCharacter) ? (start, name) : null;
    }

    public static IReadOnlyList<string> Match(string fragment)
    {
        if (fragment.Length == 0) return PickerEmojis;
        return DesktopAliases.Concat(new Dictionary<string, string> { ["cool"] = "😎", ["llama"] = "🦙" })
            .Where(alias => alias.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(alias => alias.Key.StartsWith(fragment, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(alias => alias.Value).Distinct().ToArray();
    }

    private static bool IsAliasCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+';

    public static void ReplaceFragment(TextSelectionState buffer, string emoji)
    {
        if (Fragment(buffer) is not { } fragment) { Insert(buffer, emoji); return; }
        var suffix = buffer.Text[buffer.Cursor..];
        buffer.SetText(buffer.Text[..fragment.Start] + emoji + suffix);
        buffer.Begin(fragment.Start + emoji.Length, 1, false);
        buffer.End();
        buffer.ClearSelection();
    }

    public static void Insert(TextSelectionState buffer, string emoji)
    {
        var cursor = buffer.Cursor;
        buffer.SetText(buffer.Text[..cursor] + emoji + buffer.Text[cursor..]);
        buffer.Begin(cursor + emoji.Length, 1, false);
        buffer.End();
        buffer.ClearSelection();
    }

    public static bool Expand(TextSelectionState buffer)
    {
        var cursor = buffer.Cursor;
        var text = buffer.Text;
        if (buffer.Selection.Length != 0 || cursor < 3 || text[cursor - 1] != ':') return false;
        var start = text.LastIndexOf(':', cursor - 2);
        if (start < 0 || cursor - start > 66) return false;
        // Keep file paths, URLs, and identifiers literal.
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && !"([{".Contains(text[start - 1])) return false;
        var name = text[(start + 1)..(cursor - 1)];
        if (name.Length == 0 || !name.All(IsAliasCharacter)) return false;
        var alias = text[start..cursor];
        var emoji = DesktopAliases.TryGetValue(name, out var desktopEmoji) ? desktopEmoji : name.ToLowerInvariant() switch
        {
            "cool" => "😎",
            "brain" => "🧠",
            "llama" => "🦙",
            _ => Emoji.Replace(alias.AsSpan())
        };
        if (emoji == alias) return false;
        buffer.SetText(text[..start] + emoji + text[cursor..]);
        buffer.Begin(start + emoji.Length, 1, false);
        buffer.End();
        buffer.ClearSelection();
        return true;
    }
}
