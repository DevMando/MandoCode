using MandoCode.Models;
using NAudio.Wave;

namespace MandoCode.Services;

/// <summary>
/// Core audio engine for lofi/synthwave background music playback.
/// Uses NAudio WaveOutEvent with a LoopStream wrapper for seamless looping.
/// </summary>
public class MusicPlayerService : IDisposable
{
    private readonly MandoCodeConfig _config;
    private readonly System.Reflection.Assembly _assembly;
    private readonly string _userMusicPath;
    private readonly Random _random = new();
    private readonly object _lock = new();

    private WaveOutEvent? _waveOut;
    private LoopStream? _loopStream;
    private MemoryStream? _resourceStream;
    private Mp3FileReader? _mp3Reader;
    private WaveChannel32? _volumeChannel;
    private List<MusicTrackInfo> _tracks = new();
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public MusicTrackInfo? CurrentTrack { get; private set; }
    public float Volume => _config.Music.Volume;
    public string Genre => _config.Music.Genre;
    public bool AudioAvailable { get; private set; } = true;
    public string? AudioError { get; private set; }

    public string UserMusicPath => _userMusicPath;

    public MusicPlayerService(MandoCodeConfig config)
    {
        _config = config;
        _assembly = typeof(MusicPlayerService).Assembly;
        _userMusicPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mandocode", "music");

        EnsureUserMusicFolders();
        DiscoverTracks();
    }

