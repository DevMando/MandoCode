using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArdinCode.Services;

/// <summary>
/// Probes and checks status of OpenAI-compatible API providers.
/// </summary>
public static class ApiProviderSetupHelper
{
    public sealed record ProbeResult(bool Ok, string NormalizedUrl, string? Error);

    /// <summary>
    /// Probe the API provider at the exact URL the user provided.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string url, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ProbeResult(false, url ?? "", "Empty URL");

        try
        {
            var normalizedUrl = url.TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            var requestUrl = normalizedUrl + "/models";
            using var response = await client.GetAsync(requestUrl, ct);

            if (response.IsSuccessStatusCode)
                return new ProbeResult(true, normalizedUrl, null);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ProbeResult(false, normalizedUrl, "401 Unauthorized (Invalid API Key)");

            // If we get any HTTP status other than 401 or network exceptions, the server is reachable.
            return new ProbeResult(true, normalizedUrl, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, url, ex.Message);
        }
    }

    /// <summary>
    /// Fetch the list of model IDs from the API provider.
    /// </summary>
    public static async Task<List<string>> ListModelsAsync(string endpoint, string? apiKey, CancellationToken ct = default)
    {
        var models = new List<string>();
        try
        {
            var normalizedUrl = (endpoint ?? "").TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            var requestUrl = normalizedUrl + "/models";
            using var response = await client.GetAsync(requestUrl, ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modelObj in dataProp.EnumerateArray())
                    {
                        if (modelObj.TryGetProperty("id", out var idProp))
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                models.Add(id);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore list errors and return empty list
        }
        return models;
    }
}
