using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MandoCode.Services;

/// <summary>Discovers public library entries and pulls through the user's configured Ollama server.</summary>
public sealed class OllamaModelLibrary(HttpClient http)
{
    public sealed record Model(string Name, string Description, string Capabilities);
    public sealed record Tag(string Name, string Size);
    public sealed record Progress(string Status, long Completed, long Total);
    private static string Plain(string value) => Regex.Replace(HtmlEntity.DeEntitize(value), @"\s+", " ").Trim();
    public static IReadOnlyList<Model> ParseModels(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        return (doc.DocumentNode.SelectNodes("//a[starts-with(@href, '/library/')]") ?? new HtmlNodeCollection(doc.DocumentNode))
            .Where(n => n.SelectSingleNode(".//h2") is not null)
            .Select(n => new Model(n.GetAttributeValue("href", "")[9..], Plain(n.SelectSingleNode(".//p")?.InnerText ?? ""),
                string.Join(" · ", n.SelectNodes(".//span")?.Select(s => Plain(s.InnerText)).Where(s => s is "tools" or "vision" or "thinking" or "embedding" or "cloud") ?? [])))
            .Where(m => Regex.IsMatch(m.Name, @"^[a-zA-Z0-9._-]+$"))
            .DistinctBy(m => m.Name).ToArray();
    }
    public static IReadOnlyList<Tag> ParseTags(string html, string model)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        return (doc.DocumentNode.SelectNodes("//a[starts-with(@href, '/library/')]") ?? new HtmlNodeCollection(doc.DocumentNode))
            .Where(n => n.GetAttributeValue("href", "").StartsWith("/library/" + model + ":", StringComparison.Ordinal))
            .Select(n => new Tag(n.GetAttributeValue("href", "")[9..], Regex.Match(Plain(n.InnerText), @"\b\d+(?:\.\d+)?\s*[KMGT]B\b").Value))
            .GroupBy(t => t.Name).Select(g => g.OrderByDescending(t => t.Size.Length).First()).ToArray();
    }
    public async Task<IReadOnlyList<Model>> Models(CancellationToken ct, bool newest = false) => ParseModels(await Catalog("https://ollama.com/library" + (newest ? "?sort=newest" : ""), ct));
    public async Task<IReadOnlyList<Tag>> Tags(string model, CancellationToken ct) => ParseTags(await Catalog("https://ollama.com/library/" + Uri.EscapeDataString(model) + "/tags", ct), model);
    private async Task<string> Catalog(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await http.GetStringAsync(url, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new HttpRequestException("The Ollama library timed out. Refresh, or type a model name and press Ctrl+P."); }
    }
    public async Task Pull(string endpoint, string model, Action<Progress> progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var success = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line); var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            var status = root.TryGetProperty("status", out var state) ? state.GetString() ?? "Downloading" : "Downloading";
            progress(new(status, root.TryGetProperty("completed", out var done) ? done.GetInt64() : 0, root.TryGetProperty("total", out var total) ? total.GetInt64() : 0));
            success |= status == "success";
        }
        if (!success) throw new IOException("The download ended before Ollama confirmed success. Select the model again to retry.");
    }
}
