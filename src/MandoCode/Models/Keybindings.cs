namespace MandoCode.Models;

/// <summary>Shared shortcut labels for command hints and the keyboard reference.</summary>
public static class Keybindings
{
    public const string AgentNew = "Alt+N";
    public const string ChangeDirectory = "Alt+D";
    public const string AgentFileExplorer = "Alt+E";
    public const string AgentGitChanges = "Alt+G";
    public const string AgentHistory = "Alt+H";
    public const string AgentFocus = "Alt+Left/Right";
    public const string AgentClose = "Alt+W";
    public const string WorkspaceNew = "Alt+Shift+N";
    public const string WorkspaceSwitch = "Alt+Shift+Left/Right";
    public const string WorkspaceAll = "Alt+Shift+A";
    public const string WorkspaceClose = "Alt+Shift+W";

    public sealed record Binding(string Group, string Keys, string Action);
    public static readonly IReadOnlyList<Binding> All =
    [
        new("Workspaces", WorkspaceNew, "Create a workspace (/workspace-new)"),
        new("Workspaces", WorkspaceSwitch, "Previous / next workspace (/workspace)"),
        new("Workspaces", WorkspaceAll, "Toggle the All workspaces picker (/workspace-all opens it)"),
        new("Workspaces", WorkspaceClose, "Close the current idle workspace (/workspace-close)"),
        new("Agents", AgentNew, "Create an agent (/agent-new)"),
        new("Agents", ChangeDirectory, "Change this agent's directory (/change-directory)"),
        new("Agents", AgentFileExplorer, "Toggle this agent's file explorer (/agent-file-explorer)"),
        new("Agents", AgentGitChanges, "View this agent's Git changes (/git-changes)"),
        new("Agents", AgentHistory, "Open or close saved-agent history (/history)"),
        new("Agents", AgentFocus, "Switch agents in tab order (/agent-focus)"),
        new("Agents", AgentClose, "Close the selected idle agent (/agent-close)"),
        new("Conversation", "Page Up / Page Down", "Scroll the conversation while the chat prompt is focused"),
        new("Conversation", "Ctrl+End", "Jump to the latest output from the chat prompt"),
        new("Conversation", "Escape / Ctrl+C", "Cancel an active response"),
        new("Menus and input", "Up / Down Arrow Keys", "Move through picker or menu options"),
        new("Menus and input", "Enter", "Select an option or submit input"),
        new("Menus and input", "Tab / Enter", "Accept a command/file suggestion or select a model"),
        new("Menus and input", "Escape", "Dismiss command/file suggestions"),
        new("Menus and input", "Escape", "Dismiss model/workspace pickers or cancel a plan-step edit"),
        new("Plan-step editor", "Enter", "Save the edited step"),
        new("Plan-step editor", "Shift+Enter / Alt+Enter", "Insert a new line"),
        new("Plan-step editor", "Up / Down Arrow Keys", "Move between wrapped lines"),
        new("Plan-step editor", "Page Up / Page Down", "Move a page through the text"),
        new("Plan-step editor", "Home / End", "Move to the start / end of the current line"),
        new("Plan-step editor", "Ctrl+Home / Ctrl+End", "Move to the start / end of the text"),
        new("Plan-step editor", "Shift+navigation key", "Extend the text selection")
    ];
}
