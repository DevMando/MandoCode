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
    private readonly string _audioBasePath;
    private readonly Random _random = new();
    private readonly object _lock = new();

    private WaveOutEvent? _waveOut;
    private LoopStream? _loopStream;
    private AudioFileReader? _audioFileReader;
    private List<MusicTrackInfo> _tracks = new();
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public MusicTrackInfo? CurrentTrack { get; private set; }
    public float Volume => _config.Music.Volume;
    public string Genre => _config.Music.Genre;
    public bool AudioAvailable { get; private set; } = true;
    public string? AudioError { get; private set; }

    public MusicPlayerService(MandoCodeConfig config)
    {
        _config = config;

        // Resolve audio path relative to the application binary
        var appDir = AppContext.BaseDirectory;
        _audioBasePath = Path.Combine(appDir, "Audio");

        DiscoverTracks();
    }

    /// <summary>
    /// Discovers all MP3 files in Audio/lofi/ and Audio/synthwave/ directories.
    /// </summary>
    private void DiscoverTracks()
    {
        _tracks.Clear();

        if (!Directory.Exists(_audioBasePath))
            return;

        foreach (var genreDir in Directory.GetDirectories(_audioBasePath))
        {
            var genre = Path.GetFileName(genreDir).ToLowerInvariant();
            var mp3Files = Directory.GetFiles(genreDir, "*.mp3", SearchOption.TopDirectoryOnly);

            foreach (var mp3 in mp3Files)
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
                AudioError = "No MP3 files found. Add .mp3 files to Audio/lofi/ or Audio/synthwave/ directories.";
                return false;
            }
        }

        // Pick a random track (avoid repeating current if possible)
        MusicTrackInfo track;
        if (genreTracks.Count > 1 && CurrentTrack != null)
        {
            var candidates = genreTracks.Where(t => t.FilePath != CurrentTrack.FilePath).ToList();
            track = candidates[_random.Next(candidates.Count)];
        }
        else
        {
            track = genreTracks[_random.Next(genreTracks.Count)];
        }

        return PlayTrack(track);
    }

    /// <summary>
    /// Plays a specific track with looped playback.
    /// </summary>
    private bool PlayTrack(MusicTrackInfo track)
    {
        lock (_lock)
        {
            // Stop any current playback
            StopInternal();

            try
            {
                _audioFileReader = new AudioFileReader(track.FilePath);
                _audioFileReader.Volume = _config.Music.Volume;
                _loopStream = new LoopStream(_audioFileReader);

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
            _audioFileReader?.Dispose();
        }
        catch { /* Swallow disposal errors */ }
        finally
        {
            _waveOut = null;
            _loopStream = null;
            _audioFileReader = null;
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
            if (_audioFileReader != null)
            {
                _audioFileReader.Volume = volume;
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
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _source.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                if (_source.Position == 0)
                {
                    // Source is empty — avoid infinite loop
                    break;
                }
                _source.Position = 0; // Loop back to start
            }
            totalRead += read;
        }
        return totalRead;
    }
}
