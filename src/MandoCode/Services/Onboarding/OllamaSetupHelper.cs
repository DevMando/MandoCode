using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Onboarding-time probes and side-effects: detect whether the Ollama CLI is installed,
/// whether the daemon is reachable, list models, pull a model, open URLs, etc.
///
/// Single source of truth so the OnboardingWizard, App.razor connection check, and the
/// CLI --doctor flag don't drift apart.
/// </summary>
public static class OllamaSetupHelper
{
    public sealed record ProbeResult(bool Ok, string NormalizedUrl, bool WasHealed, string? Error);

    public enum CloudAuthState
    {
        Unknown,
        NotReachable,
        SignedIn,
        NotSignedIn
    }

    /// <summary>
    /// Build a URL by trimming any trailing slash from the base and ensuring exactly one
    /// slash before the path. Defensive against the trailing-slash bug even after the
    /// config-level trim — every Ollama HTTP call should route through here.
    /// </summary>
    public static string BuildUrl(string baseUrl, string path)
    {
        var trimmedBase = (baseUrl ?? "").TrimEnd('/');
        var trimmedPath = (path ?? "").TrimStart('/');
        return $"{trimmedBase}/{trimmedPath}";
    }

    /// <summary>
    /// Probe Ollama at the exact URL the user provided. We deliberately preserve their
    /// input — no silent canonicalization — so the call reproduces whatever shape they
    /// typed. Auto-heal only fires when the as-typed URL fails AND trimming a trailing
    /// slash gives a working URL.
    ///
    /// Outcomes:
    /// - As-typed works → Ok=true, WasHealed=false, NormalizedUrl=user's input.
    /// - As-typed fails, trimmed works → Ok=true, WasHealed=true, NormalizedUrl=trimmed.
    /// - Both fail (or nothing to trim) → Ok=false, WasHealed=false, NormalizedUrl=user's input.
    ///
    /// Caller persists the trimmed value only when WasHealed=true.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ProbeResult(false, url ?? "", false, "Empty URL");

        // Attempt 1 — exactly what the user typed. Reaches the daemon as-is so a
        // trailing slash actually surfaces the "//api/tags" failure mode instead of
        // being papered over by BuildUrl.
        var asTyped = await TryProbeAsync(url, ct);
        if (asTyped.ok)
            return new ProbeResult(true, url, false, null);

        // Attempt 2 — only run if there's a trailing slash to remove. Heal silently
        // when the trimmed URL reaches the daemon.
        var trimmed = url.TrimEnd('/');
        if (trimmed != url)
        {
            var healed = await TryProbeAsync(trimmed, ct);
            if (healed.ok)
                return new ProbeResult(true, trimmed, true, null);
        }

