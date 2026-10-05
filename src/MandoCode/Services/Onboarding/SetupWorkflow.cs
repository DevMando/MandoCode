using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>Setup operations are replaceable so the complete workflow can be tested without installations.</summary>
public class SetupOperations : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public virtual Task<OllamaSetupHelper.ProbeResult> Probe(string endpoint, CancellationToken ct) => OllamaSetupHelper.ProbeAsync(endpoint, ct);
    public virtual bool CliInstalled() => OllamaSetupHelper.IsOllamaCliInstalled();
    public virtual Task<int> Install(CancellationToken ct) => OllamaSetupHelper.InstallOllamaAsync(ct);
    public virtual async Task<bool> Start(string endpoint, int context, CancellationToken ct)
    {
        if (!OllamaSetupHelper.TryStartOllamaProcess(context, endpoint)) return false;
        for (var i = 0; i < 12; i++) { ct.ThrowIfCancellationRequested(); if ((await Probe(endpoint, ct)).Ok) return true; await Task.Delay(500, ct); }
        return false;
    }
    public virtual Task<OllamaSetupHelper.ListModelsResult> Models(string endpoint, CancellationToken ct) => OllamaSetupHelper.ListModelsWithStatusAsync(endpoint, ct);
    public virtual Task Pull(string endpoint, string model, Action<OllamaModelLibrary.Progress> progress, CancellationToken ct) => new OllamaModelLibrary(_http).Pull(endpoint, model, progress, ct);
    public virtual Task<int> SignIn(Action<string> line, CancellationToken ct) => OllamaSetupHelper.RunOllamaSigninAsync(new Progress<string>(line), ct);
    public virtual Task<bool> Validate(string endpoint, string model, CancellationToken ct) => OllamaSetupHelper.ValidateModelAsync(endpoint, model, ct);
    public virtual Task<OllamaSetupHelper.AuthTestResult> Test(string endpoint, string model, CancellationToken ct) => OllamaSetupHelper.TestCloudAuthAsync(endpoint, model, ct, timeoutSeconds: 120);
    public void Dispose() => _http.Dispose();
}

