using Microsoft.AspNetCore.Components.Web;

namespace MandoCode.Services;

/// <summary>Route global shortcuts before local menus and editor input. Inactive panes
/// consume global shortcuts without changing workspace selection.</summary>
public static class AgentKeyboardRouter
{
    public static bool TryHandleGlobal(AgentPane? pane, KeyboardEventArgs key)
    {
        if (!(key.AltKey || key.MetaKey) || pane is null) return false;
        return !pane.Active || pane.Workspace.Key(pane, key);
    }
}