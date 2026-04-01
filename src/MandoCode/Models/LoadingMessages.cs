using Spectre.Console;
using System.Linq;

namespace MandoCode.Models;

/// <summary>
/// Provides fun, random loading messages instead of boring "Thinking..."
/// </summary>
public static class LoadingMessages
{
    private static readonly Spinner[] _spinners =
        typeof(Spinner.Known)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Spinner))
            .Select(p => (Spinner?)p.GetValue(null))
            .Where(s => s != null)
            .Cast<Spinner>()
            .ToArray();

    /// <summary>
    /// Gets a random spinner
    /// </summary>
    public static Spinner GetRandomSpinner()
    {
        return _spinners[Random.Shared.Next(_spinners.Length)];
    }

    private static readonly string[] _messages =
    {
        "Reloading Sound Blaster...",
        "Dialing via 56k...",
        "Buffering MTV raps...",
        "Spinning the boombox...",
        "Windmillin'...",
        "Flairin'...",
        "Headspinning...",
        "AirTracking...",
        "Six-stepping...",
        "Swipping...",
        "Three-stepping...",
        "AirFlare-ing...",
        "Cranking the mixtape...",
        "Dropping science...",
        "Laying a beat...",
        "Walkin' this way...",
        "Rocking Adidas shelltoes...",
        "It's Tricky right now...",
        "King of Rock'ing...",
        "Sampling vinyl...",
        "Scratching records...",
        "Defragging drives...",
        "Patching Win95...",
        "Compiling...",
        "Rebooting...",
        "Overclocking...",
        "Packetizing...",
        "Encrypting...",
        "Reverse-engineering...",
        "Benchmarking...",
        "Netscape navigating...",
        "Skipping standup...",
        "Booting DOS...",
        "Pinging ICQ...",
        "GeoCity hopping...",
        "Kazaa downloading...",
        "Planting the C4...",
        "Defusing with a kit...",
        "Rush B no stop...",
        "Saving for an AWP...",
        "Flashbanging long A...",
        "Bunny hopping in mid...",
        "Wallbanging through double doors...",
        "Checking T-spawn corners...",
        "Hacking the Gibson...",
        "Tracing the backdoor...",
        "Spoofing mainframes...",
        "Decrypting streams...",
        "Riding the network rail...",
        "Following the white rabbit...",
        "Decoding the Matrix...",
        "Bending the spoon...",
        "Taking the red pill...",
    };

    /// <summary>
    /// Gets a random loading message
    /// </summary>
    /// <returns>A fun loading message string</returns>
    public static string GetRandom()
    {
        return _messages[Random.Shared.Next(_messages.Length)];
    }
}
