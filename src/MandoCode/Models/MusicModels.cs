using System.Text.Json.Serialization;

namespace MandoCode.Models;

/// <summary>
/// Persisted music preferences (volume, genre, autoplay).
/// </summary>
public class MusicConfig
{
    [JsonPropertyName("volume")]
    public float Volume { get; set; } = 0.5f;

    [JsonPropertyName("genre")]
    public string Genre { get; set; } = "lofi";

    [JsonPropertyName("autoPlay")]
    public bool AutoPlay { get; set; } = false;
}

/// <summary>
/// Info about a single bundled music track.
/// </summary>
public record MusicTrackInfo
{
    public string Name { get; init; } = "";
    public string Genre { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public TimeSpan Duration { get; init; }
}
