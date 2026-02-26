using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Renders an inline mini-display panel for music playback status.
/// Shows current track, volume bar, genre, and playback state.
/// </summary>
public static class MusicPlayerUI
{
    /// <summary>
    /// Renders the music status panel after a command is executed.
    /// </summary>
    public static void RenderStatus(MusicPlayerService player)
    {
        Console.WriteLine();

        if (!player.AudioAvailable && player.AudioError != null)
        {
            RenderError(player.AudioError);
            return;
        }

        var track = player.CurrentTrack;
        if (track == null && !player.IsPlaying && !player.IsPaused)
        {
            RenderStopped();
            return;
        }

        var trackName = track?.Name ?? "Unknown";
        var genre = char.ToUpper(player.Genre[0]) + player.Genre[1..];
        var volumePercent = (int)(player.Volume * 100);
        var volumeBar = BuildVolumeBar(player.Volume);
        var stateIcon = player.IsPaused ? "\u23f8 Paused" : "\u25b6 Playing";
        var note = MusicAsciiArt.GetRandomNote();

        // Truncate track name if needed
        if (trackName.Length > 24)
            trackName = trackName[..21] + "...";

        var panelWidth = 44;
        var border = new string('\u2500', panelWidth - 4);

        Console.WriteLine($"  \u250c\u2500 MUSIC \u2500{border[8..]}\u2510");

        // Track line
        var trackLine = $"  {note} {trackName}";
        var statePad = panelWidth - 2 - trackLine.Length - stateIcon.Length;
        if (statePad < 1) statePad = 1;
        Console.WriteLine($"  \u2502{trackLine}{new string(' ', statePad)}{stateIcon} \u2502");

        // Volume line
        var volLine = $"  Vol: {volumeBar} {volumePercent}%";
        var genreLabel = $"Genre: {genre}";
        var volPad = panelWidth - 2 - volLine.Length - genreLabel.Length;
        if (volPad < 1) volPad = 1;
        Console.WriteLine($"  \u2502{volLine}{new string(' ', volPad)}{genreLabel}\u2502");

        Console.WriteLine($"  \u2514{new string('\u2500', panelWidth - 2)}\u2518");
        Console.WriteLine();
    }

    /// <summary>
    /// Renders the track listing panel.
    /// </summary>
    public static void RenderTrackList(MusicPlayerService player)
    {
        Console.WriteLine();

        var tracks = player.GetAvailableTracks();
        if (tracks.Count == 0)
        {
            Console.WriteLine("  No tracks found.");
            Console.WriteLine("  Add .mp3 files to Audio/lofi/ or Audio/synthwave/ directories.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  \u250c\u2500 Available Tracks \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");

        var grouped = tracks.GroupBy(t => t.Genre).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            var genreLabel = char.ToUpper(group.Key[0]) + group.Key[1..];
            Console.WriteLine($"  \u2502  [{genreLabel}]");

            foreach (var track in group)
            {
                var playing = player.CurrentTrack?.FilePath == track.FilePath;
                var marker = playing ? " \u25b6" : "  ";
                Console.WriteLine($"  \u2502{marker} \u266b {track.Name}");
            }
        }

        Console.WriteLine($"  \u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
        Console.WriteLine($"  {tracks.Count} track(s) total");
        Console.WriteLine();
    }

    /// <summary>
    /// Renders a stopped state message.
    /// </summary>
    private static void RenderStopped()
    {
        Console.WriteLine("  \u250c\u2500 MUSIC \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
        Console.WriteLine("  \u2502  \u23f9 Stopped                                 \u2502");
        Console.WriteLine("  \u2502  Type /music to start playing               \u2502");
        Console.WriteLine("  \u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
        Console.WriteLine();
    }

    /// <summary>
    /// Renders an error message when audio is unavailable.
    /// </summary>
    private static void RenderError(string error)
    {
        Console.WriteLine("  \u250c\u2500 MUSIC \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
        Console.WriteLine("  \u2502  \u26a0 Audio not available                     \u2502");
        foreach (var line in error.Split('\n'))
        {
            var trimmed = line.Length > 38 ? line[..35] + "..." : line;
            Console.WriteLine($"  \u2502  {trimmed,-39}\u2502");
        }
        Console.WriteLine("  \u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
        Console.WriteLine();
    }

    /// <summary>
    /// Builds a visual volume bar: ████████░░ for 80%.
    /// </summary>
    private static string BuildVolumeBar(float volume, int width = 10)
    {
        var filled = (int)(volume * width);
        var empty = width - filled;
        return new string('\u2588', filled) + new string('\u2591', empty);
    }
}
