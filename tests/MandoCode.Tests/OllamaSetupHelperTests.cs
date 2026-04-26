using System.Net;
using System.Text;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class OllamaSetupHelperTests
{
    [Theory]
    [InlineData("http://localhost:11434",  "/api/tags",      "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434",  "api/tags",       "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/", "/api/tags",      "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/", "api/tags",       "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434//","//api/tags",     "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434",  "",               "http://localhost:11434/")]
    public void BuildUrl_NormalizesSlashes(string baseUrl, string path, string expected)
    {
        Assert.Equal(expected, OllamaSetupHelper.BuildUrl(baseUrl, path));
    }

    [Fact]
    public async Task ProbeAsync_HappyPath_ReturnsOkWithoutHealing()
    {
        using var server = new StubOllamaServer(handler: _ => (HttpStatusCode.OK, "{\"models\":[]}"));
        var result = await OllamaSetupHelper.ProbeAsync(server.BaseUrl);

        Assert.True(result.Ok);
        Assert.False(result.WasHealed);
        Assert.Equal(server.BaseUrl, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_TrailingSlashOnTolerantServer_PreservesUserInput()
    {
        // If the as-typed URL works (server tolerates "//api/tags"), we don't heal —
        // the user's typed value is preserved verbatim.
        using var server = new StubOllamaServer(handler: _ => (HttpStatusCode.OK, "{\"models\":[]}"));
        var withSlash = server.BaseUrl + "/";

        var result = await OllamaSetupHelper.ProbeAsync(withSlash);

        Assert.True(result.Ok);
        Assert.False(result.WasHealed);
        Assert.Equal(withSlash, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_AsTypedFailsTrimmedSucceeds_HealsToTrimmedUrl()
    {
        // Server 404s anything containing "//" (the real Ollama failure mode). The
        // probe's first attempt fails, the trimmed retry succeeds, and the result
        // signals WasHealed so the caller persists the cleaned value.
        using var server = new StubOllamaServer(handler: req =>
            (req.RawUrl ?? "").Contains("//")
                ? (HttpStatusCode.NotFound, "")
                : (HttpStatusCode.OK, "{\"models\":[]}"));
        var withSlash = server.BaseUrl + "/";

        var result = await OllamaSetupHelper.ProbeAsync(withSlash);

        Assert.True(result.Ok);
        Assert.True(result.WasHealed);
        Assert.Equal(server.BaseUrl, result.NormalizedUrl);
        Assert.False(result.NormalizedUrl.EndsWith("/"));
    }

    [Fact]
    public async Task ProbeAsync_BothAttemptsFail_PreservesUserInput()
    {
        // Both as-typed and trimmed fail (e.g., wrong port). NormalizedUrl returns
        // the user's exact input so error messages quote what they actually typed.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var withSlash = $"http://127.0.0.1:{port}/";

        var result = await OllamaSetupHelper.ProbeAsync(withSlash);

        Assert.False(result.Ok);
        Assert.False(result.WasHealed);
        Assert.Equal(withSlash, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_NoDaemon_ReturnsNotOk()
    {
        // Bind a port and immediately release it so the connection refuses cleanly.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await OllamaSetupHelper.ProbeAsync($"http://127.0.0.1:{port}");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_EmptyUrl_ReturnsNotOk()
    {
        var result = await OllamaSetupHelper.ProbeAsync("");
        Assert.False(result.Ok);
    }

    /// <summary>
    /// Tiny in-process HTTP server so we can exercise probe/heal without spinning up
    /// real Ollama. Each request runs through the supplied handler.
    /// </summary>
    private sealed class StubOllamaServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public string BaseUrl { get; }

        public StubOllamaServer(Func<HttpListenerRequest, (HttpStatusCode status, string body)> handler)
        {
            // Pick an ephemeral port. HttpListener requires explicit prefixes — bind to a
            // ranged retry in case 0 isn't supported on this platform.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    BaseUrl = $"http://127.0.0.1:{port}";
                    _ = Task.Run(() => Loop(handler));
                    return;
                }
                catch (HttpListenerException) when (attempt < 9)
                {
                    // Try another port.
                }
            }
            throw new InvalidOperationException("Could not bind a stub HTTP listener");
        }

        private async Task Loop(Func<HttpListenerRequest, (HttpStatusCode, string)> handler)
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try
                {
                    var (status, body) = handler(ctx.Request);
                    ctx.Response.StatusCode = (int)status;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.OutputStream.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
