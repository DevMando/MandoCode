using System.Text;
using MandoCode.Models;
using MandoCode.Translators;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>
/// Renders an inline mini-display panel for music playback status.
/// Matrix-inspired green gradient theme.
/// </summary>
public static class MusicPlayerUI
{
    // Synthwave purple palette
    private const string Border    = "\u001b[38;2;90;0;160m";     // deep purple border
    private const string Header    = "\u001b[38;2;200;100;255m";  // bright violet header
    private const string TrackClr  = "\u001b[38;2;180;140;255m";  // lavender track name
    private const string StateClr  = "\u001b[38;2;0;255;200m";    // cyan-mint state icon
    private const string LabelClr  = "\u001b[38;2;140;100;200m";  // soft purple labels
    private const string DimClr    = "\u001b[38;2;70;40;100m";    // dark purple for empty volume
    private const string NoteClr   = "\u001b[38;2;255;100;220m";  // neon pink note symbol
    private const string Rst       = "\u001b[0m";

    /// <summary>
    /// Renders the music status panel after a command is executed.
    /// </summary>
    public static void RenderStatus(MusicPlayerService player)
    {
        AnsiConsole.Write(BuildStatusRenderable(player));
    }

    /// <summary>
    /// Builds the music status panel as an IRenderable.
    /// </summary>
    public static IRenderable BuildStatusRenderable(MusicPlayerService player)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        if (!player.AudioAvailable && player.AudioError != null)
        {
            return BuildErrorRenderable(player.AudioError);
        }

        var track = player.CurrentTrack;
        if (track == null && !player.IsPlaying && !player.IsPaused)
        {
            return BuildStoppedRenderable();
        }

        var trackName = track?.Name ?? "Unknown";
        var genre = string.IsNullOrEmpty(player.Genre) ? "Unknown" : char.ToUpper(player.Genre[0]) + player.Genre[1..];
        var volumePercent = (int)(player.Volume * 100);
        var volumeBar = BuildGradientVolumeBar(player.Volume);
        var stateIcon = player.IsPaused ? "\u23f8 Paused" : "\u25b6 Playing";
        var note = MusicAsciiArt.GetRandomNote();

        if (trackName.Length > 24)
            trackName = trackName[..21] + "...";

        // Calculate content widths to size the box dynamically
        var trackContent = $"  {note} {trackName}";
        var stateContent = stateIcon;
        var trackLineWidth = trackContent.Length + 1 + stateContent.Length + 1; // min gap + trailing space

        var volPrefix = "  Vol: ";
        var volSuffix = $" {volumePercent,3}%";
        var genreLabel = $"Genre: {genre}";
        var volLineWidth = volPrefix.Length + 10 + volSuffix.Length + 2 + genreLabel.Length + 1; // 10=bar, 2=min gap, 1=trailing space

        var innerWidth = Math.Max(44, Math.Max(trackLineWidth, volLineWidth));

        // Top border
        var headerLabel = "\u2552\u2550 \u266b MUSIC \u2550";
        var topPad = Math.Max(0, innerWidth - headerLabel.Length);
        sb.AppendLine($"  {Border}{headerLabel}{new string('\u2550', topPad)}\u2555{Rst}");

        // Track line: │{trackContent}{pad}{stateContent} │
        var trackPad = innerWidth - trackContent.Length - stateContent.Length - 1; // -1 for trailing space
        if (trackPad < 1) trackPad = 1;
        sb.AppendLine($"  {Border}\u2502{NoteClr}{trackContent}{new string(' ', trackPad)}{StateClr}{stateContent} {Border}\u2502{Rst}");

        // Volume + genre line: │{volPrefix}{bar}{volSuffix}{pad}{genreLabel} │
        var volPad = innerWidth - volPrefix.Length - 10 - volSuffix.Length - genreLabel.Length - 1; // -1 for trailing space
        if (volPad < 1) volPad = 1;
        sb.AppendLine($"  {Border}\u2502{LabelClr}{volPrefix}{volumeBar}{LabelClr}{volSuffix}{new string(' ', volPad)}{LabelClr}{genreLabel} {Border}\u2502{Rst}");

        // Bottom border
        sb.AppendLine($"  {Border}\u2558{new string('\u2550', innerWidth)}\u255b{Rst}");
        sb.AppendLine();

