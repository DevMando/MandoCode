using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Manages terminal theme detection (OSC 11), ANSI palette customization (OSC 4),
/// and dynamic title bar status (OSC 0).
/// </summary>
public class TerminalThemeService : IDisposable
{
    private readonly MandoCodeConfig _config;
    private readonly TokenTrackingService _tokenTracker;
    private readonly ProjectRootAccessor _projectRoot;

    private readonly TerminalPaletteService _palette;
    private readonly bool _ownsPalette;

    /// <summary>
    /// When true, the music visualizer owns the title bar — status updates are suppressed.
    /// </summary>
    public bool IsMusicTitleActive { get; set; }

    public bool IsDarkTheme => _palette.IsDarkTheme;

    public TerminalThemeService(
        MandoCodeConfig config,
        TokenTrackingService tokenTracker,
        ProjectRootAccessor projectRoot, TerminalPaletteService? palette = null)
    {
        _palette = palette ?? new TerminalPaletteService();
        _ownsPalette = palette is null;
        _config = config;
        _tokenTracker = tokenTracker;
        _projectRoot = projectRoot;
    }

    public void DetectTheme() => _palette.DetectTheme();
    public void ApplyPalette() => _palette.ApplyPalette();
    public void ResetPalette() => _palette.ResetPalette();

    /// <summary>
    /// Updates the terminal title bar with model, project, and token info via OSC 0.
    /// Skipped when the music visualizer owns the title bar.
    /// </summary>
    public void UpdateStatusTitle()
    {
        if (IsMusicTitleActive)
            return;

        var model = _config.GetEffectiveModelName();
        var project = Path.GetFileName(_projectRoot.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tokens = TokenTrackingService.FormatTokenCount(_tokenTracker.TotalSessionTokens);

        var title = $"MandoCode \u2014 {model} \u2014 {project} \u2014 {tokens} tokens";
        Console.Write($"\u001b]0;{title}\u0007");
    }

    public void Dispose()
    {
        if (_ownsPalette)
            _palette.Dispose();
    }
}
