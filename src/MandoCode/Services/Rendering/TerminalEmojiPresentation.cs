using System.Globalization;
using System.Text;

namespace MandoCode.Services;

/// <summary>Keeps text-default supplementary emoji aligned with Spectre's two-cell measurement.</summary>
public static class TerminalEmojiPresentation
{
    // Emoji minus Emoji_Presentation, restricted to supplementary code points.
    // Unicode 17: https://www.unicode.org/Public/17.0.0/ucd/emoji/emoji-data.txt
    // Spectre 0.54 measures their surrogate pairs as two cells. Explicit emoji
    // presentation prevents a terminal from drawing e.g. bare 🛠 as one text cell.
    private static readonly HashSet<int> TextDefault =
    [
        0x1F170, 0x1F171, 0x1F17E, 0x1F17F, 0x1F202, 0x1F237, 0x1F321,
        0x1F324, 0x1F325, 0x1F326, 0x1F327, 0x1F328, 0x1F329, 0x1F32A, 0x1F32B, 0x1F32C,
        0x1F336, 0x1F37D, 0x1F396, 0x1F397, 0x1F399, 0x1F39A, 0x1F39B, 0x1F39E, 0x1F39F,
        0x1F3CB, 0x1F3CC, 0x1F3CD, 0x1F3CE, 0x1F3D4, 0x1F3D5, 0x1F3D6, 0x1F3D7,
        0x1F3D8, 0x1F3D9, 0x1F3DA, 0x1F3DB, 0x1F3DC, 0x1F3DD, 0x1F3DE, 0x1F3DF,
        0x1F3F3, 0x1F3F5, 0x1F3F7, 0x1F43F, 0x1F441, 0x1F4FD, 0x1F549, 0x1F54A,
        0x1F56F, 0x1F570, 0x1F573, 0x1F574, 0x1F575, 0x1F576, 0x1F577, 0x1F578, 0x1F579,
        0x1F587, 0x1F58A, 0x1F58B, 0x1F58C, 0x1F58D, 0x1F590, 0x1F5A5, 0x1F5A8,
        0x1F5B1, 0x1F5B2, 0x1F5BC, 0x1F5C2, 0x1F5C3, 0x1F5C4, 0x1F5D1, 0x1F5D2, 0x1F5D3,
        0x1F5DC, 0x1F5DD, 0x1F5DE, 0x1F5E1, 0x1F5E3, 0x1F5E8, 0x1F5EF, 0x1F5F3, 0x1F5FA,
        0x1F6CB, 0x1F6CD, 0x1F6CE, 0x1F6CF, 0x1F6E0, 0x1F6E1, 0x1F6E2, 0x1F6E3, 0x1F6E4,
        0x1F6E5, 0x1F6E9, 0x1F6F0, 0x1F6F3
    ];

    public static string Normalize(string text)
    {
        if (!text.Any(char.IsHighSurrogate)) return text;
        var result = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            result.Append(element);
            // Preserve explicit text/emoji presentation, skin tones and ZWJ sequences.
            if (Rune.TryGetRuneAt(element, 0, out var rune) && element.Length == rune.Utf16SequenceLength && TextDefault.Contains(rune.Value))
                result.Append('\uFE0F');
        }
        return result.ToString();
    }
    public static string SpaceLabels(string text)
    {
        var result = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);
        var previousEmoji = false;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (!Rune.TryGetRuneAt(element, 0, out var rune)) { result.Append(element); previousEmoji = false; continue; }
            if (previousEmoji && Rune.IsLetterOrDigit(rune)) result.Append(' ');
            result.Append(element);
            previousEmoji = rune.Value is >= 0x1F000 and <= 0x1FAFF or 0x2699 or 0x26A1 or 0x2764 or 0x2665 or 0x2600 or 0x2601 or 0x2602 or 0x2603 or 0x260E or 0x2709 or 0x270F or 0x2702 or 0x2714 or 0x2705 or 0x274C or 0x26A0 or 0x2B50;
        }
        return result.ToString();
    }
}