        return new AnsiPassthroughRenderable(sb.ToString());
    }

    /// <summary>
    /// Renders the track listing panel.
    /// </summary>
    public static void RenderTrackList(MusicPlayerService player)
    {
        AnsiConsole.Write(BuildTrackListRenderable(player));
    }

    /// <summary>
    /// Builds the track listing panel as an IRenderable.
    /// </summary>
    public static IRenderable BuildTrackListRenderable(MusicPlayerService player)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        var tracks = player.GetAvailableTracks();
        if (tracks.Count == 0)
        {
            sb.AppendLine($"  {LabelClr}No tracks found.{Rst}");
            sb.AppendLine($"  {DimClr}Drop .mp3 files into ~/.mandocode/music/lofi/ or ~/.mandocode/music/synthwave/{Rst}");
            sb.AppendLine();
            return new AnsiPassthroughRenderable(sb.ToString());
        }

        var innerWidth = 44;
        sb.AppendLine($"  {Border}\u2552\u2550 {Header}\u266b Tracks {Border}\u2550{new string('\u2550', innerWidth - 12)}\u2555{Rst}");

        var grouped = tracks.GroupBy(t => t.Genre).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            var genreLabel = string.IsNullOrEmpty(group.Key) ? "Unknown" : char.ToUpper(group.Key[0]) + group.Key[1..];
            sb.AppendLine($"  {Border}\u2502  {Header}[{genreLabel}]{Rst}");

            foreach (var track in group)
            {
                var playing = player.CurrentTrack == track;
                var marker = playing ? $"{StateClr}\u25b6" : " ";
                sb.AppendLine($"  {Border}\u2502 {marker} {TrackClr}\u266b {track.Name}{Rst}");
            }
        }

        sb.AppendLine($"  {Border}\u2558{new string('\u2550', innerWidth)}\u255b{Rst}");
        sb.AppendLine($"  {DimClr}{tracks.Count} track(s) total{Rst}");
        sb.AppendLine();

        return new AnsiPassthroughRenderable(sb.ToString());
    }

    /// <summary>
    /// Builds a stopped state message as an IRenderable.
    /// </summary>
    private static IRenderable BuildStoppedRenderable()
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        var innerWidth = 44;
        sb.AppendLine($"  {Border}\u2552\u2550 {Header}\u266b MUSIC {Border}\u2550{new string('\u2550', innerWidth - 11)}\u2555{Rst}");
        sb.AppendLine($"  {Border}\u2502  {LabelClr}\u23f9 Stopped{new string(' ', innerWidth - 12)}{Border}\u2502{Rst}");
        sb.AppendLine($"  {Border}\u2502  {DimClr}Type /music to start playing{new string(' ', innerWidth - 31)}{Border}\u2502{Rst}");
        sb.AppendLine($"  {Border}\u2558{new string('\u2550', innerWidth)}\u255b{Rst}");
        sb.AppendLine();

        return new AnsiPassthroughRenderable(sb.ToString());
    }

    /// <summary>
    /// Builds an error message as an IRenderable when audio is unavailable.
    /// </summary>
    private static IRenderable BuildErrorRenderable(string error)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        var innerWidth = 44;
        sb.AppendLine($"  {Border}\u2552\u2550 {Header}\u266b MUSIC {Border}\u2550{new string('\u2550', innerWidth - 11)}\u2555{Rst}");
        sb.AppendLine($"  {Border}\u2502  \u001b[38;2;255;80;0m\u26a0 Audio not available{new string(' ', innerWidth - 22)}{Border}\u2502{Rst}");
        foreach (var line in error.Split('\n'))
        {
            var trimmed = line.Length > (innerWidth - 5) ? line[..(innerWidth - 8)] + "..." : line;
            var pad = innerWidth - 3 - trimmed.Length;
            if (pad < 0) pad = 0;
            sb.AppendLine($"  {Border}\u2502  {DimClr}{trimmed}{new string(' ', pad)}{Border}\u2502{Rst}");
        }
        sb.AppendLine($"  {Border}\u2558{new string('\u2550', innerWidth)}\u255b{Rst}");
        sb.AppendLine();

        return new AnsiPassthroughRenderable(sb.ToString());
    }

    /// <summary>
    /// Builds a volume bar with green gradient: dark → bright green for filled, dim for empty.
    /// </summary>
    private static string BuildGradientVolumeBar(float volume, int width = 10)
    {
        var filled = (int)(volume * width);
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < width; i++)
        {
            if (i < filled)
            {
                // Gradient from deep purple to cyan-mint across the filled portion
                var t = filled > 1 ? (double)i / (filled - 1) : 1.0;
                var r = (int)(140 - t * 140);  // 140 → 0
                var g = (int)(60 + t * 195);   // 60  → 255
                var b = (int)(200 + t * 55);   // 200 → 255
                sb.Append($"\u001b[38;2;{r};{g};{b}m\u2588");
            }
            else
            {
                sb.Append($"{DimClr}\u2591");
            }
        }

        return sb.ToString();
    }
}
