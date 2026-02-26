namespace MandoCode.Models;

/// <summary>
/// Static class with animated note characters and equalizer elements for the music display.
/// </summary>
public static class MusicAsciiArt
{
    private static readonly string[] Notes = { "\u266b", "\u266a", "\u266c" };
    private static readonly char[] BarLevels = { '\u2581', '\u2582', '\u2583', '\u2584', '\u2585', '\u2586', '\u2587', '\u2588' };
    private static readonly Random _random = new();

    /// <summary>
    /// Gets a random music note character.
    /// </summary>
    public static string GetRandomNote()
    {
        return Notes[_random.Next(Notes.Length)];
    }

    /// <summary>
    /// Gets an animated equalizer bar frame with 8 bars at 8 height levels.
    /// </summary>
    public static string GetEqualizerFrame(int barCount = 8)
    {
        var bars = new char[barCount];
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i] = BarLevels[_random.Next(BarLevels.Length)];
        }
        return new string(bars);
    }

    /// <summary>
    /// Gets a static (non-animated) equalizer frame for paused state — all bars at mid level.
    /// </summary>
    public static string GetPausedEqualizerFrame(int barCount = 8)
    {
        return new string('\u2583', barCount);
    }

    // Purple-to-blue gradient using ANSI 256-color palette
    private static readonly int[] EqBarColors = { 135, 134, 99, 98, 63, 69, 75, 81 };

    /// <summary>
    /// Returns a complete formatted visualizer line with purple-blue gradient coloring.
    /// Includes embedded ANSI color codes — caller should NOT wrap in additional color.
    /// </summary>
    public static string GetVisualizerLine(string trackName, bool isPlaying)
    {
        var note = GetRandomNote();
        var stateIcon = isPlaying ? "\u25b6 Playing" : "\u23f8 Paused";

        // Truncate track name if too long
        if (trackName.Length > 22)
            trackName = trackName[..19] + "...";

        // Build gradient-colored equalizer — each bar gets a purple→blue shade
        var eqBars = isPlaying ? GetEqualizerFrame() : GetPausedEqualizerFrame();
        var coloredEq = new System.Text.StringBuilder();
        for (int i = 0; i < eqBars.Length; i++)
        {
            var color = EqBarColors[i % EqBarColors.Length];
            coloredEq.Append($"\u001b[38;5;{color}m{eqBars[i]}");
        }

        // Note: bright violet | Track: light purple | EQ: gradient | State: sky blue
        return $"  \u001b[38;5;177m{note} \u001b[38;5;141m{trackName}  {coloredEq}\u001b[0m  \u001b[38;5;75m{stateIcon}\u001b[0m";
    }

    /// <summary>
    /// Returns the plain-text length of a visualizer line (without ANSI codes) for positioning.
    /// </summary>
    public static int GetVisualizerLineLength(string trackName, bool isPlaying)
    {
        var note = GetRandomNote();
        var stateIcon = isPlaying ? "\u25b6 Playing" : "\u23f8 Paused";
        if (trackName.Length > 22)
            trackName = trackName[..19] + "...";
        // "  {note} {trackName}  {8 eq bars}  {stateIcon}"
        return 2 + note.Length + 1 + trackName.Length + 2 + 8 + 2 + stateIcon.Length;
    }
}
