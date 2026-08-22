using System.Text;
using System.Text.Json.Nodes;

namespace MandoCode.Services;

/// <summary>
/// Stamps <c>options.num_ctx</c> onto outgoing Ollama <c>/api/chat</c> requests.
///
/// Ollama resolves the context window with this precedence: per-request
/// <c>options.num_ctx</c> &gt; the daemon's <c>OLLAMA_CONTEXT_LENGTH</c> env var &gt; the
/// daemon default (which the Ollama desktop app's settings slider controls). MandoCode
/// only sets the env var when it launches the daemon itself — when the tray app started
/// it first, the in-app context-length setting was silently a no-op. MandoCode's own
/// OllamaApiClient wiring (see AIService.BuildAgent) has no per-request num_ctx option to
/// set, so this handler patches the request body instead: the configured window wins no
/// matter who started the daemon, takes effect on the next message without a restart, and
/// can differ per agent tab.
///
/// <para><paramref name="getNumCtx"/> is re-read on every request so it tracks live
/// config changes; return 0 to leave the request untouched (cloud models — their
/// context lives server-side at the model's full window — or unset config).</para>
/// </summary>
public sealed class NumCtxHttpHandler : DelegatingHandler
{
    private readonly Func<int> _getNumCtx;

    public NumCtxHttpHandler(Func<int> getNumCtx, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _getNumCtx = getNumCtx;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var numCtx = _getNumCtx();
        if (numCtx > 0
            && request.Method == HttpMethod.Post
            && request.Content != null
            && (request.RequestUri?.AbsolutePath.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                if (body != null)
                {
                    // A request that already carries options keeps them — only num_ctx is added.
                    if (body["options"] is JsonObject options)
                        options["num_ctx"] = numCtx;
                    else
                        body["options"] = new JsonObject { ["num_ctx"] = numCtx };

                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }
            }
            catch
            {
                // Unparseable body — send the request unmodified rather than fail the call.
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
