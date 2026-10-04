using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Turns styled legacy tool output into measurable text, discarding cursor commands.</summary>
public static class AnsiTranscriptText
{
    public static Paragraph Parse(string input)
    {
        var output = new Paragraph();
        var foreground = Color.Default;
        var background = Color.Default;
        var decoration = Decoration.None;
        string? link = null;
        var start = 0;
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] != '\u001b') continue;
            if (index > start) output.Append(input[start..index], new Style(foreground, background, decoration, link));
            if (index + 1 < input.Length && input[index + 1] == '[')
            {
                var end = index + 2;
                while (end < input.Length && !(input[end] >= '@' && input[end] <= '~')) end++;
                if (end < input.Length && input[end] == 'm')
                {
                    var values = input[(index + 2)..end].Split(';').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
                    for (var n = 0; n < values.Length; n++)
                    {
                        var code = values[n];
                        if (code == 0) { foreground = background = Color.Default; decoration = Decoration.None; }
                        else if (code == 1) decoration |= Decoration.Bold;
                        else if (code == 2) decoration |= Decoration.Dim;
                        else if (code == 3) decoration |= Decoration.Italic;
                        else if (code == 4) decoration |= Decoration.Underline;
                        else if (code == 22) decoration &= ~(Decoration.Bold | Decoration.Dim);
                        else if (code == 23) decoration &= ~Decoration.Italic;
                        else if (code == 24) decoration &= ~Decoration.Underline;
                        else if (code == 39) foreground = Color.Default;
                        else if (code == 49) background = Color.Default;
                        else if (code is >= 30 and <= 37) foreground = Color.FromInt32(code - 30);
                        else if (code is >= 90 and <= 97) foreground = Color.FromInt32(code - 90 + 8);
                        else if (code is >= 40 and <= 47) background = Color.FromInt32(code - 40);
                        else if (code is 38 or 48 && n + 2 < values.Length)
                        {
                            Color? color = null;
                            if (values[n + 1] == 5) { color = Color.FromInt32(Math.Clamp(values[n + 2], 0, 255)); n += 2; }
                            else if (values[n + 1] == 2 && n + 4 < values.Length)
                            {
                                color = new Color((byte)Math.Clamp(values[n + 2], 0, 255), (byte)Math.Clamp(values[n + 3], 0, 255), (byte)Math.Clamp(values[n + 4], 0, 255));
                                n += 4;
                            }
                            if (color.HasValue) { if (code == 38) foreground = color.Value; else background = color.Value; }
                        }
                    }
                }
                index = Math.Min(end, input.Length - 1);
            }
            else if (index + 1 < input.Length && input[index + 1] == ']')
            {
                var end = index + 2;
                while (end < input.Length && input[end] != '\a' && !(input[end] == '\u001b' && end + 1 < input.Length && input[end + 1] == '\\')) end++;
                var osc = input[(index + 2)..end];
                if (osc.StartsWith("8;", StringComparison.Ordinal))
                {
                    var separator = osc.IndexOf(';', 2);
                    if (separator >= 0) link = osc[(separator + 1)..] is { Length: > 0 } uri ? uri : null;
                }
                index = Math.Min(end + (end < input.Length && input[end] == '\u001b' ? 1 : 0), input.Length - 1);
            }
            else index = Math.Min(index + 1, input.Length - 1);
            start = index + 1;
        }
        if (start < input.Length) output.Append(input[start..], new Style(foreground, background, decoration, link));
        return output;
    }
}
