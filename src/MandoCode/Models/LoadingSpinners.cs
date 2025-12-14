namespace MandoCode.Models;

/// <summary>
/// Provides fun, random spinners from Spectre.Console instead of the same boring one
/// </summary>
public static class LoadingSpinners
{
    private static readonly Random _random = new();

    private static readonly Spectre.Console.Spinner[] _spinners =
    {
        Spectre.Console.Spinner.Known.Dots,
        Spectre.Console.Spinner.Known.Dots2,
        Spectre.Console.Spinner.Known.Dots3,
        Spectre.Console.Spinner.Known.Dots4,
        Spectre.Console.Spinner.Known.Dots5,
        Spectre.Console.Spinner.Known.Dots6,
        Spectre.Console.Spinner.Known.Dots7,
        Spectre.Console.Spinner.Known.Dots8,
        Spectre.Console.Spinner.Known.Dots9,
        Spectre.Console.Spinner.Known.Dots10,
        Spectre.Console.Spinner.Known.Dots11,
        Spectre.Console.Spinner.Known.Dots12,
        Spectre.Console.Spinner.Known.Line,
        Spectre.Console.Spinner.Known.Line2,
        Spectre.Console.Spinner.Known.Pipe,
        Spectre.Console.Spinner.Known.SimpleDots,
        Spectre.Console.Spinner.Known.SimpleDotsScrolling,
        Spectre.Console.Spinner.Known.Star,
        Spectre.Console.Spinner.Known.Star2,
        Spectre.Console.Spinner.Known.Flip,
        Spectre.Console.Spinner.Known.Hamburger,
        Spectre.Console.Spinner.Known.GrowVertical,
        Spectre.Console.Spinner.Known.GrowHorizontal,
        Spectre.Console.Spinner.Known.Balloon,
        Spectre.Console.Spinner.Known.Balloon2,
        Spectre.Console.Spinner.Known.Noise,
        Spectre.Console.Spinner.Known.Bounce,
        Spectre.Console.Spinner.Known.BoxBounce,
        Spectre.Console.Spinner.Known.BoxBounce2,
        Spectre.Console.Spinner.Known.Triangle,
        Spectre.Console.Spinner.Known.Arc,
        Spectre.Console.Spinner.Known.Circle,
        Spectre.Console.Spinner.Known.SquareCorners,
        Spectre.Console.Spinner.Known.CircleQuarters,
        Spectre.Console.Spinner.Known.CircleHalves,
        Spectre.Console.Spinner.Known.Squish,
        Spectre.Console.Spinner.Known.Toggle,
        Spectre.Console.Spinner.Known.Toggle2,
        Spectre.Console.Spinner.Known.Toggle3,
        Spectre.Console.Spinner.Known.Toggle4,
        Spectre.Console.Spinner.Known.Toggle5,
        Spectre.Console.Spinner.Known.Toggle6,
        Spectre.Console.Spinner.Known.Toggle7,
        Spectre.Console.Spinner.Known.Toggle8,
        Spectre.Console.Spinner.Known.Toggle9,
        Spectre.Console.Spinner.Known.Toggle10,
        Spectre.Console.Spinner.Known.Toggle11,
        Spectre.Console.Spinner.Known.Toggle12,
        Spectre.Console.Spinner.Known.Toggle13,
        Spectre.Console.Spinner.Known.Arrow,
        Spectre.Console.Spinner.Known.Arrow2,
        Spectre.Console.Spinner.Known.Arrow3,
        Spectre.Console.Spinner.Known.BouncingBar,
        Spectre.Console.Spinner.Known.BouncingBall,
        Spectre.Console.Spinner.Known.Smiley,
        Spectre.Console.Spinner.Known.Monkey,
        Spectre.Console.Spinner.Known.Hearts,
        Spectre.Console.Spinner.Known.Clock,
        Spectre.Console.Spinner.Known.Earth,
        Spectre.Console.Spinner.Known.Moon,
        Spectre.Console.Spinner.Known.Runner,
        Spectre.Console.Spinner.Known.Pong,
        Spectre.Console.Spinner.Known.Shark,
        Spectre.Console.Spinner.Known.Dqpb,
        Spectre.Console.Spinner.Known.Weather,
        Spectre.Console.Spinner.Known.Christmas,
        Spectre.Console.Spinner.Known.Grenade,
        Spectre.Console.Spinner.Known.Point,
        Spectre.Console.Spinner.Known.Layer
    };

    /// <summary>
    /// Gets a random spinner from Spectre.Console
    /// </summary>
    /// <returns>A random Spectre.Console.Spinner</returns>
    public static Spectre.Console.Spinner GetRandom()
    {
        return _spinners[_random.Next(_spinners.Length)];
    }
}
