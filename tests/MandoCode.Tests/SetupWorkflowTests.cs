using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public sealed class SetupWorkflowTests
{
    private static MandoCodeConfig Config() => new() { AllowPersistence = false, ModelName = "existing:local", ContextLength = 8192 };
    private static SetupWorkflow Flow(FakeOperations operations, Queue<string> answers, Func<Task<string?>>? browse = null) => new(operations,
        (_, _, options) => { var answer = answers.Dequeue(); Assert.Contains(answer, options); return Task.FromResult<string?>(answer); },
        _ => Task.FromResult<string?>("http://remote:11434"), browse ?? (() => Task.FromResult<string?>(null)), (_, _) => { });

    [Fact]
    public async Task FirstCloudDownloadSignsInWithoutRequiringExistingCloudModels()
    {
        using var operations = new FakeOperations { UnauthorizedPulls = 1 };
        var config = Config();
        var result = await Flow(operations, new(["Cloud — recommended flash models", "glm-5.3-flash:cloud", "Sign in", "Start chatting"])).Run(config, default);
        Assert.False(result.Skipped);
        Assert.True(config.HasCompletedOnboarding);
        Assert.Equal("glm-5.3-flash:cloud", config.ModelName);
        Assert.Equal(2, operations.Pulls.Count);
        Assert.Equal(1, operations.SignIns);
        Assert.Equal(0, operations.ModelListCalls); // Auth never depends on cloud tags being present.
    }

    [Fact]
    public async Task FailedVerificationDoesNotChangeSavedModelOrCompleteSetup()
    {
        using var operations = new FakeOperations { Valid = false };
        var config = Config();
        var result = await Flow(operations, new(["Local — starter models", "qwen3:4b (~2.6 GB, smaller starter)", "Finish later"])).Run(config, default);
        Assert.True(result.Skipped);
        Assert.Null(result.FinalModel);
        Assert.False(config.HasCompletedOnboarding);
        Assert.Equal("existing:local", config.ModelName);
        Assert.Equal(8192, config.ContextLength);
    }

    [Fact]
    public async Task RemoteDownloadUsesConfiguredEndpointWithoutInstallingLocalCli()
    {
        using var operations = new FakeOperations();
        var config = Config(); config.OllamaEndpoint = "http://remote:12345";
        await Flow(operations, new(["Cloud — recommended flash models", "deepseek-v4.1-flash:cloud", "Start chatting"])).Run(config, default);
        Assert.Equal(("http://remote:12345", "deepseek-v4.1-flash:cloud"), Assert.Single(operations.Pulls));
        Assert.Equal(0, operations.CliChecks);
    }

    [Fact]
    public async Task BrowserDownloadIsSelectedAndVerifiedWithoutDownloadingAgain()
    {
        using var operations = new FakeOperations();
        var config = Config();
        var result = await Flow(operations, new(["Browse all models", "Start chatting"]), () => Task.FromResult<string?>("chosen:cloud")).Run(config, default);
        Assert.Empty(operations.Pulls);
        Assert.Equal("chosen:cloud", result.FinalModel);
        Assert.True(config.HasCompletedOnboarding);
    }

    [Fact]
    public async Task CancellationDuringDownloadPausesWithoutChangingDefaults()
    {
        using var stop = new CancellationTokenSource();
        using var operations = new FakeOperations { OnPull = () => stop.Cancel() };
        var config = Config();
        var result = await Flow(operations, new(["Local — starter models", "qwen3:4b (~2.6 GB, smaller starter)"])).Run(config, stop.Token);
        Assert.True(result.Skipped);
        Assert.False(config.HasCompletedOnboarding);
        Assert.Equal("existing:local", config.ModelName);
    }

    [Fact]
    public async Task AuthenticationFailureAfterSignInIsVerifiedAgain()
    {
        using var operations = new FakeOperations { UnauthorizedTests = 1 };
        var config = Config();
        await Flow(operations, new(["Cloud — recommended flash models", "glm-5.3-flash:cloud", "Sign in", "Start chatting"])).Run(config, default);
        Assert.Equal(2, operations.Tests);
        Assert.Equal(1, operations.SignIns);
        Assert.True(config.HasCompletedOnboarding);
    }

    private sealed class FakeOperations : SetupOperations
    {
        public bool Valid = true;
        public int UnauthorizedPulls, UnauthorizedTests, SignIns, ModelListCalls, CliChecks, Tests;
        public Action? OnPull;
        public List<(string Endpoint, string Model)> Pulls = [];
        public override bool CliInstalled() { CliChecks++; return false; }
        public override Task<OllamaSetupHelper.ProbeResult> Probe(string endpoint, CancellationToken ct) => Task.FromResult(new OllamaSetupHelper.ProbeResult(true, endpoint, false, null));
        public override Task<OllamaSetupHelper.ListModelsResult> Models(string endpoint, CancellationToken ct) { ModelListCalls++; return Task.FromResult(new OllamaSetupHelper.ListModelsResult(true, [], null)); }
        public override Task Pull(string endpoint, string model, Action<OllamaModelLibrary.Progress> progress, CancellationToken ct)
        {
            Pulls.Add((endpoint, model)); OnPull?.Invoke(); ct.ThrowIfCancellationRequested();
            if (UnauthorizedPulls-- > 0) throw new HttpRequestException("401 Unauthorized");
            return Task.CompletedTask;
        }
        public override Task<int> SignIn(Action<string> line, CancellationToken ct) { SignIns++; return Task.FromResult(0); }
        public override Task<bool> Validate(string endpoint, string model, CancellationToken ct) => Task.FromResult(Valid);
        public override Task<OllamaSetupHelper.AuthTestResult> Test(string endpoint, string model, CancellationToken ct)
        {
            Tests++; var unauthorized = UnauthorizedTests-- > 0;
            return Task.FromResult(new OllamaSetupHelper.AuthTestResult(!unauthorized, unauthorized, unauthorized ? "401 Unauthorized" : null));
        }
    }
}
