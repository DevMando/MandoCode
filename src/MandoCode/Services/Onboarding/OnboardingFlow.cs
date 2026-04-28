using MandoCode.Models;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// First-run onboarding orchestrator.
///
/// Handles the gating that surrounds picking a model:
/// - Trailing-slash auto-heal on the Ollama URL (silent — never prompts).
/// - Detect Ollama CLI / daemon, offer install + auto-start.
/// - Walk users through cloud sign-in, or auto-pull a sensible cloud default.
///
/// Prompts use Spectre's imperative <see cref="SelectionPrompt{T}"/> /
/// <see cref="TextPrompt{T}"/> / <see cref="ConfirmationPrompt"/> rather than the VDOM
/// wizard primitives — the VDOM Select had a redraw glitch where the cursor `>` didn't
/// follow arrow keys on the second iteration of a loop. Long-running operations are
/// wrapped in <see cref="AnsiConsole.Status"/> so users see live progress.
///
/// Connection prep runs in a single state-driven loop that picks the right sub-menu
/// (install vs daemon-start) based on whether the CLI is present and whether the URL
/// is local. No Skip option on connection-required steps — Ctrl+C remains the hard
/// escape if the user truly needs to bail.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly Action<string> _setStatus;
    private readonly Func<string, string, Func<string, string?>?, string?, Task<string>>? _promptTextVdom;

    /// <param name="setStatus">Updates the ambient VDOM status indicator during long ops.</param>
    /// <param name="promptTextVdom">
    /// Optional VDOM-aware text prompt (App.razor's WizardPromptTextAsync). When provided,
    /// URL entry routes through RazorConsole's TextInput component, which captures
    /// keystrokes reliably even when the VDOM render loop is active. The fourth
    /// argument is an optional initialValue that pre-fills the input box (so users
    /// can hit Enter to accept the default URL or edit it in place). Without this
    /// delegate, falls back to Spectre's TextPrompt for non-VDOM callers.
    /// </param>
    public OnboardingFlow(
        Action<string> setStatus,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        _setStatus = setStatus;
        _promptTextVdom = promptTextVdom;
    }

    public sealed record FlowResult(bool Connected, bool Skipped, string? FinalModel);

    /// <summary>
    /// Run the full onboarding decision tree against the supplied config. Mutates the
    /// config in place (endpoint heal, model pick, HasCompletedOnboarding) and saves it.
    /// When <paramref name="forceInteractive"/> is true, the silent fast path is
    /// skipped — used by /setup so users explicitly running it always get the wizard
    /// (rather than a no-op when their config looks superficially fine).
    /// </summary>
    public async Task<FlowResult> RunAsync(MandoCodeConfig config, bool forceInteractive = false, CancellationToken ct = default)
    {
        // Step 0 — silent preflight. Decide which path to take before showing any UI.
        _setStatus("Checking your setup...");
        var probe = await OllamaSetupHelper.ProbeAsync(config.OllamaEndpoint, ct);

        if (probe.WasHealed)
        {
            config.OllamaEndpoint = probe.NormalizedUrl;
            config.Save();
            AnsiConsole.MarkupLine($"[cyan]Detected trailing slash on Ollama URL — using {Spectre.Console.Markup.Escape(probe.NormalizedUrl)}[/]");
        }

        var cliInstalled = OllamaSetupHelper.IsOllamaCliInstalled();
        // Use the status-aware variant so a transient `/api/tags` failure during preflight
        // doesn't misroute a user (with models) into the no-models flow. If the call
        // outright failed, leave models=[] but skip the fast path and let the post-connect
        // refresh re-fetch with retries.
        var preflightModels = probe.Ok
            ? await OllamaSetupHelper.ListModelsWithStatusAsync(probe.NormalizedUrl, ct)
            : new OllamaSetupHelper.ListModelsResult(false, new List<string>(), "Daemon unreachable");
        var models = preflightModels.Models;
        var configuredModel = config.GetEffectiveModelName();
        var configuredModelPresent = preflightModels.Ok
                                     && !string.IsNullOrEmpty(configuredModel)
                                     && models.Contains(configuredModel);

        // Fast path: everything ready and the configured model is already pulled. Zero UI.
        // Skipped when forceInteractive is true — /setup users explicitly want the wizard
        // even when their config looks fine (most often because chat is failing despite
        // the model being in /api/tags, e.g. cloud signed-out scenarios).
        if (!forceInteractive && probe.Ok && cliInstalled && configuredModelPresent)
        {
            config.HasCompletedOnboarding = true;
            config.Save();
            _setStatus("");
            return new FlowResult(Connected: true, Skipped: false, FinalModel: configuredModel);
        }

        _setStatus("");

        // Decide intro: full Welcome panel only when there's actual connection setup
        // to do. If everything's connected and we're just here to pick/pull a model,
        // skip the panel + the "Run setup now?" confirm — the user already implicitly
        // wants to fix that since they have no working model.
        var connectionAlreadyGood = probe.Ok && cliInstalled;
        if (!connectionAlreadyGood)
        {
            DisplayHeader();
            if (probe.Ok)
            {
                AnsiConsole.MarkupLine($"[green]✓ Connected to Ollama at[/] [white]{Spectre.Console.Markup.Escape(probe.NormalizedUrl)}[/]");
                AnsiConsole.WriteLine();
            }
            if (!Confirm("Run a quick setup now?", true))
            {
                AnsiConsole.MarkupLine("[dim]Skipped. Run /setup any time to launch this again.[/]");
                return new FlowResult(Connected: probe.Ok, Skipped: true, FinalModel: configuredModel);
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓ Connected to Ollama at[/] [white]{Spectre.Console.Markup.Escape(probe.NormalizedUrl)}[/]");
            AnsiConsole.MarkupLine("[dim]Let's pick a model so you can get started.[/]");
            AnsiConsole.WriteLine();
        }

        // Connection prep — single loop that escalates until both CLI and daemon are
        // ready. Picks install vs daemon-start menu based on current state. Continues
        // until probe.Ok (and CLI present, when targeting localhost).
        probe = await EnsureConnectedAsync(config, probe, cliInstalled, ct);

        // Refresh model list against the (possibly new) URL — retry once on transient
        // failure so a momentary daemon hiccup doesn't misroute a user with pulled
        // models into the "No models yet" flow. If the second attempt also fails,
        // surface a clear recovery message and exit with HasCompletedOnboarding=false
        // so /setup auto-fires next launch.
        var fetched = await FetchModelsWithRetryAsync(probe.NormalizedUrl, ct);
        if (!fetched.Ok)
        {
            AnsiConsole.MarkupLine($"[yellow]Couldn't fetch your model list from {Spectre.Console.Markup.Escape(probe.NormalizedUrl)}.[/]");
            AnsiConsole.MarkupLine($"[dim]Details: {Spectre.Console.Markup.Escape(fetched.Error ?? "unknown error")}[/]");
            AnsiConsole.MarkupLine("[dim]Run [cyan]/setup[/] to try again, or [cyan]/retry[/] to re-check the connection. The wizard will run again automatically next launch.[/]");
            AnsiConsole.WriteLine();
            // Don't set HasCompletedOnboarding — we want auto-rerun on next launch.
            config.Save();
            return new FlowResult(Connected: probe.Ok, Skipped: true, FinalModel: null);
        }
        models = fetched.Models;

        // Step 3 — Model source. Returns null when the user paused (e.g., picked
        // Local but had no models pulled). A null pick is NOT a successful setup —
        // we don't validate a model the user didn't choose, don't print a misleading
        // "Setup complete. Using <fallback>" line, and don't set HasCompletedOnboarding
        // so /setup auto-fires next launch.
        var pickedModel = await PickModelAsync(probe.NormalizedUrl, models, ct);

        if (string.IsNullOrEmpty(pickedModel))
        {
            AnsiConsole.MarkupLine("[yellow]Setup paused — no model selected.[/]");
            AnsiConsole.MarkupLine("[dim]Pull a model (e.g. [cyan]ollama pull qwen3:8b[/]) and run [cyan]/setup[/] when you're ready — or [cyan]/config[/] to pick from existing models.[/]");
            AnsiConsole.MarkupLine("[dim]The wizard will run again automatically next launch.[/]");
            AnsiConsole.WriteLine();
            // Save URL changes / heal state, but leave HasCompletedOnboarding=false
            // so the wizard re-engages next launch.
            config.Save();
            return new FlowResult(Connected: probe.Ok, Skipped: true, FinalModel: null);
        }

        config.ModelName = pickedModel;
        config.ModelPath = null;

        // Cloud-model auth check — pulled cloud models stick around in /api/tags after
        // sign-out, so a model showing in the picker doesn't prove the daemon can
        // actually USE it. The picker can't catch this; only a real /api/generate call
        // does. If 401, walk the user through `ollama signin` before declaring success.
        if (pickedModel.Contains(":cloud", StringComparison.OrdinalIgnoreCase))
        {
            _setStatus("Verifying cloud authentication...");
            var authTest = await OllamaSetupHelper.TestCloudAuthAsync(probe.NormalizedUrl, pickedModel, ct);
            _setStatus("");
            if (authTest.Unauthorized)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[yellow]The daemon returned 401 Unauthorized when testing {Spectre.Console.Markup.Escape(pickedModel)}.[/]");
                AnsiConsole.MarkupLine("[dim]The model is on disk, but the daemon isn't signed in for cloud calls.[/]");
                AnsiConsole.WriteLine();

                var signedIn = await WalkThroughCloudSigninAsync(probe.NormalizedUrl, ct);
                if (!signedIn)
                {
                    AnsiConsole.MarkupLine("[yellow]Setup paused — sign-in didn't complete.[/]");
                    AnsiConsole.MarkupLine("[dim]Cloud chat will return 401 until `ollama signin` succeeds. Run /setup again when ready, or /config to switch to a non-cloud model.[/]");
                    config.Save();
                    return new FlowResult(Connected: probe.Ok, Skipped: true, FinalModel: null);
                }
            }
        }

        // Validate the picked model via /api/show before declaring success — surfaces
        // models that appeared in /api/tags but aren't actually loadable, before the
        // user hits the chat and gets a confusing "couldn't validate model" later.
        var finalModel = config.GetEffectiveModelName();
        var validated = await OllamaSetupHelper.ValidateModelAsync(probe.NormalizedUrl, finalModel, ct);
        if (!validated)
        {
            AnsiConsole.MarkupLine($"[yellow]Note: model [white]{Spectre.Console.Markup.Escape(finalModel)}[/] didn't validate via /api/show.[/]");
            AnsiConsole.MarkupLine($"[dim]You may need to run: [cyan]ollama pull {Spectre.Console.Markup.Escape(finalModel)}[/] and then /retry — or /setup to pick a different model.[/]");
        }

        config.HasCompletedOnboarding = true;
        config.Save();

        AnsiConsole.MarkupLine($"[green]✓ Setup complete. Using {Spectre.Console.Markup.Escape(finalModel)}[/]");
        AnsiConsole.MarkupLine("[dim]Tip: run [cyan]/config[/] any time to switch models, change context size, or tweak settings.[/]");
        AnsiConsole.WriteLine();

        return new FlowResult(Connected: true, Skipped: false, FinalModel: finalModel);
    }

    /// <summary>
    /// Drive the user through whichever combination of (install / start daemon /
    /// change URL) is needed until the daemon is reachable. Returns the working probe
    /// result. Never returns a non-Ok result — the loop only exits on success
    /// (Ctrl+C is the hard escape).
    /// </summary>
    private async Task<OllamaSetupHelper.ProbeResult> EnsureConnectedAsync(
        MandoCodeConfig config,
        OllamaSetupHelper.ProbeResult probe,
        bool cliInstalled,
        CancellationToken ct)
    {
        while (!probe.Ok || (!cliInstalled && OllamaSetupHelper.IsLocalUrl(config.OllamaEndpoint)))
        {
            // Prefer install when the CLI is missing AND we're targeting localhost —
            // we can't start a local daemon without the CLI.
            if (!cliInstalled && OllamaSetupHelper.IsLocalUrl(config.OllamaEndpoint))
            {
                await GuideOllamaInstallAsync(ct);
            }
            else
            {
                var newProbe = await EnsureDaemonRunningAsync(config, ct);
                if (newProbe.WasHealed)
                {
                    config.OllamaEndpoint = newProbe.NormalizedUrl;
                    config.Save();
                }
                probe = newProbe;
            }

            // Refresh state for the next iteration. After install or URL change,
            // these may have flipped.
            cliInstalled = OllamaSetupHelper.IsOllamaCliInstalled();
            if (!probe.Ok)
            {
                _setStatus("Re-checking Ollama...");
                probe = await OllamaSetupHelper.ProbeAsync(config.OllamaEndpoint, ct);
                _setStatus("");
                if (probe.WasHealed)
                {
                    config.OllamaEndpoint = probe.NormalizedUrl;
                    config.Save();
                }
            }
        }
        return probe;
    }

    // ── Spectre prompt helpers ────────────────────────────────────────────────────

    private static string Select(string title, params string[] options)
        => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[cyan]{title}[/]")
                .HighlightStyle(new Style(foreground: Color.Green))
                .AddChoices(options));

    private static bool Confirm(string title, bool defaultYes)
        => AnsiConsole.Prompt(new ConfirmationPrompt($"[cyan]{title}[/]") { DefaultValue = defaultYes });

    /// <summary>
    /// Prompt for a URL. Prefers the VDOM TextInput when the host wired one in,
    /// because Spectre's TextPrompt can drop keystrokes while the VDOM render loop
    /// is live. Falls back to Spectre when there's no VDOM host (CLI / tests).
    /// </summary>
    private async Task<string> AskUrl(string title, string defaultValue)
    {
        Func<string, string?> validateOrFallback = v =>
        {
            // Empty submission falls back to defaultValue at the call site; only flag
            // non-empty values that don't parse as URLs.
            if (string.IsNullOrWhiteSpace(v)) return null;
            return Uri.TryCreate(v, UriKind.Absolute, out _)
                ? null
                : "Please enter a full URL like http://192.168.1.50:11434";
        };

        if (_promptTextVdom != null)
        {
            // Pre-fill with the default URL so the user can press Enter to accept or
            // edit in place. Inline "(press Enter to keep)" hint removes ambiguity
            // for first-timers who might not realize the pre-filled value is editable.
            var titleWithHint = $"{title} (press Enter to keep current)";
            var entered = await _promptTextVdom(titleWithHint, defaultValue, validateOrFallback, defaultValue);
            return string.IsNullOrWhiteSpace(entered) ? defaultValue : entered.Trim();
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[cyan]{title}[/]")
                .DefaultValue(defaultValue)
                .Validate(v =>
                    Uri.TryCreate(v, UriKind.Absolute, out _)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Please enter a full URL like http://192.168.1.50:11434[/]")));
    }

    /// <summary>
    /// "Press Enter to continue" gate. Uses an empty-allowed TextPrompt instead of
    /// ConfirmationPrompt — we don't want a misleading Y/N for a "wait for the user
    /// to be ready" pause.
    /// </summary>
    private static void PressEnterToContinue(string label)
        => AnsiConsole.Prompt(new TextPrompt<string>($"[dim]{label}[/]").AllowEmpty());

    private static void DisplayHeader()
    {
        var panel = new Panel(
            Align.Center(
                new Markup("[bold cyan]Welcome to MandoCode[/]\n[dim]Let's get you set up — takes about a minute[/]"),
                VerticalAlignment.Middle))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static async Task GuideOllamaInstallAsync(CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[yellow]Ollama not found[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var cmd = OllamaSetupHelper.GetOsInstallCommand();

        // Lead with auto-install — keeps the menu tight and the recommended action
        // up front. The download-page fallback only surfaces if the auto-install
        // actually fails (winget missing, UAC denied, no curl on minimal Linux,
        // etc.), so users who hit that path don't need a separate menu choice.
        var options = new List<string>();
        if (cmd != null) options.Add($"Install Ollama for me (runs `{cmd}` here)");
        options.Add("I've installed it, retry");

        var choice = Select("What would you like to do?", options.ToArray());

        if (choice.StartsWith("Install"))
        {
            AnsiConsole.MarkupLine($"[cyan]Running: {Spectre.Console.Markup.Escape(cmd!)}[/]");
            AnsiConsole.MarkupLine("[dim](inherited stdio — answer any prompts the installer shows)[/]");
            AnsiConsole.WriteLine();

            var exitCode = await OllamaSetupHelper.InstallOllamaAsync(ct);
            AnsiConsole.WriteLine();

            if (exitCode == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ Installer reported success.[/]");
            }
            else
            {
                if (exitCode == -1)
                    AnsiConsole.MarkupLine("[yellow]Couldn't launch the installer (winget/brew/curl may not be available).[/]");
                else
                    AnsiConsole.MarkupLine($"[yellow]Installer exited with code {exitCode} — install may not have completed.[/]");
                AnsiConsole.MarkupLine("[dim]Opening the download page as a fallback — finish the install there, then return.[/]");
                AnsiConsole.WriteLine();
                OllamaSetupHelper.OpenInBrowser("https://ollama.com/download");
            }
        }

        PressEnterToContinue("Press Enter once Ollama is installed");
    }

    private async Task<OllamaSetupHelper.ProbeResult> EnsureDaemonRunningAsync(MandoCodeConfig config, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[yellow]Couldn't reach Ollama[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Tried: {Spectre.Console.Markup.Escape(config.OllamaEndpoint)}[/]");
        AnsiConsole.WriteLine();

        // Tailor the menu to the URL: starting a local daemon makes no sense when the
        // user has pointed mandocode at a remote Ollama box.
        while (true)
        {
            var localUrl = OllamaSetupHelper.IsLocalUrl(config.OllamaEndpoint);
            var choice = localUrl
                ? Select("What would you like to do?",
                    "Start Ollama for me (local)",
                    "Use a different Ollama URL")
                : Select("What would you like to do?",
                    "Use a different Ollama URL");

            if (choice.StartsWith("Start"))
            {
                var probe = await StartDaemonAndProbeAsync(config.OllamaEndpoint, ct);
                if (probe.Ok)
                {
                    AnsiConsole.MarkupLine($"[green]✓ Connected to Ollama at[/] [white]{Spectre.Console.Markup.Escape(probe.NormalizedUrl)}[/]");
                    return probe;
                }

                // Start succeeded (process spawned) but the configured URL didn't reach
                // the daemon — most often because the user's config points at a
                // non-default port like :1313 while ollama serve binds to :11434. Try
                // the canonical default automatically before bouncing the user back to
                // the menu so they don't have to retype "http://localhost:11434".
                const string defaultUrl = "http://localhost:11434";
                if (OllamaSetupHelper.IsLocalUrl(config.OllamaEndpoint)
                    && !string.Equals(config.OllamaEndpoint, defaultUrl, StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.WriteLine(); // separate previous spinner from next one
                    var fallback = await ProbeWithSpinnerAsync(defaultUrl, $"Trying default endpoint {defaultUrl}...", ct);
                    if (fallback.Ok)
                    {
                        AnsiConsole.WriteLine(); // ensure spinner line is fully cleared before output
                        AnsiConsole.MarkupLine($"[green]✓ Default endpoint found — Ollama is running at[/] [white]{defaultUrl}[/]");
                        AnsiConsole.MarkupLine($"[dim]Switching from {Spectre.Console.Markup.Escape(config.OllamaEndpoint)} to {defaultUrl} and continuing setup.[/]");
                        AnsiConsole.WriteLine();
                        config.OllamaEndpoint = defaultUrl;
                        config.Save();
                        return fallback;
                    }
                }
                continue;
            }

            // "Use a different Ollama URL"
            var entered = await AskUrl("Enter your Ollama URL", config.OllamaEndpoint);
            var probed = await ProbeWithSpinnerAsync(entered, $"Probing {entered}...", ct);
            if (probed.Ok)
            {
                AnsiConsole.MarkupLine($"[green]✓ Connected to Ollama at[/] [white]{Spectre.Console.Markup.Escape(probed.NormalizedUrl)}[/]");
                config.OllamaEndpoint = probed.NormalizedUrl;
                config.Save();
                return probed;
            }
            AnsiConsole.MarkupLine($"[yellow]Couldn't reach {Spectre.Console.Markup.Escape(entered)} — {Spectre.Console.Markup.Escape(probed.Error ?? "unknown error")}[/]");
        }
    }

    /// <summary>
    /// Spawn `ollama serve`, then poll the configured URL with a visible spinner.
    /// Splits "process didn't launch" from "process launched but URL doesn't reach
    /// it" so the failure message tells the truth — important when the user's config
    /// points at a non-default port or a remote host that a local start can't fix.
    /// </summary>
    private async Task<OllamaSetupHelper.ProbeResult> StartDaemonAndProbeAsync(string url, CancellationToken ct)
    {
        var startedProcess = false;
        OllamaSetupHelper.ProbeResult probe = new(false, url, false, "Not yet probed");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting ollama serve...", async ctx =>
            {
                startedProcess = OllamaSetupHelper.TryStartOllamaProcess();
                if (!startedProcess) return;

                ctx.Status($"Waiting for Ollama at {Spectre.Console.Markup.Escape(url)}...");
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    probe = await OllamaSetupHelper.ProbeAsync(url, ct);
                    if (probe.Ok) return;
                    await Task.Delay(500, ct);
                }
            });

        if (probe.Ok) return probe;

        if (!startedProcess)
        {
            AnsiConsole.MarkupLine("[yellow]Couldn't launch `ollama serve` — is the Ollama CLI on your PATH?[/]");
            AnsiConsole.MarkupLine("[dim]Try running `ollama serve` in another terminal, then choose Retry.[/]");
            return probe;
        }

        // Process did launch — the URL is the problem. Tailor the hint to the URL:
        // remote URLs can't be helped by a local daemon. The non-default-port case is
        // intentionally not surfaced here — the caller auto-probes the canonical
        // http://localhost:11434 next, which usually resolves it without user action.
        AnsiConsole.MarkupLine($"[yellow]Started `ollama serve` but couldn't reach {Spectre.Console.Markup.Escape(url)}.[/]");
        if (!OllamaSetupHelper.IsLocalUrl(url))
        {
            AnsiConsole.MarkupLine("[dim]This URL points at a remote host. Starting a local daemon won't help — make sure Ollama is running on that machine, or pick \"Use a different Ollama URL\".[/]");
        }
        else if (url.Contains(":11434"))
        {
            AnsiConsole.MarkupLine("[dim]The daemon may still be starting. Try Start again, or check `ollama serve` output for errors.[/]");
        }
        return probe;
    }

    /// <summary>
    /// Fetch the model list, retrying once after a brief delay if the first attempt
    /// fails (transient daemon hiccups are surprisingly common right after starting
    /// `ollama serve`). Returns the first successful result, or the second failure.
    /// </summary>
    private async Task<OllamaSetupHelper.ListModelsResult> FetchModelsWithRetryAsync(string url, CancellationToken ct)
    {
        var first = await OllamaSetupHelper.ListModelsWithStatusAsync(url, ct);
        if (first.Ok) return first;

        _setStatus("Couldn't fetch model list — retrying...");
        await Task.Delay(1000, ct);
        var second = await OllamaSetupHelper.ListModelsWithStatusAsync(url, ct);
        _setStatus("");
        return second;
    }

    private async Task<OllamaSetupHelper.ProbeResult> ProbeWithSpinnerAsync(string url, string spinnerLabel, CancellationToken ct)
    {
        OllamaSetupHelper.ProbeResult result = new(false, url, false, "Not yet probed");
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(spinnerLabel, async _ =>
            {
                result = await OllamaSetupHelper.ProbeAsync(url, ct);
            });
        return result;
    }

    private async Task<string?> PickModelAsync(string url, List<string> models, CancellationToken ct)
    {
        if (models.Count == 0)
            return await PickWhenEmptyAsync(url, ct);

        AnsiConsole.Write(new Rule("[yellow]Pick a model[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // One combined picker — devs scan their pulled models and pick directly.
        // The previous "Cloud or Local?" intermediate select was extra friction for
        // users with mixed setups. Cloud models bubble to the top because they're
        // typically more capable; local models follow alphabetically. Inline badges
        // keep the cloud/local distinction visible without a separate question, and
        // the one-line explainer educates newcomers without nagging devs.
        AnsiConsole.MarkupLine("[dim]Cloud models run on ollama.com servers (more capable, no GPU needed, free with sign-in).[/]");
        AnsiConsole.MarkupLine("[dim]Local models run on your own hardware (private, offline-capable).[/]");
        AnsiConsole.WriteLine();

        // Use parentheses, not brackets, for the badge — Spectre's SelectionPrompt
        // parses bracket-style markup, so "[local]" would be misinterpreted as a
        // (nonexistent) style tag and throw.
        var labeled = models
            .OrderBy(m => m.Contains(":cloud", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Contains(":cloud", StringComparison.OrdinalIgnoreCase)
                ? $"{m}   (cloud)"
                : $"{m}   (local)")
            .ToArray();

        // Always show the picker, even for a single model. Auto-picking is faster but
        // surprising — the user expects to confirm what's about to be set.
        var picked = Select("Select a model:", labeled);
        return picked.Split(' ')[0];
    }

    private async Task<string?> PickWhenEmptyAsync(string url, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule("[yellow]No models yet[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Cloud models are more powerful and run on Ollama's servers — they need a free ollama.com account.[/]");
        AnsiConsole.MarkupLine("[dim]Local models are private and run on your hardware.[/]");
        AnsiConsole.WriteLine();

        var source = Select("Which would you like?",
            $"Cloud (recommended — auto-installs {MandoCodeConfig.DefaultCloudModel})",
            "Local — pick a starter model that fits your hardware");

        if (source.StartsWith("Cloud"))
            return await EnsureCloudModelAsync(url, ct);

        // Local — give the user a tiered picker. We can't auto-detect VRAM, so spell
        // out the hardware expectation up front and let them self-select. Each label
        // is "<tag> ..." so the first whitespace-split grabs the tag.
        AnsiConsole.Write(new Rule("[yellow]How big should it be?[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Bigger models give better answers, but use more memory and run slower without a GPU.[/]");
        AnsiConsole.MarkupLine("[dim]Rough guide for what to expect:[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  0.8b   ~1.0 GB   CPU-only / integrated GPU — fast on any laptop, short replies, light reasoning[/]");
        AnsiConsole.MarkupLine("[dim]  2b     ~2.7 GB   Modern CPU or 4 GB+ GPU — quick Q&A, simple code edits[/]");
        AnsiConsole.MarkupLine("[dim]  4b     ~3.4 GB   Mid-range GPU (4-6 GB VRAM) or 16 GB RAM — balanced day-to-day use[/]");
        AnsiConsole.MarkupLine("[dim]  9b     ~6.6 GB   Dedicated GPU (8+ GB VRAM) — best local quality, multi-file refactors[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Cloud tip: [white]{MandoCodeConfig.DefaultCloudModel}[/] outperforms all of these and needs no GPU (free with [cyan]ollama signin[/]).[/]");
        AnsiConsole.WriteLine();

        var localChoice = Select("Pick a starter model to install:",
            "qwen3.5:0.8b   (~1.0 GB)",
            "qwen3.5:2b     (~2.7 GB)",
            "qwen3.5:4b     (~3.4 GB)",
            "qwen3.5:9b     (~6.6 GB)",
            "Skip — I'll pull one myself");

        if (localChoice.StartsWith("Skip"))
        {
            AnsiConsole.MarkupLine("[dim]Pull a model and run /setup again. Suggestions:[/]");
            AnsiConsole.MarkupLine("[dim]  ollama pull qwen3.5:4b[/]");
            AnsiConsole.MarkupLine("[dim]  ollama pull qwen2.5-coder:14b[/]");

            if (Confirm("Open ollama.com/library in your browser?", true))
                OllamaSetupHelper.OpenInBrowser("https://ollama.com/library");
            return null;
        }

        var modelTag = localChoice.Split(' ')[0];
        var pulled = await PullModelWithProgressAsync(modelTag, ct);
        if (!pulled)
        {
            AnsiConsole.MarkupLine($"[yellow]Pull didn't finish cleanly. Retry with: [cyan]ollama pull {Spectre.Console.Markup.Escape(modelTag)}[/][/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]✓ Pulled {Spectre.Console.Markup.Escape(modelTag)}[/]");
        AnsiConsole.WriteLine();
        // Cloud upsell — informational, not pushy. Local works fine; cloud is just
        // genuinely more capable for users willing to sign in.
        AnsiConsole.MarkupLine($"[dim]Tip: [white]{MandoCodeConfig.DefaultCloudModel}[/] is more powerful and needs no GPU (free with [cyan]ollama signin[/]).[/]");
        AnsiConsole.MarkupLine("[dim]Run [cyan]/setup[/] any time to switch to a cloud model.[/]");
        return modelTag;
    }

    /// <summary>
    /// Run `ollama pull &lt;tag&gt;` inside a Spectre Status spinner, surfacing the latest
    /// progress line as the spinner label. Used for both the cloud auto-pull and the
    /// local starter-model auto-pull.
    /// </summary>
    private static async Task<bool> PullModelWithProgressAsync(string modelTag, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[cyan]Pulling {Spectre.Console.Markup.Escape(modelTag)}...[/]");
        var lastLine = "";
        var progress = new Progress<string>(line => lastLine = line);

        var pulled = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Pulling {modelTag}...", async ctx =>
            {
                var pullTask = OllamaSetupHelper.AutoPullAsync(modelTag, progress, ct);
                while (!pullTask.IsCompleted)
                {
                    if (!string.IsNullOrEmpty(lastLine))
                        ctx.Status($"[dim]{Spectre.Console.Markup.Escape(Truncate(lastLine, 80))}[/]");
                    await Task.Delay(200, ct);
                }
                pulled = await pullTask;
            });
        return pulled;
    }

    private async Task<string?> EnsureCloudModelAsync(string url, CancellationToken ct)
    {
        var auth = await OllamaSetupHelper.CheckCloudSignInAsync(url, ct);
        if (auth != OllamaSetupHelper.CloudAuthState.SignedIn)
        {
            if (!await WalkThroughCloudSigninAsync(url, ct))
                return null;
        }

        var pulled = await PullModelWithProgressAsync(MandoCodeConfig.DefaultCloudModel, ct);
        if (!pulled)
        {
            AnsiConsole.MarkupLine($"[yellow]Pull didn't finish cleanly. If you're not signed in, run [cyan]ollama signin[/] then retry with: [cyan]ollama pull {MandoCodeConfig.DefaultCloudModel}[/][/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]✓ Pulled {MandoCodeConfig.DefaultCloudModel}[/]");
        return MandoCodeConfig.DefaultCloudModel;
    }

    /// <summary>
    /// Cloud sign-in walkthrough. Shared by:
    /// - Empty-models flow (initial onboarding for users with nothing pulled).
    /// - Post-pick auth check (cloud model picked but daemon returns 401).
    /// - 401-on-chat auto-trigger (App.razor invokes this directly when a chat
    ///   response contains a 401 error so users don't have to manually run /setup).
    /// Returns true if the daemon ends up signed in, false if the user skipped or
    /// `ollama signin` failed.
    /// </summary>
    public async Task<bool> WalkThroughCloudSigninAsync(string url, CancellationToken ct = default)
    {
        AnsiConsole.MarkupLine("[yellow]Cloud models need a free ollama.com account.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("MandoCode can sign you in by running [cyan]ollama signin[/] for you — your browser will");
        AnsiConsole.MarkupLine("open to confirm, and the local token gets saved automatically. Or you can run");
        AnsiConsole.MarkupLine("the command yourself in another terminal if you'd rather.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Note: signing in on ollama.com in your browser is NOT enough — the daemon[/]");
        AnsiConsole.MarkupLine("[dim]needs its own local token, which only the `ollama signin` CLI command writes.[/]");
        AnsiConsole.MarkupLine("[dim]If `ollama signin` isn't recognized, update Ollama from https://ollama.com/download[/]");
        AnsiConsole.WriteLine();

        var pick = Select("What would you like to do?",
            "Sign me in now (runs `ollama signin` here)",
            "I've already run `ollama signin` in a terminal, continue",
            "Skip — I'll set this up later");

        if (pick.StartsWith("Skip"))
        {
            AnsiConsole.MarkupLine("[dim]Skipped cloud sign-in. Run /setup again later if you'd like cloud models.[/]");
            return false;
        }

        if (pick.StartsWith("Sign"))
        {
            // Spawn `ollama signin` as a child process. Stdio is inherited so the
            // user sees the URL Ollama prints, the browser opens, and any prompts
            // are shown directly. We wait for it to exit, then re-check auth.
            AnsiConsole.MarkupLine("[cyan]Launching `ollama signin`...[/]");
            AnsiConsole.MarkupLine("[dim]Your browser will open to confirm your account. Come back here when it finishes.[/]");
            AnsiConsole.WriteLine();

            // Echo Ollama's stdout/stderr lines as they arrive so the user sees
            // progress. RunOllamaSigninAsync already auto-launches the browser and
            // re-prints the URL via AnsiConsole when it spots one in the output —
            // this callback is just for everything else (waiting messages, errors).
            var progress = new Progress<string>(line =>
                AnsiConsole.WriteLine(line));
            var exitCode = await OllamaSetupHelper.RunOllamaSigninAsync(progress, ct);
            AnsiConsole.WriteLine();

            if (exitCode == -1)
            {
                AnsiConsole.MarkupLine("[yellow]Couldn't launch `ollama signin` — is the Ollama CLI on your PATH and up to date?[/]");
                AnsiConsole.MarkupLine("[dim]Update Ollama from https://ollama.com/download, then re-run /setup. Or run `ollama signin` manually in another terminal.[/]");
                return false;
            }
            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]`ollama signin` exited with code {exitCode} — sign-in may not have completed.[/]");
                AnsiConsole.MarkupLine("[dim]You can retry from /setup, or run `ollama signin` manually.[/]");
                return false;
            }

            AnsiConsole.MarkupLine("[green]`ollama signin` finished. Re-checking authentication...[/]");
        }

        // Final check — does the daemon report cloud-tag visibility now?
        var finalAuth = await OllamaSetupHelper.CheckCloudSignInAsync(url, ct);
        return finalAuth == OllamaSetupHelper.CloudAuthState.SignedIn;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
