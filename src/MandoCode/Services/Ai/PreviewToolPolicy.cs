using System.Text.Json;

namespace MandoCode.Services;

/// <summary>Live host preview operations cannot reuse cached observations or action results.</summary>
public static class PreviewToolPolicy
{
    public static bool IsLiveTool(string name) => name is
        "open_desktop_preview" or "refresh_desktop_preview" or "inspect_desktop_preview" or
        "observe_desktop_preview" or "click_desktop_preview" or "press_key_desktop_preview" or
        "hover_desktop_preview" or "fill_desktop_preview" or "select_desktop_preview" or
        "scroll_desktop_preview" or "wait_for_desktop_preview";

    public static bool IsFailure(string name, string result) => IsLiveTool(name) && !HasSuccessfulResult(result);

    public static bool IsFreshObservation(string name, string result) =>
        IsLiveTool(name) && name is not ("open_desktop_preview" or "refresh_desktop_preview") &&
        HasSuccessfulResult(result, requireDocument: true);

    private static bool HasSuccessfulResult(string result, bool requireDocument = false)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True && (!requireDocument ||
                    root.TryGetProperty("readyState", out var ready) && ready.ValueKind == JsonValueKind.String &&
                    ready.GetString() is "interactive" or "complete");
        }
        catch (JsonException) { return false; }
    }
}
