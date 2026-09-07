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

    [Fact]
    public async Task PlanScreenshotReachesRequestingStepBeforeCompletion_AndNeverLeaksToChat()
    {
        var config = new MandoCodeConfig { ResponseStreaming = "off", EnableAutoContinuation = false };
        var ai = Create(config);
        using var vision = Client("{\"capabilities\":[\"vision\"]}");
        await ai.ValidateModelAsync(vision);
        ai.SetHostTools([AIFunctionFactory.Create(() =>
        {
            Assert.True(ai.TryAttachImage(Png, "image/png", "plan screenshot", out _));
            return "{\"ok\":true,\"imageAttached\":true,\"readyState\":\"complete\"}";
        }, new AIFunctionFactoryOptions { Name = "screenshot_desktop_preview" })]);
        var seen = new List<string>();
        ai.SetChatClientFactoryForTests(body =>
        {
            seen.Add(body);
            if (seen.Count == 1) return ScreenshotCall();
            return Reply(body.Contains(Convert.ToBase64String(Png))
                ? "Image inspected in this step. [PLAN_STEP_RESULT:SUCCESS]" : "Screenshot requested.");
        });

        var result = await ai.ExecutePlanStepAsync("Inspect the preview screenshot", []);
        Assert.Contains("Image inspected in this step", result);
        Assert.Contains(seen, body => body.Contains(Convert.ToBase64String(Png)));
        Assert.DoesNotContain(await ai.GetHistoryAsync(), message => message.Contents.OfType<DataContent>().Any());

        seen.Clear();
        ai.SetChatClientFactoryForTests(body => { seen.Add(body); return Reply("unrelated chat"); });
        await foreach (var _ in ai.ChatStreamAsync("new request")) { }
        Assert.DoesNotContain(seen, body => body.Contains(Convert.ToBase64String(Png)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedPlanDiscardsUndeliveredImages(bool cancel)
    {
        var ai = await VisionServiceAsync(streaming: false);
        using var cancellation = new CancellationTokenSource();
        ai.SetChatClientFactoryForTests(_ =>
        {
            Assert.True(ai.TryAttachImage(Png, "image/png", "interrupted screenshot", out _));
            if (cancel) cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("test failure") };
        });
        await Assert.ThrowsAnyAsync<Exception>(() => ai.ExecutePlanStepAsync("Inspect the preview", [], cancellation.Token));
        var seen = new List<string>();
        ai.SetChatClientFactoryForTests(body => { seen.Add(body); return Reply("new request"); });
        await foreach (var _ in ai.ChatStreamAsync("unrelated request")) { }
        Assert.DoesNotContain(seen, body => body.Contains(Convert.ToBase64String(Png)));
    }

    [Fact]
    public async Task ClearHistoryDiscardsQueuedImageEvidence()
    {
        var ai = await VisionServiceAsync(streaming: false);
        Assert.True(ai.TryAttachImage(Png, "image/png", "old screenshot", out _));
        await ai.ClearHistoryAsync();
        var seen = new List<string>();
        ai.SetChatClientFactoryForTests(body => { seen.Add(body); return Reply("new conversation"); });
        await foreach (var _ in ai.ChatStreamAsync("start over")) { }
        Assert.DoesNotContain(seen, body => body.Contains(Convert.ToBase64String(Png)));
    }

    [Fact]
    public async Task ImageContinuationLimitRefusesAdditionalCapturesInsteadOfDroppingAcceptedImages()
    {
        var ai = await VisionServiceAsync(streaming: false);
        var accepted = 0;
        var refused = 0;
        ai.SetChatClientFactoryForTests(_ =>
        {
            if (ai.TryAttachImage(Png, "image/png", "screenshot", out _)) accepted++;
            else refused++;
            return Reply("Check image.");
        });
        await foreach (var _ in ai.ChatStreamAsync("inspect")) { }
        Assert.Equal(AIService.MaxImageDeliveriesPerTurn, accepted);
        Assert.Equal(1, refused);
    }

    [Fact]
    public void ImageContentIsCountedTowardTheContextEstimate()
    {
        var text = new ChatMessage(ChatRole.User, "a short question");
        var screenshot = new ChatMessage(ChatRole.User, [
            new TextContent("look at this"),
            new DataContent(new byte[32 * 1024], "image/png"),
        ]);

        var textOnly = AIService.EstimateChars([text]);
        var withImage = AIService.EstimateChars([text, screenshot]);

        // Base64 is four characters per three bytes, so a 32 KB capture is ~43,700 characters.
        Assert.InRange(withImage - textOnly, 43_000, 44_000);

        // The point of counting it: one screenshot alone must trip a small local model's window,
        // so the pre-flight compaction fires instead of Ollama silently dropping the system prompt.
        Assert.True(AIService.ExceedsContextBudget(withImage / 4, contextLength: 8192),
            "A screenshot larger than the whole context window did not trip the budget check.");
        Assert.False(AIService.ExceedsContextBudget(textOnly / 4, contextLength: 8192));
    }

    private static HttpResponseMessage ScreenshotCall() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "m", created_at = "2026-01-01T00:00:00Z", done = true, done_reason = "stop",
            message = new { role = "assistant", content = "", tool_calls = new[] {
                new { function = new { name = "screenshot_desktop_preview", arguments = new { } } }
            } }
        }), Encoding.UTF8, "application/json")
    };

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
