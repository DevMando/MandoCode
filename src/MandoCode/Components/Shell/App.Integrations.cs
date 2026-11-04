using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace MandoCode.Components;

public partial class App
{
    private string? PromptForSkill()
    {
        var skills = Skills.GetAll();
        if (skills.Count == 0)
        {
            TuiConsole.MarkupLine("[yellow]No skills installed.[/] Run [deepskyblue1]/skills[/] [yellow]for setup instructions.[/]");
            TuiConsole.WriteLine();
            return null;
        }

        const string CancelChoice = "(cancel)";
        const string Separator = "  —  ";

        var entries = new List<string> { CancelChoice };
        foreach (var s in skills)
        {
            entries.Add(string.IsNullOrWhiteSpace(s.Description)
                ? s.Name
                : s.Name + Separator + s.Description);
        }

        string selected;
        try
        {
            selected = TuiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[deepskyblue1]Select a skill to force:[/] [dim](type to filter)[/]")
                    .HighlightStyle(SelectionHighlight)
                    .PageSize(10)
                    .MoreChoicesText("[dim](scroll for more)[/]")
                    .EnableSearch()
                    .AddChoices(entries)
            );
        }
        catch (Exception)
        {
            TuiConsole.WriteLine();
            return null;
        }

        if (selected == CancelChoice)
        {
            TuiConsole.WriteLine();
            return null;
        }

        var dashIdx = selected.IndexOf(Separator, StringComparison.Ordinal);
        return dashIdx > 0 ? selected[..dashIdx] : selected;
    }

    private async Task HandleForceSkillCommandAsync(string skillName)
    {
        TuiConsole.WriteLine();

        if (string.IsNullOrWhiteSpace(skillName))
        {
            var picked = PromptForSkill();
            if (picked == null) return;
            skillName = picked;
        }

        var skill = Skills.GetByName(skillName);
        if (skill == null)
        {
            TuiConsole.MarkupLine($"[red]No skill named[/] [deepskyblue1]{Spectre.Console.Markup.Escape(skillName)}[/] [red]is installed.[/]");
            TuiConsole.MarkupLine("[dim]Run[/] [deepskyblue1]/skills[/] [dim]to see available skills.[/]");
            TuiConsole.WriteLine();
            return;
        }

        if (!_isConnected || _modelError)
        {
            TuiConsole.MarkupLine("[yellow]Cannot run skill — no model is connected.[/]");
            TuiConsole.WriteLine();
            return;
        }

        TuiConsole.MarkupLine($"[green]✓ Forcing skill:[/] [deepskyblue1]{Spectre.Console.Markup.Escape(skill.Name)}[/]");
        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            TuiConsole.MarkupLine($"  [dim]{Spectre.Console.Markup.Escape(skill.Description)}[/]");
        }
        TuiConsole.WriteLine();

        var hostInstruction =
            $"The user explicitly selected skill '{skill.Name}' via /force-skill. " +
            $"Follow these instructions for this turn.\n\n# Skill: {skill.Name}\n\n{skill.Body}";

        _messages.Add(new ChatMsg { Role = "user", Text = $"/force-skill {skill.Name}" });
        await ProcessDirectRequestAsync($"/force-skill {skill.Name}", hostInstruction);
    }

    private async Task HandleMcpReloadCommandAsync()
    {
        var agents = (Pane?.Workspace.Registry?.Workspaces.SelectMany(w => w.Panes) ?? Pane?.Workspace.Panes ?? Array.Empty<AgentPane>()).ToArray();
        if (agents.Any(agent => agent != Pane && agent.IsBusy?.Invoke() == true)) { TuiConsole.MarkupLine("[yellow]Finish work in other agents before reloading shared integrations.[/]"); return; }
        var coordinator = IntegrationCoordinator;
        using var update = coordinator.BeginUpdate();
        TuiConsole.WriteLine();
        TuiConsole.MarkupLine("[dim]Reloading MCP servers...[/]");

        await coordinator.ReloadMcpAsync(agents);

        var active = McpManager.ActiveClients.Count;
        var failed = McpManager.StartupErrors.Count;
        TuiConsole.MarkupLine(failed == 0
            ? $"[green]MCP reloaded: {active} server(s) connected.[/]"
            : $"[yellow]MCP reloaded: {active} connected, {failed} failed.[/]");
        TuiConsole.WriteLine();
    }

    /// <summary>
    /// Lists tools exposed by one or all connected MCP servers, with descriptions.
    /// If <paramref name="serverFilter"/> is null, iterates every active server.
    /// </summary>
    private async Task HandleMcpToolsCommandAsync(string? serverFilter)
    {
        TuiConsole.WriteLine();

        if (McpManager.ActiveClients.Count == 0)
        {
            TuiConsole.MarkupLine("[yellow]No MCP servers are connected.[/]");
            TuiConsole.MarkupLine("[dim]Type /mcp to configure servers.[/]");
            TuiConsole.WriteLine();
            return;
        }

        if (serverFilter != null && !McpManager.ActiveClients.ContainsKey(serverFilter))
        {
            TuiConsole.MarkupLine($"[yellow]Server '{Spectre.Console.Markup.Escape(serverFilter)}' is not connected.[/]");
            TuiConsole.MarkupLine($"[dim]Connected: {string.Join(", ", McpManager.ActiveClients.Keys)}[/]");
            TuiConsole.WriteLine();
            return;
        }

        var clients = serverFilter != null
            ? new[] { (serverFilter, McpManager.ActiveClients[serverFilter]) }
            : McpManager.ActiveClients.Select(kv => (kv.Key, kv.Value)).ToArray();

        foreach (var (serverName, client) in clients)
        {
            IList<ModelContextProtocol.Client.McpClientTool> tools;
            try
            {
                tools = await client.ListToolsAsync();
            }
            catch (Exception ex)
            {
                TuiConsole.MarkupLine($"[red]{Spectre.Console.Markup.Escape(serverName)}:[/] [dim]{Spectre.Console.Markup.Escape(ex.Message)}[/]");
                continue;
            }

            var table = new Table().Border(TableBorder.Rounded).Title($"[deepskyblue1]{Spectre.Console.Markup.Escape(serverName)}[/] [dim]({tools.Count} tool{(tools.Count == 1 ? "" : "s")})[/]");
            table.AddColumn("Tool");
            table.AddColumn("Description");
            foreach (var t in tools)
            {
                var desc = string.IsNullOrWhiteSpace(t.Description) ? "[dim](no description)[/]" : Spectre.Console.Markup.Escape(t.Description);
                table.AddRow(Spectre.Console.Markup.Escape(t.Name), desc);
            }
            TuiConsole.Write(table);
        }
        TuiConsole.WriteLine();
    }

    private async Task HandleLearnCommandAsync()
    {
        TuiConsole.WriteLine();
        LearnContent.Display();

        if (_isConnected)
        {
            var wantChat = await WizardConfirmAsync("Chat with an AI educator to learn more?", false);
            if (wantChat)
            {
                await AI.EnterLearnModeAsync();
                TuiConsole.WriteLine();
                TuiConsole.MarkupLine("[green]AI Educator mode active![/]");
                TuiConsole.MarkupLine("[dim]Ask me anything about local AI! Type /clear to return to normal mode.[/]");
                TuiConsole.WriteLine();
            }
        }
    }

}
