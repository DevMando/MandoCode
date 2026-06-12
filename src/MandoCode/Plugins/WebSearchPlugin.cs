using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.SemanticKernel;

namespace MandoCode.Plugins;

/// <summary>
/// Provides web search and page fetching capabilities for the AI assistant.
/// Search prefers Tavily (an LLM-optimized search API) when an API key is configured,
/// with DuckDuckGo HTML search as the zero-config fallback — DuckDuckGo's free endpoint
/// rate-limits and temporarily blocks IPs under normal agentic use, which is exactly
/// why the Tavily path exists. Page fetching uses HttpClient + HtmlAgilityPack.
/// </summary>
public class WebSearchPlugin
{
    private static readonly HttpClient _httpClient;

    private const string TavilySearchEndpoint = "https://api.tavily.com/search";

    /// <summary>
    /// One canonical onboarding hint, returned to the MODEL as part of tool results so
    /// the assistant explains the fix in context at the exact moment search fails —
    /// that moment is when the user actually cares why a key matters.
    /// </summary>
    private const string TavilySignupHint =
        "get a free API key at https://app.tavily.com (free tier ~1,000 searches/month), " +
        "then run: /config set tavilyKey <key>. The key is stored locally in ~/.mandocode/config.json " +
        "and is only ever sent to Tavily.";

    private readonly string? _tavilyApiKey;

    static WebSearchPlugin()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <param name="tavilyApiKey">
    /// Effective Tavily key (config or TAVILY_API_KEY env — resolve via
    /// MandoCodeConfig.GetEffectiveTavilyApiKey before constructing). Null/blank = DuckDuckGo only.
    /// </param>
    public WebSearchPlugin(string? tavilyApiKey = null)
    {
        _tavilyApiKey = string.IsNullOrWhiteSpace(tavilyApiKey) ? null : tavilyApiKey.Trim();
    }

    /// <summary>
    /// Searches the web — Tavily when a key is configured, DuckDuckGo otherwise.
    /// </summary>
    [KernelFunction("search_web")]
    [Description("Searches the web for information. Returns titles, URLs, and snippets. Use this to find current information, documentation, tutorials, or answers to questions.")]
    public async Task<string> SearchWeb(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return (1-10, default 5)")] int maxResults = 5)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);

        if (_tavilyApiKey != null)
        {
            var (ok, tavilyResult) = await SearchTavilyAsync(query, maxResults);
            if (ok) return tavilyResult;

            // Tavily down/misconfigured shouldn't kill search entirely — degrade to
            // DuckDuckGo and surface the Tavily failure so the model can relay it.
            var fallback = await SearchDuckDuckGoAsync(query, maxResults, tavilyConfigured: true);
            return $"[{tavilyResult} — fell back to DuckDuckGo]\n\n{fallback}";
        }

        return await SearchDuckDuckGoAsync(query, maxResults, tavilyConfigured: false);
    }

