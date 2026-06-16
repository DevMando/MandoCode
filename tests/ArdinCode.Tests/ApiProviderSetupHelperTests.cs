using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArdinCode.Services;
using Xunit;

namespace ArdinCode.Tests;

public class ApiProviderSetupHelperTests
{
    [Fact]
    public async Task ProbeAsync_HappyPath_ReturnsOk()
    {
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.OK, "{\"data\":[]}"));
        var result = await ApiProviderSetupHelper.ProbeAsync(server.BaseUrl, "test-api-key");

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(server.BaseUrl, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_TrailingSlash_NormalizesUrl()
    {
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.OK, "{\"data\":[]}"));
        var withSlash = server.BaseUrl + "/";

        var result = await ApiProviderSetupHelper.ProbeAsync(withSlash, "test-api-key");

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(server.BaseUrl, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_Unauthorized_ReturnsNotOkWithError()
    {
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.Unauthorized, ""));
        var result = await ApiProviderSetupHelper.ProbeAsync(server.BaseUrl, "wrong-key");

        Assert.False(result.Ok);
        Assert.Contains("401 Unauthorized", result.Error);
        Assert.Equal(server.BaseUrl, result.NormalizedUrl);
    }

    [Fact]
    public async Task ProbeAsync_OtherHttpStatus_ReturnsOk()
    {
        // Per code implementation: if we get any HTTP status other than 401, server is reachable.
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.InternalServerError, ""));
        var result = await ApiProviderSetupHelper.ProbeAsync(server.BaseUrl, "test-api-key");

        Assert.True(result.Ok);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_EmptyUrl_ReturnsNotOk()
    {
        var result = await ApiProviderSetupHelper.ProbeAsync("", "key");
        Assert.False(result.Ok);
        Assert.Contains("Empty URL", result.Error);
    }

    [Fact]
    public async Task ListModelsAsync_ParsesModelsCorrectly()
    {
        var jsonResponse = """
        {
          "data": [
            { "id": "gpt-4o" },
            { "id": "gpt-4o-mini" }
          ]
        }
        """;
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.OK, jsonResponse));
        var models = await ApiProviderSetupHelper.ListModelsAsync(server.BaseUrl, "key");

        Assert.Equal(2, models.Count);
        Assert.Contains("gpt-4o", models);
        Assert.Contains("gpt-4o-mini", models);
    }

    [Fact]
    public async Task ListModelsAsync_ErrorResponse_ReturnsEmptyList()
    {
        using var server = new StubApiServer(handler: _ => (HttpStatusCode.InternalServerError, ""));
        var models = await ApiProviderSetupHelper.ListModelsAsync(server.BaseUrl, "key");

        Assert.Empty(models);
    }

    private sealed class StubApiServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public string BaseUrl { get; }

        public StubApiServer(Func<HttpListenerRequest, (HttpStatusCode status, string body)> handler)
        {
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