        // Both failed — return the user's input verbatim so error messages quote
        // exactly what they configured.
        return new ProbeResult(false, url, false, asTyped.error);
    }

    private static async Task<(bool ok, string? error)> TryProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // Raw concat — preserves the user's URL shape (trailing slash → double
            // slash on the wire) so the probe sees the same failure mode they would.
            var response = await client.GetAsync($"{url}/api/tags", ct);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Returns true if the `ollama` CLI is on PATH OR present at a known install location.
    ///
    /// The PATH check alone misses a real failure mode: Windows installers update PATH
    /// for new processes only, so a user who installs Ollama via the wizard's "Open
    /// browser" step still sees `where ollama` fail in the running mandocode session.
    /// Checking the canonical install paths directly catches the post-install case
    /// without forcing the user to relaunch.
    /// </summary>
    public static bool IsOllamaCliInstalled()
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(which, "ollama")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(2000);
            if (proc.ExitCode == 0) return true;
        }
        catch { /* fall through to direct path check */ }

        foreach (var path in CandidateOllamaPaths())
        {
            if (File.Exists(path)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns "ollama" if the bare command is on PATH, otherwise a full path to
    /// the binary at one of the known install locations. Used wherever we spawn
    /// `ollama` as a child process — `Process.Start("ollama", ...)` with
    /// UseShellExecute=false fails when the running process inherited a stale
    /// PATH (extremely common right after the user installs Ollama via the
    /// wizard, since installer PATH updates only apply to NEW processes).
    /// </summary>
    public static string ResolveOllamaExecutable()
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(which, "ollama")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(2000);
            if (proc.ExitCode == 0) return "ollama";
        }
        catch { /* fall through to direct path check */ }

        foreach (var path in CandidateOllamaPaths())
        {
            if (File.Exists(path)) return path;
        }
        return "ollama"; // last-ditch: let Process.Start fail with a clear OS error
    }

    private static IEnumerable<string> CandidateOllamaPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
                yield return Path.Combine(localApp, "Programs", "Ollama", "ollama.exe");
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
                yield return Path.Combine(pf, "Ollama", "ollama.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/opt/homebrew/bin/ollama";
            yield return "/usr/local/bin/ollama";
            yield return "/Applications/Ollama.app/Contents/Resources/ollama";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            yield return "/usr/local/bin/ollama";
            yield return "/usr/bin/ollama";
        }
    }

    /// <summary>
    /// True when the URL targets the local machine (localhost / 127.0.0.1 / ::1 / 0.0.0.0).
    /// Used to decide whether to offer "Start Ollama for me" or insist on a local CLI install.
    /// Falls back to "treat as local" on parse failure so the wizard still offers helpful options.
    /// </summary>
    public static bool IsLocalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        var host = uri.Host.ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "0.0.0.0";
    }

    /// <summary>
    /// Result of a cloud-auth probe. <see cref="Unauthorized"/> is the load-bearing
    /// flag — true means we got a definitive 401 and the daemon isn't signed in for
    /// cloud calls. Other failures (network, timeout) leave Unauthorized=false so we
    /// don't push the user into a sign-in flow they don't actually need.
    /// </summary>
    public sealed record AuthTestResult(bool Ok, bool Unauthorized, string? Error);

    /// <summary>
    /// Verify the daemon can actually run a cloud model by hitting /api/generate
    /// with a 1-token request. Catches the common false-positive where the model is
    /// in /api/tags (so CheckCloudSignInAsync says SignedIn) but the user has since
    /// signed out — pulled models stick around in /api/tags but inference returns
    /// 401 because the daemon's local auth token is gone.
    /// </summary>
    public static async Task<AuthTestResult> TestCloudAuthAsync(string url, string modelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return new AuthTestResult(false, false, "Empty model name");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var body = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    model = modelName,
                    prompt = "hi",
                    stream = false,
                    options = new { num_predict = 1 }
                }),
                System.Text.Encoding.UTF8,
                "application/json");
            var response = await client.PostAsync(BuildUrl(url, "api/generate"), body, ct);
            if (response.IsSuccessStatusCode)
                return new AuthTestResult(true, false, null);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new AuthTestResult(false, true, "401 Unauthorized");
            return new AuthTestResult(false, false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new AuthTestResult(false, false, ex.Message);
        }
    }

    /// <summary>
    /// POSTs to /api/show to confirm the model exists on the server. Returns true on
    /// 2xx. The wizard runs this after the user picks a model so we can warn at save
    /// time rather than first chat.
    /// </summary>
    public static async Task<bool> ValidateModelAsync(string url, string modelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { name = modelName }),
                System.Text.Encoding.UTF8,
                "application/json");
            var response = await client.PostAsync(BuildUrl(url, "api/show"), body, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Spawn `ollama serve` detached. Returns whether <see cref="Process.Start(ProcessStartInfo)"/>
    /// succeeded — does NOT probe the URL afterward. Callers should probe themselves so
    /// they can distinguish "process didn't launch" from "process launched but configured
    /// URL is wrong" (e.g. config points at :2323 while ollama binds to default :11434).
    /// </summary>
    public static bool TryStartOllamaProcess()
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveOllamaExecutable(), "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = Process.Start(psi);
            return proc != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Run the OS-appropriate Ollama installer as a child process with inherited stdio.
    /// On Windows: `winget install Ollama.Ollama`. On macOS: `brew install ollama`. On
    /// Linux: pipes the official install.sh through sh (which sudo-prompts for the
    /// /usr/local/bin write step). User sees the installer's own progress and can
    /// answer any UAC/license/sudo prompts directly. Returns:
    /// 0  — installer reported success
    /// >0 — installer ran but failed/aborted
    /// -1 — couldn't launch (e.g. winget/brew not installed, unsupported OS)
    /// </summary>
    public static async Task<int> InstallOllamaAsync(CancellationToken ct = default)
    {
        try
        {
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi = new ProcessStartInfo("winget", "install --id Ollama.Ollama --accept-source-agreements --accept-package-agreements");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi = new ProcessStartInfo("brew", "install ollama");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // The official install.sh writes to /usr/local/bin and will sudo-prompt
                // mid-script — inherited stdio lets the user type their password.
                psi = new ProcessStartInfo("sh", "-c \"curl -fsSL https://ollama.com/install.sh | sh\"");
            }
            else
            {
                return -1;
            }

            psi.UseShellExecute = false;
            psi.RedirectStandardInput = false;
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.CreateNoWindow = false;

            using var proc = Process.Start(psi);
            if (proc == null) return -1;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Run `ollama signin` as a child process. Captures stdout/stderr so we can:
    /// (1) auto-launch the browser ourselves the moment Ollama prints its sign-in
    ///     URL — Ollama's own browser-opening is unreliable across terminals
    ///     (especially when MandoCode's VDOM render loop is repainting the screen),
    /// (2) re-emit the URL via AnsiConsole so it survives any subsequent VDOM redraw
    ///     and the user can copy it as a fallback if no browser pops open.
    /// Each captured line is also forwarded through onLine so the caller can echo
    /// it. Returns:
    /// 0  — success (auth completed)
    /// >0 — signin failed or was cancelled
    /// -1 — process couldn't be launched (CLI missing, version too old, etc.)
    /// </summary>
    public static async Task<int> RunOllamaSigninAsync(IProgress<string>? onLine = null, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveOllamaExecutable(), "signin")
            {
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var browserLaunched = false;
            void HandleLine(string? line)
            {
                if (line == null) return;
                onLine?.Report(line);

                if (browserLaunched) return;
                var url = ExtractFirstUrl(line);
                if (url == null) return;

                browserLaunched = true;
                OpenInBrowser(url);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[cyan]→ Opening sign-in page in your browser:[/] [link]{Spectre.Console.Markup.Escape(url)}[/]");
                AnsiConsole.MarkupLine("[dim]If your browser didn't open, copy the URL above and paste it manually.[/]");
                AnsiConsole.WriteLine();
            }

            proc.OutputDataReceived += (_, e) => HandleLine(e.Data);
            proc.ErrorDataReceived  += (_, e) => HandleLine(e.Data);

            if (!proc.Start()) return -1;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    // Pulls the first http(s) URL out of a line. Trims trailing punctuation that
    // commonly clings to URLs in CLI output ("...visit https://x.com/abc.").
    private static string? ExtractFirstUrl(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"https?://\S+");
        if (!match.Success) return null;
        return match.Value.TrimEnd('.', ',', ')', ']', '>', '"', '\'');
    }

    /// <summary>
    /// Convenience: start the daemon and poll until it answers (or 5 seconds elapse).
    /// Used by --doctor and any caller that wants the start + readiness check fused.
    /// </summary>
    public static async Task<bool> StartOllamaServeAsync(string url, CancellationToken ct = default)
    {
        if (!TryStartOllamaProcess()) return false;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(url, ct);
            if (probe.Ok) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// OS-appropriate one-liner for installing Ollama. Returns null on unknown platforms.
    /// </summary>
    public static string? GetOsInstallCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "winget install Ollama.Ollama";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return "brew install ollama";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return "curl -fsSL https://ollama.com/install.sh | sh";
        return null;
    }

    /// <summary>
    /// Open a URL in the user's default browser. Swallows failures — a missing browser
    /// shouldn't crash the wizard.
    /// </summary>
    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Fall back: try platform-specific openers that don't rely on shell association.
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start("cmd", $"/c start {url}");
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
            }
            catch { /* nothing more to try */ }
        }
    }

    /// <summary>
    /// Result of a `/api/tags` call. <see cref="Ok"/> distinguishes "user genuinely has
    /// no models" (Ok=true, Models=[]) from "the call itself failed and we don't know"
    /// (Ok=false). Callers can retry on the latter instead of mistakenly routing the
    /// user into the no-models flow.
    /// </summary>
    public sealed record ListModelsResult(bool Ok, List<string> Models, string? Error);

    /// <summary>
    /// Status-aware variant of <see cref="ListModelsAsync"/>. Surfaces transient failures
    /// (network blip, daemon hiccup, timeout, malformed JSON) so the caller can retry
    /// rather than treat them as "user has zero models."
    /// </summary>
    public static async Task<ListModelsResult> ListModelsWithStatusAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync(BuildUrl(url, "api/tags"), ct);
            if (!response.IsSuccessStatusCode)
                return new ListModelsResult(false, new List<string>(), $"HTTP {(int)response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(content);

            var models = new List<string>();
            if (json.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var n = name.GetString();
                        if (!string.IsNullOrEmpty(n)) models.Add(n);
                    }
                }
            }
            return new ListModelsResult(true, models, null);
        }
        catch (Exception ex)
        {
            return new ListModelsResult(false, new List<string>(), ex.Message);
        }
    }

    /// <summary>
    /// Returns the list of model tags reported by `/api/tags`. Empty list on any failure
    /// (this is the "fail-empty" variant — keep using it where transient failures and a
    /// truly empty response should be treated identically; otherwise prefer
    /// <see cref="ListModelsWithStatusAsync"/>).
    /// </summary>
    public static async Task<List<string>> ListModelsAsync(string url, CancellationToken ct = default)
    {
        var result = await ListModelsWithStatusAsync(url, ct);
        return result.Models;
    }

    /// <summary>
    /// Heuristic for cloud sign-in. Ollama has no public whoami endpoint, so we infer
    /// from `/api/tags`:
    /// - Daemon unreachable → <see cref="CloudAuthState.NotReachable"/>.
    /// - At least one ":cloud" tag visible → <see cref="CloudAuthState.SignedIn"/>
    ///   (definitive — only signed-in users can pull cloud models).
    /// - No ":cloud" tags → <see cref="CloudAuthState.Unknown"/> (ambiguous: could be
    ///   signed out, OR signed in without any cloud pulls yet). Callers should ask
    ///   the user rather than assume.
    /// </summary>
    public static async Task<CloudAuthState> CheckCloudSignInAsync(string url, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(url, ct);
        if (!probe.Ok) return CloudAuthState.NotReachable;

        var models = await ListModelsAsync(probe.NormalizedUrl, ct);
        return models.Any(m => m.Contains(":cloud", StringComparison.OrdinalIgnoreCase))
            ? CloudAuthState.SignedIn
            : CloudAuthState.Unknown;
    }

    /// <summary>
    /// Run `ollama pull &lt;tag&gt;`, streaming stdout/stderr lines into the provided progress
    /// callback (one line per call). Returns true on exit code 0.
    /// </summary>
    public static async Task<bool> AutoPullAsync(string modelTag, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelTag)) return false;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(ResolveOllamaExecutable(), $"pull {modelTag}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };

            if (!proc.Start()) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
