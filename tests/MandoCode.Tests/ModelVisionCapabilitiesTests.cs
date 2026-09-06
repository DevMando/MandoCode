using System.Net;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ModelVisionCapabilitiesTests
{
    [Theory]
    [InlineData("{\"capabilities\":[\"completion\",\"vision\",\"tools\"]}", ModelVisionSupport.Supported)]
    [InlineData("{\"capabilities\":[\"VISION\"]}", ModelVisionSupport.Supported)]
    [InlineData("{\"capabilities\":[\"completion\",\"tools\"]}", ModelVisionSupport.Unsupported)]
    [InlineData("{}", ModelVisionSupport.Unknown)]
    [InlineData("{\"capabilities\":[]}", ModelVisionSupport.Unknown)]
    [InlineData("{\"capabilities\":null}", ModelVisionSupport.Unknown)]
    [InlineData("{\"capabilities\":\"vision\"}", ModelVisionSupport.Unknown)]
    [InlineData("{\"capabilities\":[\"vision\",42]}", ModelVisionSupport.Unknown)]
    [InlineData("{\"capabilities\":[\"\"]}", ModelVisionSupport.Unknown)]
    [InlineData("not json", ModelVisionSupport.Unknown)]
    [InlineData("[]", ModelVisionSupport.Unknown)]
    public void ReadsExplicitCapabilitiesOnly(string json, ModelVisionSupport expected) =>
        Assert.Equal(expected, ModelVisionCapabilities.Parse(json));

    [Fact]
    public async Task ValidationUpdatesAgentAndPreservesConversation_UnknownDoesNotBlock()
    {
        var config = new MandoCodeConfig();
        var ai = Create(config);
        ai.AppendUserNote("Keep my conversation");
        using var client = Client("{\"capabilities\":[\"vision\"]}");
        Assert.True((await ai.ValidateModelAsync(client)).IsValid);
        Assert.True(ai.SupportsImageInput);
        var history = await ai.GetHistoryAsync();
        Assert.Contains("Vision supported", history[0].Text);
        Assert.Contains(history, message => message.Text == "Keep my conversation");

        using var legacy = Client("not json");
        Assert.True((await ai.ValidateModelAsync(legacy)).IsValid);
        Assert.Equal(ModelVisionSupport.Unknown, ai.VisionSupport);
        Assert.False(ai.SupportsImageInput);
    }

    [Fact]
    public async Task ModelOrEndpointMutationImmediatelyInvalidatesVision()
    {
        var config = new MandoCodeConfig();
        var ai = Create(config);
        using var client = Client("{\"capabilities\":[\"vision\"]}");
        await ai.ValidateModelAsync(client);
        config.OllamaEndpoint = "http://another-server:11434";
        Assert.Equal(ModelVisionSupport.Unknown, ai.VisionSupport);
        await ai.ValidateModelAsync(client);
        Assert.True(ai.SupportsImageInput);
        config.ModelName = "different-model";
        Assert.Equal(ModelVisionSupport.Unknown, ai.VisionSupport);
    }

    [Fact]
    public async Task LateResponseCannotOverwriteNewerInspection()
    {
        var ai = Create(new MandoCodeConfig());
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var slow = new HttpClient(new Handler(_ => pending.Task));
        var oldCheck = ai.ValidateModelAsync(slow);
        using var current = Client("{\"capabilities\":[\"completion\"]}");
        await ai.ValidateModelAsync(current);
        pending.SetResult(Response("{\"capabilities\":[\"vision\"]}"));
        await oldCheck;
        Assert.Equal(ModelVisionSupport.Unsupported, ai.VisionSupport);
    }

    [Fact]
    public async Task LateResponseFromPreviousModelIsIgnored()
    {
        var config = new MandoCodeConfig();
        var ai = Create(config);
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        using var slow = new HttpClient(new Handler(_ => pending.Task));
        var oldCheck = ai.ValidateModelAsync(slow);
        config.ModelName = "replacement-model";
        pending.SetResult(Response("{\"capabilities\":[\"vision\"]}"));
        await oldCheck;
        Assert.Equal(ModelVisionSupport.Unknown, ai.VisionSupport);
        Assert.False(ai.SupportsImageInput);
    }

    [Fact]
    public async Task FailedValidationClearsPreviouslySupportedVision()
    {
        var ai = Create(new MandoCodeConfig());
        using var client = Client("{\"capabilities\":[\"vision\"]}");
        await ai.ValidateModelAsync(client);
        using var failed = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.False((await ai.ValidateModelAsync(failed)).IsValid);
        Assert.Equal(ModelVisionSupport.Unknown, ai.VisionSupport);
    }

    private static AIService Create(MandoCodeConfig config)
    {
        var root = new ProjectRootAccessor(Path.GetTempPath());
        return new AIService(root, config, new TokenTrackingService(), new PlanHandoff(),
            new SkillLoader(config, root), new McpClientManager(config), new McpApprovalGate(config), new SpinnerService());
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpClient Client(string json) => new(new Handler(_ => Task.FromResult(Response(json))));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
