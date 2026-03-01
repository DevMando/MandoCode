using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Manages terminal theme detection (OSC 11), ANSI palette customization (OSC 4),
/// and dynamic title bar status (OSC 0).
/// </summary>
public class TerminalThemeService : IDisposable
{
    private readonly MandoCodeConfig _config;
    private readonly TokenTrackingService _tokenTracker;
    private readonly ProjectRootAccessor _projectRoot;

    private bool _isDarkTheme = true;
    private bool _paletteApplied;

    /// <summary>
    /// When true, the music visualizer owns the title bar — status updates are suppressed.
    /// </summary>
    public bool IsMusicTitleActive { get; set; }

    public bool IsDarkTheme => _isDarkTheme;

    public TerminalThemeService(
        MandoCodeConfig config,
        TokenTrackingService tokenTracker,
        ProjectRootAccessor projectRoot)
    {
        _config = config;
        _tokenTracker = tokenTracker;
        _projectRoot = projectRoot;
    }

    /// <summary>
    /// Queries the terminal background color via OSC 11 and determines light vs dark theme.
    /// Falls back to dark on timeout or parse failure.
    /// </summary>
    public void DetectTheme()
    {
        try
        {
            // Send OSC 11 query
            Console.Write("\u001b]11;?\u0007");

            // Poll for response with 500ms deadline
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            var response = new System.Text.StringBuilder();
            var foundEsc = false;

            while (DateTime.UtcNow < deadline)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                response.Append(key.KeyChar);

                if (key.KeyChar == '\u001b') foundEsc = true;

                // Response ends with BEL (\x07) or ST (\x1b\\)
                if (foundEsc && (key.KeyChar == '\u0007' || response.ToString().EndsWith("\u001b\\")))
                    break;
            }

            // Parse rgb:RRRR/GGGG/BBBB
            var text = response.ToString();
            var rgbMatch = System.Text.RegularExpressions.Regex.Match(text, @"rgb:([0-9a-fA-F]+)/([0-9a-fA-F]+)/([0-9a-fA-F]+)");

            if (rgbMatch.Success)
            {
                var r = ParseColorComponent(rgbMatch.Groups[1].Value);
                var g = ParseColorComponent(rgbMatch.Groups[2].Value);
                var b = ParseColorComponent(rgbMatch.Groups[3].Value);

                var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                _isDarkTheme = luminance <= 0.5;
            }
            // else: default to dark (already set)
        }
        catch
        {
            // Default to dark on any error
            _isDarkTheme = true;
        }
    }

    /// <summary>
    /// Overrides ANSI palette indices 1-6 via OSC 4 based on detected theme.
    /// All existing ANSI color usage automatically picks up the new colors.
    /// </summary>
    public void ApplyPalette()
    {
        if (_isDarkTheme)
        {
            // Dark palette: soft rose, mint, warm amber, periwinkle, lavender, teal
            WritePaletteEntry(1, "e0/6c/75"); // soft rose (red)
            WritePaletteEntry(2, "98/c3/79"); // mint (green)
            WritePaletteEntry(3, "e5/c0/7b"); // warm amber (yellow)
            WritePaletteEntry(4, "61/af/ef"); // periwinkle (blue)
            WritePaletteEntry(5, "c6/78/dd"); // lavender (magenta)
            WritePaletteEntry(6, "56/b6/c2"); // teal (cyan)
        }
        else
        {
            // Light palette: deep red, forest green, burnt orange, royal blue, deep purple, ocean teal
            WritePaletteEntry(1, "c9/1b/00"); // deep red
            WritePaletteEntry(2, "00/6b/3c"); // forest green
            WritePaletteEntry(3, "c0/76/00"); // burnt orange
            WritePaletteEntry(4, "00/44/b8"); // royal blue
            WritePaletteEntry(5, "6c/38/b5"); // deep purple
            WritePaletteEntry(6, "00/7f/8c"); // ocean teal
        }

        _paletteApplied = true;
    }

    /// <summary>
    /// Restores the terminal's default ANSI palette via OSC 104.
    /// </summary>
    public void ResetPalette()
    {
        if (_paletteApplied)
        {
            Console.Write("\u001b]104\u0007");
            _paletteApplied = false;
        }
    }

    /// <summary>
    /// Updates the terminal title bar with model, project, and token info via OSC 0.
    /// Skipped when the music visualizer owns the title bar.
    /// </summary>
    public void UpdateStatusTitle()
    {
        if (IsMusicTitleActive)
            return;

        var model = _config.GetEffectiveModelName();
        var project = Path.GetFileName(_projectRoot.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tokens = TokenTrackingService.FormatTokenCount(_tokenTracker.TotalSessionTokens);

        var title = $"MandoCode \u2014 {model} \u2014 {project} \u2014 {tokens} tokens";
        Console.Write($"\u001b]0;{title}\u0007");
    }

    public void Dispose()
    {
        ResetPalette();
        // Restore default title
        try { Console.Write("\u001b]0;MandoCode\u0007"); } catch { }
    }

    private static void WritePaletteEntry(int index, string rgb)
    {
        Console.Write($"\u001b]4;{index};rgb:{rgb}\u0007");
    }

    /// <summary>
    /// Parses a hex color component (2 or 4 hex digits) to a 0.0-1.0 range.
    /// </summary>
    private static double ParseColorComponent(string hex)
    {
        var value = Convert.ToInt32(hex, 16);
        // 4-digit components have max 0xFFFF, 2-digit have max 0xFF
        var max = hex.Length <= 2 ? 255.0 : 65535.0;
        return value / max;
    }
}
