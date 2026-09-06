using System.Net;
using System.Text;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Xunit;

namespace MandoCode.Tests;

public class ImageInputDeliveryTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    [Fact]
    public async Task TextOnlyAndUnknownModelsRefuseImages()
    {
        var ai = Create(new MandoCodeConfig());
        Assert.False(ai.TryAttachImage(Png, "image/png", "shot", out var unknown));
        Assert.Contains("unknown", unknown);

        using var textOnly = Client("{\"capabilities\":[\"tools\"]}");
        await ai.ValidateModelAsync(textOnly);
        Assert.False(ai.TryAttachImage(Png, "image/png", "shot", out var refused));
        Assert.Contains("text-only", refused);
    }

    [Fact]
    public async Task VisionModelAcceptsBoundedImageContentOnly()
    {
        var ai = await VisionServiceAsync();
        Assert.True(ai.TryAttachImage(Png, "image/png", "screenshot of the page", out _));
        Assert.False(ai.TryAttachImage(ReadOnlyMemory<byte>.Empty, "image/png", "", out var empty));
        Assert.Contains("empty", empty);
        Assert.False(ai.TryAttachImage(new byte[AIService.MaxImageInputBytes + 1], "image/png", "", out var big));
        Assert.Contains("limit", big);
        Assert.False(ai.TryAttachImage(Png, "application/pdf", "", out var wrong));
        Assert.Contains("image content", wrong);
    }

    [Fact]
    public async Task AttachedImagesAreDeliveredThenRetractedFromHistory()
    {
        var ai = await VisionServiceAsync(streaming: false);
        var seen = new List<string>();
        ai.SetChatClientFactoryForTests(body => { seen.Add(body); return Reply("looked at it"); });

        Assert.True(ai.TryAttachImage(Png, "image/png", "preview screenshot", out _));
        await foreach (var _ in ai.ChatStreamAsync("check the page")) { }

        // The model must actually receive the bytes...
        var delivered = seen.Any(body => body.Contains(Convert.ToBase64String(Png)));
        Assert.True(delivered, "the image never reached the model: " + string.Join("\n", seen));

        // ...and the image must not linger in history to be re-uploaded on every later request.
        var history = await ai.GetHistoryAsync();
        Assert.DoesNotContain(history, message => message.Contents.OfType<DataContent>().Any());
    }

    private static async Task<AIService> VisionServiceAsync(bool streaming = true)
    {
        var config = new MandoCodeConfig();
        if (!streaming) config.ResponseStreaming = "off";
        var ai = Create(config);
        using var vision = Client("{\"capabilities\":[\"vision\"]}");
        await ai.ValidateModelAsync(vision);
        Assert.True(ai.SupportsImageInput);
        return ai;
    }

    private static AIService Create(MandoCodeConfig config)
    {
        var root = new ProjectRootAccessor(Path.GetTempPath());
        return new AIService(root, config, new TokenTrackingService(), new PlanHandoff(),
            new SkillLoader(config, root), new McpClientManager(config), new McpApprovalGate(config), new SpinnerService());
    }

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "m",
            created_at = "2026-01-01T00:00:00Z",
            message = new { role = "assistant", content = text },
            done = true,
            done_reason = "stop",
        }), Encoding.UTF8, "application/json"),
    };

    private static HttpClient Client(string json) =>
        new(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