/// <summary>Guided setup; saved model/defaults only change after a successful inference check.</summary>
public sealed class SetupWorkflow(SetupOperations operations,
    Func<string, string, string[], Task<string?>> choose,
    Func<string, Task<string?>> askUrl,
    Func<Task<string?>> browse,
    Action<string, string> status)
{
    public static readonly string[] CloudStarters = ["glm-5.3-flash:cloud", "deepseek-v4.1-flash:cloud"];
    public async Task<OnboardingFlow.FlowResult> Run(MandoCodeConfig config, CancellationToken ct)
    {
        var connected = false;
        var endpoint = config.OllamaEndpoint;
        string? model = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                status("Connect", "Checking Ollama at " + endpoint);
                var probe = await operations.Probe(endpoint, ct);
                connected = probe.Ok;
                if (connected) { endpoint = probe.NormalizedUrl; config.OllamaEndpoint = endpoint; break; }
                var local = OllamaSetupHelper.IsLocalUrl(endpoint);
                var actions = new List<string>();
                if (local) actions.Add(operations.CliInstalled() ? "Start Ollama" : "Install Ollama");
                actions.AddRange(["Retry connection", "Use another Ollama URL", "Finish later"]);
                var action = await choose("Connect", $"Couldn't connect to {endpoint}. {probe.Error}", actions.ToArray());
                if (action is null or "Finish later") return Pause();
                if (action == "Use another Ollama URL") { var value = await askUrl(endpoint); if (value is not null) endpoint = value; }
                else if (action == "Start Ollama") { status("Connect", "Starting Ollama"); await operations.Start(endpoint, config.ContextLength, ct); }
                else if (action == "Install Ollama")
                {
                    var command = OllamaSetupHelper.GetOsInstallCommand();
                    if (await choose("Connect", $"Install using {command ?? "the Ollama download page"}. The installer may request permission or your password.", ["Install", "Back"]) != "Install") continue;
                    status("Connect", "Installing Ollama — follow any installer prompts");
                    var code = await operations.Install(ct);
                    if (code != 0 && await choose("Connect", $"Installation did not finish (exit {code}). Install from https://ollama.com/download, then retry.", ["Retry connection", "Finish later"]) is null or "Finish later") return Pause();
                }
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (model is null)
                {
                    var action = await choose("Choose model", "Cloud uses Ollama's servers and needs sign-in and internet. Local runs on your hardware; download size and memory requirements vary.", ["Cloud — recommended flash models", "Local — starter models", "Browse all models", "Use an installed model", "Change connection", "Finish later"]);
                    if (action is null or "Finish later") return Pause();
                    if (action == "Change connection")
                    {
                        var value = await askUrl(endpoint);
                        if (value is not null)
                        {
                            status("Connect", "Checking " + value);
                            var probe = await operations.Probe(value, ct);
                            if (!probe.Ok) { await choose("Connect", "Couldn't connect: " + probe.Error, ["Back"]); continue; }
                            endpoint = probe.NormalizedUrl; config.OllamaEndpoint = endpoint;
                        }
                        continue;
                    }
                    var downloaded = false;
                    if (action == "Browse all models") { model = await browse(); downloaded = model is not null; }
                    else if (action == "Use an installed model")
                    {
                        status("Choose model", "Loading installed models");
                        var list = await operations.Models(endpoint, ct);
                        if (!list.Ok || list.Models.Count == 0) { await choose("Choose model", list.Ok ? "No models installed yet. Choose a starter or browse models." : "Couldn't load models: " + list.Error, ["Back"]); continue; }
                        model = await choose("Choose model", "Select the model to use as your default.", list.Models.Order(StringComparer.OrdinalIgnoreCase).Concat(["Back"]).ToArray());
                        if (model == "Back") model = null;
                        downloaded = true;
                    }
                    else
                    {
                        var cloud = action.StartsWith("Cloud", StringComparison.Ordinal);
                        var options = cloud ? CloudStarters.Concat(["Back"]).ToArray()
                            : new[] { "qwen3:4b (~2.6 GB, smaller starter)", "qwen3:8b (~5.2 GB, more memory)", "Back" };
                        var selected = await choose("Choose model", cloud ? "Flash cloud starters support tool calling and vision. No local GPU required. Ollama account limits and pricing apply." : "Sizes are approximate downloads, not total RAM/VRAM use. Context also uses memory. Browse models for more choices.", options);
                        if (selected is not null && selected != "Back") model = selected.Split(' ')[0];
                    }
                    if (model is null) continue;
                    if (!downloaded)
                    {
                        while (true)
                        {
                            try
                            {
                                status("Download", "Downloading " + model);
                                await operations.Pull(endpoint, model, p => status("Download", p.Status + (p.Total > 0 ? $" · {p.Completed * 100d / p.Total:F0}% · {p.Completed / 1048576d:F1}/{p.Total / 1048576d:F1} MiB" : "")), ct);
                                break;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                if (MandoCodeConfig.IsCloudModel(model) && ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) && await SignIn(ex.Message)) continue;
                                await choose("Download", "Download failed: " + ex.Message + ". Select the same model to resume, or choose another.", ["Back"]);
                                model = null; break;
                            }
                        }
                        if (model is null) continue;
                    }
                }
                status("Verify", "Checking " + model);
                var valid = await operations.Validate(endpoint, model, ct);
                var test = valid ? await operations.Test(endpoint, model, ct) : new OllamaSetupHelper.AuthTestResult(false, false, "Model information is unavailable. Download the model again.");
                ct.ThrowIfCancellationRequested();
                if (test.Unauthorized && MandoCodeConfig.IsCloudModel(model) && await SignIn(test.Error ?? "Sign-in required")) continue;
                if (!test.Ok)
                {
                    var action = await choose("Verify", "The model isn't ready: " + test.Error, ["Retry verification", "Choose another model", "Finish later"]);
                    if (action is null or "Finish later") return Pause();
                    if (action == "Choose another model") model = null;
                    continue;
                }
                var previous = config.GetEffectiveModelName();
                config.ModelName = model; config.ModelPath = null;
                config.ApplyRecommendedContextLength(previous, model);
                config.HasCompletedOnboarding = true; config.OllamaEndpoint = endpoint; config.Save();
                await choose("Ready", $"Ready to chat!\nModel: {model}\nOllama: {endpoint}\nContext: {MandoCodeConfig.ContextLengthLabel(config.ContextLength)}\nThis model is your default for new agents. Use /model to switch, /ollama-pull to download more, or Agent Settings to customize an agent.", ["Start chatting"]);
                return new(true, false, model);
            }
        }
        catch (OperationCanceledException) { return Pause(); }

        OnboardingFlow.FlowResult Pause() { config.Save(); return new(connected, true, null); }
        async Task<bool> SignIn(string error)
        {
            if (!OllamaSetupHelper.IsLocalUrl(endpoint))
            {
                await choose("Verify", "Cloud authentication must be configured on the remote Ollama server. Run ollama signin on that machine, then retry. " + error, ["Back"]);
                return false;
            }
            var action = await choose("Verify", "Cloud models need Ollama sign-in. Your browser will open; a sign-in link will also appear here. " + error, ["Sign in", "I've signed in — retry", "Back"]);
            if (action == "I've signed in — retry") return true;
            if (action != "Sign in") return false;
            status("Verify", "Waiting for Ollama sign-in");
            var code = await operations.SignIn(line => status("Verify", line), ct);
            ct.ThrowIfCancellationRequested();
            if (code == 0) return true; // No model-list heuristic: retry pull/inference to prove readiness.
            await choose("Verify", "Sign-in did not finish. Update Ollama if the signin command is unavailable, or run ollama signin manually.", ["Back"]);
            return false;
        }
    }
}
