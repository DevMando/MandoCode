using System.Net;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

public static class OllamaModelAvailability
{
    private static readonly Regex GoneStatus = new(@"\b410\s*(?:\(\s*Gone\s*\)|Gone\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsUnavailable(Exception? error)
    {
        while (error is not null)
        {
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Gone }) return true;
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsUnavailable)) return true;
            // Some provider wrappers retain only EnsureSuccessStatusCode's message.
            if (error is not HttpRequestException { StatusCode: not null } && GoneStatus.IsMatch(error.Message)) return true;
            error = error.InnerException;
        }
        return false;
    }

    public static async Task<string?> CheckCandidateAsync(HttpClient client, string endpoint, string model)
    {
        using var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { name = model }), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(MandoCode.Services.OllamaSetupHelper.BuildUrl(endpoint, "api/show"), body);
        return response.IsSuccessStatusCode ? null : ValidationFailure(model, response.StatusCode);
    }

    public static string ValidationFailure(string model, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Gone => Message(model),
        HttpStatusCode.NotFound when MandoCode.Models.MandoCodeConfig.IsCloudModel(model) =>
            $"Cloud model '{model}' is not available on Ollama (HTTP 404 Not Found). Run /model to select an available model.",
        HttpStatusCode.NotFound => $"Model '{model}' not found. Run: ollama pull {model}, or select another model with /model.",
        _ => $"Could not validate model '{model}': Ollama returned HTTP {(int)status} ({status}). Try /retry or check the Ollama endpoint."
    };

    public static bool OfferStartupPicker(string model, string? error) =>
        MandoCode.Models.MandoCodeConfig.IsCloudModel(model) && error is not null &&
        (error.Contains("HTTP 404", StringComparison.Ordinal) || error.Contains("HTTP 410", StringComparison.Ordinal));

    public static string Message(string model) =>
        $"Error: The model '{model}' is no longer available on Ollama (HTTP 410 Gone). It may have been retired.\n\n" +
        "Run /model to select an available model, then send your request again.";
}