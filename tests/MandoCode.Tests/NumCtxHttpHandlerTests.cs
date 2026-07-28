using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for the per-request num_ctx injection. Observed live: a session on qwen3.5:2b
/// died with an empty response because the tray-app-started daemon ignored MandoCode's
/// OLLAMA_CONTEXT_LENGTH — the in-app context setting was silently a no-op. The handler
/// makes the setting real by stamping options.num_ctx onto every /api/chat request,
/// which outranks both the env var and the daemon default.
/// </summary>
public class NumCtxHttpHandlerTests
{
    /// <summary>Inner handler that captures the outgoing body instead of hitting the network.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static async Task<string?> SendAsync(int numCtx, string path, string body, HttpMethod? method = null)
    {
        var inner = new CapturingHandler();
        using var client = new HttpClient(new NumCtxHttpHandler(() => numCtx, inner));
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, $"http://localhost:11434{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        await client.SendAsync(request);
        return inner.CapturedBody;
    }

    [Fact]
    public async Task Injects_NumCtx_Into_Chat_Request()
    {
        var body = await SendAsync(16384, "/api/chat", """{"model":"qwen3.5:2b","messages":[]}""");

        var json = JsonNode.Parse(body!)!;
        Assert.Equal(16384, (int)json["options"]!["num_ctx"]!);
        Assert.Equal("qwen3.5:2b", (string)json["model"]!);
    }

    [Fact]
    public async Task Preserves_Existing_Options()
    {
        var body = await SendAsync(8192, "/api/chat", """{"model":"m","options":{"temperature":0.7,"num_predict":2048}}""");

        var options = JsonNode.Parse(body!)!["options"]!;
        Assert.Equal(8192, (int)options["num_ctx"]!);
        Assert.Equal(0.7, (double)options["temperature"]!, precision: 5);
        Assert.Equal(2048, (int)options["num_predict"]!);
    }

    [Fact]
    public async Task Overrides_NumCtx_Already_In_Options()
    {
        var body = await SendAsync(16384, "/api/chat", """{"model":"m","options":{"num_ctx":4096}}""");

        Assert.Equal(16384, (int)JsonNode.Parse(body!)!["options"]!["num_ctx"]!);
    }

    [Fact]
    public async Task Zero_NumCtx_Leaves_Request_Untouched()
    {
        // Cloud models resolve to 0 — their context lives server-side, so the body must
        // pass through byte-identical.
        var original = """{"model":"kimi-k2.6:cloud","messages":[]}""";
        var body = await SendAsync(0, "/api/chat", original);

        Assert.Equal(original, body);
    }

    [Fact]
    public async Task NonChat_Endpoints_Pass_Through()
    {
        var original = """{"name":"qwen3.5:2b"}""";
        var body = await SendAsync(16384, "/api/show", original);

        Assert.Equal(original, body);
    }

    [Fact]
    public async Task Malformed_Body_Passes_Through()
    {
        var original = "not json at all";
        var body = await SendAsync(16384, "/api/chat", original);

        Assert.Equal(original, body);
    }

    [Fact]
    public async Task Chat_Path_Under_Prefixed_Endpoint_Is_Matched()
    {
        // Endpoints behind a reverse-proxy path prefix still end in /api/chat.
        var body = await SendAsync(4096, "/ollama/api/chat", """{"model":"m"}""");

        Assert.Equal(4096, (int)JsonNode.Parse(body!)!["options"]!["num_ctx"]!);
    }
}

/// <summary>
/// Tests for the pre-flight context gate. Local Ollama silently truncates oversized
/// prompts instead of rejecting them, so the reactive overflow recovery never fires —
/// the gate must trip BEFORE the send, and early enough that a thinking model still
/// has generation headroom inside the window.
/// </summary>
public class ContextBudgetTests
{
    [Theory]
    [InlineData(1000, 8192, false)]  // comfortable fit
    [InlineData(7200, 8192, true)]   // inside the reserve band (reserve = 1024)
    [InlineData(7168, 8192, false)]  // exactly at the boundary — not over
    [InlineData(9000, 8192, true)]   // outright overflow
    [InlineData(13500, 16384, false)] // 16k window, reserve 2048 → boundary 14336
    [InlineData(14500, 16384, true)]
    [InlineData(5000, 0, false)]     // unknown window — never trip
    [InlineData(3700, 4096, true)]   // small window, reserve clamps to 512 → boundary 3584
    public void ExceedsContextBudget_Trips_Inside_Reserve(long promptTokens, int contextLength, bool expected)
    {
        Assert.Equal(expected, AIService.ExceedsContextBudget(promptTokens, contextLength));
    }
}
