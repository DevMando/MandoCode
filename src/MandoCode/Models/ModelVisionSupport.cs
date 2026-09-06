using System.Text.Json;

namespace MandoCode.Models;

public enum ModelVisionSupport { Unknown, Unsupported, Supported }

/// <summary>Provider-reported image input support, independent of host image delivery.</summary>
public static class ModelVisionCapabilities
{
    public static ModelVisionSupport Parse(string modelDetails)
    {
        try
        {
            using var document = JsonDocument.Parse(modelDetails);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("capabilities", out var capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() == 0)
                return ModelVisionSupport.Unknown;

            var vision = false;
            foreach (var capability in capabilities.EnumerateArray())
            {
                if (capability.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(capability.GetString()))
                    return ModelVisionSupport.Unknown;
                vision |= string.Equals(capability.GetString(), "vision", StringComparison.OrdinalIgnoreCase);
            }
            return vision ? ModelVisionSupport.Supported : ModelVisionSupport.Unsupported;
        }
        catch (JsonException) { return ModelVisionSupport.Unknown; }
    }

    public static string Label(this ModelVisionSupport support) => support switch
    {
        ModelVisionSupport.Supported => "Vision supported",
        ModelVisionSupport.Unsupported => "Text-only model",
        _ => "Vision capability unknown"
    };

    public static string AgentInstruction(this ModelVisionSupport support) =>
        $"Model image input capability: {support.Label()}. " +
        (support == ModelVisionSupport.Supported
            ? "Only describe visual details when an image has actually been supplied as image input. A screenshot path or URL alone is not image input. "
            : "Use text and DOM observations for browser checks; do not request image input or claim visual inspection. ") +
        "Model capability does not grant browser access. Use only available tools and report any limits on visual verification.";
}
