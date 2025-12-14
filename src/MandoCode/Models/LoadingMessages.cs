namespace MandoCode.Models;

/// <summary>
/// Provides fun, random loading messages instead of boring "Thinking..."
/// </summary>
public static class LoadingMessages
{
    private static readonly Random _random = new();

    private static readonly string[] _messages =
    {
        "Pondering...",
        "Cogitating...",
        "Ruminating...",
        "Contemplating...",
        "Mushing...",
        "Noodling...",
        "Brewing...",
        "Materializing...",
        "Manifesting...",
        "Synthesizing...",
        "Concocting...",
        "Fabricating...",
        "Orchestrating...",
        "Choreographing...",
        "Harmonizing...",
        "Vibing...",
        "Grooving...",
        "Jamming...",
        "Riffing...",
        "Improvising...",
        "Composing...",
        "Crafting...",
        "Weaving...",
        "Knitting...",
        "Stitching...",
        "Forging...",
        "Smithing...",
        "Sculpting...",
        "Molding...",
        "Blueprinting...",
        "Architecting...",
        "Engineering...",
        "Tinkering...",
        "Fiddling...",
        "Dabbling...",
        "Experimenting...",
        "Discovering...",
        "Exploring...",
        "Adventuring...",
        "Questing...",
        "Voyaging...",
        "Wandering..."
    };

    /// <summary>
    /// Gets a random loading message
    /// </summary>
    /// <returns>A fun loading message string</returns>
    public static string GetRandom()
    {
        return _messages[_random.Next(_messages.Length)];
    }
}
