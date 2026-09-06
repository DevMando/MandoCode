using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Xunit;

namespace MandoCode.Tests;

// Probe: does OllamaSharp put DataContent images on the wire where Ollama expects them?
public sealed class OllamaImageWireFormatProbe
{
    [Fact]
    public async Task ImageContentReachesTheOllamaImagesArray()
    {
        string? body = null;
        var handler = new Capture(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\"," +
                    "\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        IChatClient client = new OllamaApiClient(http, "llava");

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("What is on screen?"),
            new DataContent(png, "image/png"),
        ]);
        await client.GetResponseAsync([message]);

        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        var first = document.RootElement.GetProperty("messages")[0];
        Assert.True(first.TryGetProperty("images", out var images), "no images array on the wire: " + body);
        Assert.Equal(Convert.ToBase64String(png), images[0].GetString());
    }

    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
