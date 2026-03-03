using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.SemanticKernel;

namespace MandoCode.Plugins;

/// <summary>
/// Provides web search and page fetching capabilities for the AI assistant.
/// Uses DuckDuckGo HTML search (no API key required) and HttpClient for fetching pages.
/// </summary>
public class WebSearchPlugin
{
    private static readonly HttpClient _httpClient;

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

    /// <summary>
    /// Searches the web using DuckDuckGo and returns titles, URLs, and snippets.
    /// </summary>
    [KernelFunction("search_web")]
    [Description("Searches the web for information using DuckDuckGo. Returns titles, URLs, and snippets. Use this to find current information, documentation, tutorials, or answers to questions.")]
    public async Task<string> SearchWeb(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return (1-10, default 5)")] int maxResults = 5)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);

        try
        {
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
                new KeyValuePair<string, string>("b", ""),
                new KeyValuePair<string, string>("kl", "us-en")
            });

            var response = await _httpClient.PostAsync("https://html.duckduckgo.com/html/", formData);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]");
            if (resultNodes == null || resultNodes.Count == 0)
            {
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
                var collapsed = Regex.Replace(line, @"[ \t]+", " ");
                result.Add(collapsed);
                lastWasEmpty = false;
            }
        }

        return string.Join("\n", result).Trim();
    }
}
