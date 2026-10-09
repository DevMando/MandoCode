using System.Text;
using System.Text.Json;
using MandoCode.Models;

namespace MandoCode.Services;

internal sealed record ModelInspection(bool IsValid, string? ErrorMessage, ModelVisionSupport VisionSupport);

/// <summary>Inspect server metadata without mutating model identity, tools, or history.</summary>
internal static class OllamaModelInspector
{
    public static async Task<ModelInspection> InspectAsync(HttpClient client, string endpoint, string modelName)
    {
        try
        {
            using var request = new StringContent(JsonSerializer.Serialize(new { name = modelName }), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(OllamaSetupHelper.BuildUrl(endpoint, "api/show"), request);
            if (!response.IsSuccessStatusCode)
                return new(false, OllamaModelAvailability.ValidationFailure(modelName, response.StatusCode), ModelVisionSupport.Unknown);

            // Missing metadata on an older server does not invalidate a successful response.
            var support = ModelVisionCapabilities.Parse(await response.Content.ReadAsStringAsync());
            return new(true, null, support);
        }
        catch (Exception ex)
        {
            return new(false, $"Could not validate model: {ex.Message}", ModelVisionSupport.Unknown);
        }
    }
}