    /// <summary>
    /// Searches via Tavily's REST API. Returns (false, reason) on failure so the caller
    /// can fall back to DuckDuckGo; (true, formattedResults) on success — including the
    /// "no results" case, which is an answer, not a failure.
    /// </summary>
    private async Task<(bool Ok, string Result)> SearchTavilyAsync(string query, int maxResults)
    {
        try
        {
            using var request = BuildTavilyRequest(_tavilyApiKey!, query, maxResults, includeAnswer: true);
            using var response = await _httpClient.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"Tavily rejected the API key (HTTP {(int)response.StatusCode}) — the user should check it at https://app.tavily.com and re-run /config set tavilyKey <key>");
            // 432/433 are Tavily-specific plan/pay-as-you-go limit codes; 429 is plain rate limiting.
            if ((int)response.StatusCode is 429 or 432 or 433)
                return (false, $"Tavily usage limit reached (HTTP {(int)response.StatusCode}) — the user can check their plan at https://app.tavily.com");
            if (!response.IsSuccessStatusCode)
                return (false, $"Tavily search failed (HTTP {(int)response.StatusCode})");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new StringBuilder();
            sb.AppendLine($"Search results for \"{query}\" (via Tavily):");
            sb.AppendLine();

            var hasAnswer = false;
            if (root.TryGetProperty("answer", out var answerEl) &&
                answerEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(answerEl.GetString()))
            {
                sb.AppendLine($"Answer summary: {answerEl.GetString()!.Trim()}");
                sb.AppendLine();
                hasAnswer = true;
            }

            var count = 0;
            if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsEl.EnumerateArray())
                {
                    if (count >= maxResults) break;

                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;

                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;

                    count++;
                    sb.AppendLine($"{count}. {title.Trim()}");
                    sb.AppendLine($"   URL: {url.Trim()}");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var snippet = CollapseWhitespace(content);
                        if (snippet.Length > 500) snippet = snippet[..500] + "…";
                        sb.AppendLine($"   {snippet}");
                    }
                    sb.AppendLine();
                }
            }

            if (count == 0 && !hasAnswer)
                return (true, $"No results found for: {query}");

            return (true, sb.ToString().TrimEnd());
        }
        catch (TaskCanceledException)
        {
            return (false, "Tavily search timed out");
        }
        catch (Exception ex)
        {
            return (false, $"Tavily search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches via DuckDuckGo's free HTML endpoint. When DuckDuckGo blocks the request
    /// (rate limit / IP block / bot challenge), the returned error teaches the model —
    /// and through it the user — about the Tavily option instead of failing opaquely.
    /// </summary>
    private static async Task<string> SearchDuckDuckGoAsync(string query, int maxResults, bool tavilyConfigured)
    {
        try
        {
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
                new KeyValuePair<string, string>("b", ""),
                new KeyValuePair<string, string>("kl", "us-en")
            });

            var response = await _httpClient.PostAsync("https://html.duckduckgo.com/html/", formData);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 403 or 429 or 503)
                    return BuildDuckDuckGoBlockedMessage($"HTTP {(int)response.StatusCode}", tavilyConfigured);
                return $"Error: DuckDuckGo search failed (HTTP {(int)response.StatusCode})";
            }

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]");
            if (resultNodes == null || resultNodes.Count == 0)
            {
                // A 200 with zero results is either genuinely empty or DuckDuckGo's
                // bot-detection challenge page; the challenge markup names its scripts.
                if (html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                    return BuildDuckDuckGoBlockedMessage("bot-detection challenge page", tavilyConfigured);
                return $"No results found for: {query}";
            }

            var results = new List<string>();
            var count = 0;

            foreach (var node in resultNodes)
            {
                if (count >= maxResults) break;

                var linkNode = node.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                var snippetNode = node.SelectSingleNode(".//*[contains(@class, 'result__snippet')]");

                if (linkNode == null) continue;

                var title = WebUtility.HtmlDecode(linkNode.InnerText.Trim());
                var rawUrl = linkNode.GetAttributeValue("href", "");
                var snippet = snippetNode != null
                    ? WebUtility.HtmlDecode(snippetNode.InnerText.Trim())
                    : "";

                // DuckDuckGo wraps URLs in a redirect: //duckduckgo.com/l/?uddg=<encoded_url>&...
                var url = ExtractDuckDuckGoUrl(rawUrl);

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) continue;

                count++;
                results.Add($"{count}. {title}");
                results.Add($"   URL: {url}");
                if (!string.IsNullOrEmpty(snippet))
                {
                    results.Add($"   {snippet}");
                }
                results.Add("");
            }

            if (results.Count == 0)
            {
                return $"No results found for: {query}";
            }

            return $"Search results for \"{query}\":\n\n{string.Join("\n", results).TrimEnd()}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Search request timed out for query: {query}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Failed to search the web: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error searching the web: {ex.Message}";
        }
    }

    private static string BuildDuckDuckGoBlockedMessage(string reason, bool tavilyConfigured)
    {
        var msg = $"Error: DuckDuckGo blocked this search ({reason}). Its free endpoint rate-limits " +
                  "and temporarily blocks IPs, so this happens under normal use.";
        if (tavilyConfigured)
            return msg + " Tavily was tried first and also failed (reason above) — tell the user both " +
                   "search providers are currently unavailable; retrying in a few minutes may work.";
        return msg + $"\nTell the user: for reliable web search, MandoCode supports Tavily — a search API " +
               $"built for AI agents — {TavilySignupHint}";
    }

    /// <summary>
    /// Fires one minimal Tavily search to verify a key the moment it's set — instant
    /// feedback at configuration time beats a silent failure on the first real search
    /// days later. Returns a user-facing message; never throws. The key is saved by the
    /// caller regardless (the user may simply be offline), so failure wording says so.
    /// </summary>
    public static async Task<string> ValidateTavilyKeyAsync(string apiKey)
    {
        try
        {
            using var request = BuildTavilyRequest(apiKey, "MandoCode API key verification", maxResults: 1, includeAnswer: false);
            using var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
                return "✓ Tavily key verified — web search now prefers Tavily (DuckDuckGo stays as fallback)";
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return $"✗ Tavily rejected the key (HTTP {(int)response.StatusCode}). It was saved anyway — double-check it at https://app.tavily.com and re-run /config set tavilyKey <key>";
            if ((int)response.StatusCode is 429 or 432 or 433)
                return $"⚠ Key saved, but Tavily reports a usage limit (HTTP {(int)response.StatusCode}) — check your plan at https://app.tavily.com";
            return $"⚠ Key saved, but verification was inconclusive (Tavily returned HTTP {(int)response.StatusCode}) — the first real search will tell";
        }
        catch (Exception ex)
        {
            return $"⚠ Key saved, but Tavily couldn't be reached to verify it ({ex.Message}) — verification will happen on the first search";
        }
    }

    private static HttpRequestMessage BuildTavilyRequest(string apiKey, string query, int maxResults, bool includeAnswer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TavilySearchEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                query,
                max_results = maxResults,
                search_depth = "basic",
                include_answer = includeAnswer
            }),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    /// <summary>
    /// Fetches a webpage and extracts its text content.
    /// </summary>
    [KernelFunction("fetch_webpage")]
    [Description("Fetches a webpage URL and extracts its readable text content. Use this to read documentation pages, articles, blog posts, or any web page the user wants to learn about.")]
    public async Task<string> FetchWebpage(
        [Description("The URL of the webpage to fetch")] string url,
        [Description("Maximum characters of content to return (500-15000, default 5000)")] int maxCharacters = 5000)
    {
        maxCharacters = Math.Clamp(maxCharacters, 500, 15000);

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return $"Error: Invalid URL: {url}. Must be an http or https URL.";
            }

            var response = await _httpClient.GetAsync(uri);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Extract the page title
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            var title = titleNode != null
                ? WebUtility.HtmlDecode(titleNode.InnerText.Trim())
                : "";

            // Remove non-content elements
            var tagsToRemove = new[] { "script", "style", "nav", "footer", "header", "aside", "iframe", "form", "noscript", "svg" };
            foreach (var tag in tagsToRemove)
            {
                var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        node.Remove();
                    }
                }
            }

            // Extract text from body
            var bodyNode = doc.DocumentNode.SelectSingleNode("//body");
            var rawText = bodyNode != null
                ? WebUtility.HtmlDecode(bodyNode.InnerText)
                : WebUtility.HtmlDecode(doc.DocumentNode.InnerText);

            // Collapse whitespace: normalize line endings, collapse blank lines, trim
            var cleanText = CollapseWhitespace(rawText);

            if (string.IsNullOrWhiteSpace(cleanText))
            {
                return $"Error: No readable text content found at: {url}";
            }

            // Truncate to maxCharacters
            if (cleanText.Length > maxCharacters)
            {
                cleanText = cleanText[..maxCharacters] + "\n\n[Content truncated at " + maxCharacters + " characters]";
            }

            var header = !string.IsNullOrEmpty(title)
                ? $"Page: {title}\nURL: {url}\n\n"
                : $"URL: {url}\n\n";

            return header + cleanText;
        }
        catch (TaskCanceledException)
        {
            return $"Error: Request timed out fetching: {url}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Failed to fetch webpage: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error fetching webpage: {ex.Message}";
        }
    }

    /// <summary>
    /// Extracts the actual URL from DuckDuckGo's redirect wrapper.
    /// DuckDuckGo wraps results as: //duckduckgo.com/l/?uddg=ENCODED_URL&amp;...
    /// </summary>
    private static string ExtractDuckDuckGoUrl(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return rawUrl;

        // Check for uddg= parameter in the URL
        var match = Regex.Match(rawUrl, @"[?&]uddg=([^&]+)");
        if (match.Success)
        {
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        // If not a redirect, return as-is (clean up protocol-relative URLs)
        if (rawUrl.StartsWith("//"))
        {
            return "https:" + rawUrl;
        }

        return rawUrl;
    }

    // Pre-compiled regex for collapsing inline whitespace
    private static readonly Regex InlineWhitespacePattern = new(@"[ \t]+", RegexOptions.Compiled);

    /// <summary>
    /// Collapses excessive whitespace in extracted text content.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        // Normalize line endings
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // Split into lines, trim each, remove empty runs
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .ToList();

        var result = new List<string>();
        bool lastWasEmpty = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (!lastWasEmpty)
                {
                    result.Add("");
                    lastWasEmpty = true;
                }
            }
            else
            {
                // Collapse inline whitespace (tabs, multiple spaces)
                var collapsed = InlineWhitespacePattern.Replace(line, " ");
                result.Add(collapsed);
                lastWasEmpty = false;
            }
        }

        return string.Join("\n", result).Trim();
    }
}
