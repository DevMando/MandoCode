using MandoCode.Acp.Protocol;
using Spectre.Console;

namespace MandoCode.Services.Acp;

/// <summary>The connected agent's session details, using its own labels and permission description.</summary>
public static class AcpConnectionCard
{
    public static Panel Build(IAcpAgentRuntime runtime)
    {
        var dim = new Style(Color.Grey);
        var body = new Paragraph();
        var model = AcpPaneCommands.ModelOption(runtime);
        if (model?.CurrentSelection is { } modelValue)
        {
            var label = model.Choices().FirstOrDefault(choice => choice.Value == modelValue)?.Name ?? modelValue;
            body.Append("Model    ", dim).Append(label).Append("\n");
        }

        var modeOption = runtime.ConfigOptions.FirstOrDefault(option => option.IsSelect && option.Category == ConfigOptionCategories.Mode);
        var modeId = modeOption?.CurrentSelection ?? runtime.CurrentMode;
        var mode = modeOption?.Choices().FirstOrDefault(choice => choice.Value == modeId);
        var modeLabel = mode?.Name ?? runtime.Modes?.AvailableModes.FirstOrDefault(choice => choice.Id == modeId)?.Name ?? modeId;
        body.Append("Mode     ", dim).Append(modeLabel ?? "Not reported", new Style(Color.Gold1)).Append("\n");
        if (!string.IsNullOrWhiteSpace(mode?.Description))
            body.Append(mode.Description, dim).Append("\n");

        body.Append("Session  ", dim).Append(runtime.SessionOrigin switch
        {
            AcpSessionOrigin.Resumed => "Resumed",
            AcpSessionOrigin.Loaded => "Restored",
            _ => "New conversation"
        });
        body.Append("\n\nNative conversation preserved; its context is not shared.", dim);
        body.Append("\n/agent-acp-mode", new Style(Color.Cyan1)).Append("  Change mode", dim);
        body.Append("\n/agent-acp stop", new Style(Color.Cyan1)).Append("  Return to the native agent", dim);
        return new Panel(body)
        {
            Header = new PanelHeader($"[green]Connected[/] · {Markup.Escape(runtime.AgentDisplayName ?? runtime.AgentName)} · ACP"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Purple),
            Padding = new Padding(1, 0),
            Expand = false
        };
    }
}
