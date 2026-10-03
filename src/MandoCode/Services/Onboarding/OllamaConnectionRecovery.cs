namespace MandoCode.Services;

/// <summary>Bounded daemon recovery shared by all CLI panes; never replays agent actions.</summary>
public sealed class OllamaConnectionRecovery
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, CancellationToken, Task<OllamaSetupHelper.ProbeResult>> _probe;
    private readonly Func<string, int, bool> _start;
    private readonly Func<CancellationToken, Task> _delay;
    public OllamaConnectionRecovery() : this(OllamaSetupHelper.ProbeAsync,
        (url, context) => OllamaSetupHelper.TryStartOllamaProcess(context, url),
        ct => Task.Delay(TimeSpan.FromSeconds(2), ct)) { }
    internal OllamaConnectionRecovery(Func<string, CancellationToken, Task<OllamaSetupHelper.ProbeResult>> probe,
        Func<string, int, bool> start, Func<CancellationToken, Task> delay)
        => (_probe, _start, _delay) = (probe, start, delay);

    public async Task<OllamaSetupHelper.ProbeResult> RecoverAsync(string url, int contextLength,
        Action<string> progress, CancellationToken ct = default)
    {
        progress("Checking Ollama connection");
        await _gate.WaitAsync(ct);
        try
        {
            var probe = await _probe(url, ct);
            ct.ThrowIfCancellationRequested();
            if (probe.Ok) return probe;
            if (probe.Error?.StartsWith("HTTP ", StringComparison.Ordinal) == true)
                return probe with { Error = $"Ollama responded with {probe.Error}. Check server logs or authentication; the server is already running." };
            var local = Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == "http" && endpoint.IsLoopback;
            var started = false;
            if (local)
            {
                progress("Starting ollama serve");
                started = _start(url, contextLength);
            }
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                progress($"Reconnecting to Ollama ({attempt}/2)");
                await _delay(ct);
                probe = await _probe(url, ct);
                if (probe.Ok) return probe;
            }
            return probe with { Error = local
                ? started ? "Ollama was started but the configured endpoint is still unreachable. Check the Ollama server logs."
                          : "Couldn't launch Ollama. Check that Ollama is installed and its executable is available."
                : "This endpoint needs Ollama running on its configured server. Check that server and the URL in /setup." };
        }
        finally { _gate.Release(); }
    }
}
