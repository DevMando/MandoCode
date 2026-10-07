using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class OllamaConnectionRecoveryTests
{
    private const string Local = "http://localhost:11434";
    private static OllamaSetupHelper.ProbeResult Result(bool ok, string url = Local, string? error = null)
        => new(ok, url, false, error);

    [Fact]
    public async Task StartsOnceAndConnectsOnSecondRetryWithProgress()
    {
        var checks = 0; var starts = 0; var delays = 0; var progress = new List<string>();
        var recovery = new OllamaConnectionRecovery((url, ct) => Task.FromResult(Result(++checks == 3, url)),
            (url, context) => { Assert.Equal("http://localhost:12121", url); Assert.Equal(32768, context); starts++; return true; },
            ct => { delays++; return Task.CompletedTask; });
        var result = await recovery.RecoverAsync("http://localhost:12121", 32768, progress.Add);
        Assert.True(result.Ok); Assert.Equal(1, starts); Assert.Equal(3, checks); Assert.Equal(2, delays);
        Assert.Contains("Starting ollama serve", progress);
        Assert.Contains("Reconnecting to Ollama (2/2)", progress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureIsBoundedAndExplainsWhetherLaunchSucceeded(bool launched)
    {
        var checks = 0; var starts = 0;
        var recovery = new OllamaConnectionRecovery((url, ct) => { checks++; return Task.FromResult(Result(false)); },
            (url, context) => { starts++; return launched; }, ct => Task.CompletedTask);
        var result = await recovery.RecoverAsync(Local, 16384, _ => { });
        Assert.False(result.Ok); Assert.Equal(3, checks); Assert.Equal(1, starts);
        Assert.Contains(launched ? "still unreachable" : "Couldn't launch", result.Error);
    }

    [Theory]
    [InlineData("http://example.com:11434")]
    [InlineData("https://localhost:11434")]
    [InlineData("not a URL")]
    public async Task RemoteOrUnsupportedEndpointNeverLaunchesLocalDaemon(string endpoint)
    {
        var checks = 0;
        var recovery = new OllamaConnectionRecovery((url, ct) => { checks++; return Task.FromResult(Result(false, url)); },
            (url, context) => throw new Exception("Must not start"), ct => Task.CompletedTask);
        var result = await recovery.RecoverAsync(endpoint, 0, _ => { });
        Assert.False(result.Ok); Assert.Equal(3, checks); Assert.Contains("configured server", result.Error);
    }

    [Theory]
    [InlineData("HTTP 401")]
    [InlineData("HTTP 500")]
    public async Task HttpResponseDoesNotStartAnAlreadyRunningServer(string error)
    {
        var recovery = new OllamaConnectionRecovery((url, ct) => Task.FromResult(Result(false, url, error)),
            (url, context) => throw new Exception("Must not start"), ct => throw new Exception("Must not retry"));
        var result = await recovery.RecoverAsync(Local, 0, _ => { });
        Assert.Contains(error, result.Error); Assert.Contains("already running", result.Error);
    }

    [Fact]
    public async Task ReachableOrHealedConnectionDoesNotStartDaemon()
    {
        var healed = new OllamaSetupHelper.ProbeResult(true, Local, true, null);
        var recovery = new OllamaConnectionRecovery((url, ct) => Task.FromResult(healed),
            (url, context) => throw new Exception("Must not start"), ct => throw new Exception("Must not retry"));
        Assert.Equal(healed, await recovery.RecoverAsync(Local + "/", 0, _ => { }));
    }

    [Fact]
    public async Task ConcurrentPanesShareTheLaunchAndCancellationReleasesGate()
    {
        var starts = 0; var ready = false;
        var recovery = new OllamaConnectionRecovery((url, ct) => Task.FromResult(Result(ready)),
            (url, context) => { starts++; return true; }, async ct => { await Task.Delay(10, ct); ready = true; });
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => recovery.RecoverAsync(Local, 0, _ => { })));
        Assert.All(results, r => Assert.True(r.Ok)); Assert.Equal(1, starts);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverAsync(Local, 0, _ => { }, cancelled.Token));
        Assert.True((await recovery.RecoverAsync(Local, 0, _ => { })).Ok);
    }
}
