using Spectre.Console;

namespace MandoCode.Models;

/// <summary>
/// Provides fun, random loading messages instead of boring "Thinking..."
/// </summary>
public static class LoadingMessages
{
    private static readonly Random _random = new();

    private static readonly Spinner[] _spinners =
    {
        Spinner.Known.Dots,
        Spinner.Known.Dots2,
        Spinner.Known.Dots3,
        Spinner.Known.Dots8,
        Spinner.Known.Dots9,
        Spinner.Known.Dots10,
        Spinner.Known.Dots11,
        Spinner.Known.Line,
        Spinner.Known.Star,
        Spinner.Known.Star2,
        Spinner.Known.Flip,
        Spinner.Known.Bounce,
        Spinner.Known.BouncingBar,
        Spinner.Known.BouncingBall,
        Spinner.Known.Pipe,
        Spinner.Known.Toggle,
        Spinner.Known.Toggle2,
        Spinner.Known.Toggle3,
        Spinner.Known.Arrow,
        Spinner.Known.Arrow3,
        Spinner.Known.Aesthetic,
        Spinner.Known.Earth,
        Spinner.Known.Moon,
        Spinner.Known.Monkey,
        Spinner.Known.Hearts,
        Spinner.Known.Clock,
        Spinner.Known.Grenade,
        Spinner.Known.Point,
        Spinner.Known.Layer,
        Spinner.Known.Hamburger,
        Spinner.Known.GrowVertical,
        Spinner.Known.GrowHorizontal,
        Spinner.Known.Noise,
        Spinner.Known.SimpleDots,
        Spinner.Known.SimpleDotsScrolling,
        Spinner.Known.Balloon,
        Spinner.Known.Balloon2,
        Spinner.Known.Dqpb,
        Spinner.Known.Christmas,
        Spinner.Known.Squish,
        Spinner.Known.Toggle4,
        Spinner.Known.Arc,
        Spinner.Known.Pong,
    };

    /// <summary>
    /// Gets a random spinner
    /// </summary>
    public static Spinner GetRandomSpinner()
    {
        return _spinners[_random.Next(_spinners.Length)];
    }

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
        "Wandering...",
        "Following the white rabbit...",
        "Decoding the Matrix...",
        "Bending the spoon...",
        "Taking the red pill...",
        "Jacking in..."
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