    /// <summary>
    /// Creates ~/.mandocode/music/lofi/ and ~/.mandocode/music/synthwave/ if they don't exist.
    /// </summary>
    private void EnsureUserMusicFolders()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(_userMusicPath, "lofi"));
            Directory.CreateDirectory(Path.Combine(_userMusicPath, "synthwave"));
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Discovers MP3 tracks from embedded resources and the user's ~/.mandocode/music/ folder.
    /// Embedded resource names follow: {RootNamespace}.Audio.{genre}.{filename}.mp3
    /// User tracks follow: ~/.mandocode/music/{genre}/{filename}.mp3
    /// </summary>
    private void DiscoverTracks()
    {
        _tracks.Clear();

        // 1. Embedded resources (bundled defaults)
        var prefix = "MandoCode.Audio.";
        foreach (var resourceName in _assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix) || !resourceName.EndsWith(".mp3"))
                continue;

            var afterPrefix = resourceName[prefix.Length..];
            var firstDot = afterPrefix.IndexOf('.');
            if (firstDot < 0) continue;

            var genre = afterPrefix[..firstDot];
            var trackFile = afterPrefix[(firstDot + 1)..];
            var trackName = Path.GetFileNameWithoutExtension(trackFile).Replace('_', ' ');

            _tracks.Add(new MusicTrackInfo
            {
                Name = trackName,
                Genre = genre,
                FileName = trackFile,
                ResourceName = resourceName
            });
        }

        // 2. User's custom tracks from ~/.mandocode/music/{genre}/*.mp3
        if (Directory.Exists(_userMusicPath))
        {
            foreach (var genreDir in Directory.GetDirectories(_userMusicPath))
            {
                var genre = Path.GetFileName(genreDir).ToLowerInvariant();
                foreach (var mp3 in Directory.GetFiles(genreDir, "*.mp3", SearchOption.TopDirectoryOnly))
                {
                    _tracks.Add(new MusicTrackInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(mp3),
                        Genre = genre,
                        FileName = Path.GetFileName(mp3),
                        FilePath = mp3
                    });
                }
            }
        }
    }

    /// <summary>
    /// Gets distinct genre names from all discovered tracks.
    /// </summary>
    public List<string> GetAvailableGenres()
    {
        return _tracks
            .Select(t => t.Genre)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();
    }

    /// <summary>
    /// Gets all available tracks, optionally filtered by genre.
    /// </summary>
    public List<MusicTrackInfo> GetAvailableTracks(string? genre = null)
    {
        if (string.IsNullOrEmpty(genre))
            return _tracks.ToList();

        return _tracks.Where(t => t.Genre.Equals(genre, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Starts playing a random track from the specified genre (or current preference).
    /// </summary>
    public bool Play(string? genre = null)
    {
        genre ??= _config.Music.Genre;
        _config.Music.Genre = genre;

        var genreTracks = GetAvailableTracks(genre);
        if (genreTracks.Count == 0)
        {
            // Try fallback to any genre
            genreTracks = _tracks.ToList();
            if (genreTracks.Count == 0)
            {
                AudioError = $"No MP3 files found. Drop .mp3 files into ~/.mandocode/music/{{genre}}/ (e.g. {_userMusicPath}/lofi/)";
                return false;
            }
        }

        // Pick a random track (avoid repeating current if possible)
        MusicTrackInfo track;
        if (genreTracks.Count > 1 && CurrentTrack != null)
        {
            var candidates = genreTracks.Where(t => t != CurrentTrack).ToList();
            track = candidates[_random.Next(candidates.Count)];
        }
        else
        {
            track = genreTracks[_random.Next(genreTracks.Count)];
        }

        return PlayTrack(track);
    }

    /// <summary>
    /// Plays a specific track with looped playback. Supports both embedded resources and local files.
    /// </summary>
    private bool PlayTrack(MusicTrackInfo track)
    {
        lock (_lock)
        {
            // Stop any current playback
            StopInternal();

            try
            {
                if (!string.IsNullOrEmpty(track.FilePath))
                {
                    // Local file track
                    _mp3Reader = new Mp3FileReader(track.FilePath);
                }
                else
                {
                    // Embedded resource track
                    using var stream = _assembly.GetManifestResourceStream(track.ResourceName);
                    if (stream == null)
                    {
                        AudioError = $"Embedded audio resource not found: {track.ResourceName}";
                        return false;
                    }

                    // Copy to MemoryStream for seeking support (required for looping)
                    _resourceStream = new MemoryStream();
                    stream.CopyTo(_resourceStream);
                    _resourceStream.Position = 0;

                    _mp3Reader = new Mp3FileReader(_resourceStream);
                }

                _volumeChannel = new WaveChannel32(_mp3Reader);
                _volumeChannel.Volume = _config.Music.Volume;
                _loopStream = new LoopStream(_volumeChannel);

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_loopStream);
                _waveOut.Play();

                CurrentTrack = track;
                IsPlaying = true;
                IsPaused = false;
                AudioAvailable = true;
                AudioError = null;

                return true;
            }
            catch (Exception ex)
            {
                AudioAvailable = false;
                AudioError = $"Audio playback failed: {ex.Message}";
                if (ex.Message.Contains("NoDriver") || ex.Message.Contains("waveOut"))
                {
                    AudioError += "\nIf on WSL2, ensure PulseAudio/PipeWire is configured for audio output.";
                }
                StopInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops playback.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
            IsPlaying = false;
            IsPaused = false;
            CurrentTrack = null;
        }
        SavePreferences();
    }

    private void StopInternal()
    {
        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _loopStream?.Dispose();
            _volumeChannel?.Dispose();
            _mp3Reader?.Dispose();
            _resourceStream?.Dispose();
        }
        catch { /* Swallow disposal errors */ }
        finally
        {
            _waveOut = null;
            _loopStream = null;
            _volumeChannel = null;
            _mp3Reader = null;
            _resourceStream = null;
        }
    }

    /// <summary>
    /// Toggles pause/resume.
    /// </summary>
    public void TogglePause()
    {
        lock (_lock)
        {
            if (_waveOut == null) return;

            if (IsPaused)
            {
                _waveOut.Play();
                IsPaused = false;
                IsPlaying = true;
            }
            else
            {
                _waveOut.Pause();
                IsPaused = true;
                IsPlaying = false;
            }
        }
    }

    /// <summary>
    /// Skips to the next random track in the current genre.
    /// </summary>
    public bool NextTrack()
    {
        return Play(_config.Music.Genre);
    }

    /// <summary>
    /// Sets playback volume (0.0 - 1.0).
    /// </summary>
    public void SetVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        _config.Music.Volume = volume;

        lock (_lock)
        {
            if (_volumeChannel != null)
            {
                _volumeChannel.Volume = volume;
            }
        }

        SavePreferences();
    }

    /// <summary>
    /// Persists current music preferences to config.
    /// </summary>
    private void SavePreferences()
    {
        try { _config.Save(); } catch { /* non-critical */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            StopInternal();
        }
    }
}

/// <summary>
/// Wraps a WaveStream to loop seamlessly back to the start when the source ends.
/// </summary>
public class LoopStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopStream(WaveStream source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _source.Length;

    public override long Position
    {
        get => _source.Position;
        set
        {
            if (!_source.CanSeek)
                throw new NotSupportedException("Source stream does not support seeking");
            _source.Position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        int loopResets = 0;
        while (totalRead < count)
        {
            int read = _source.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                if (_source.Position == 0 || loopResets >= 3)
                {
                    // Source is empty or stuck — avoid infinite loop
                    break;
                }
                _source.Position = 0; // Loop back to start
                loopResets++;
            }
            else
            {
                loopResets = 0;
            }
            totalRead += read;
        }
        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
