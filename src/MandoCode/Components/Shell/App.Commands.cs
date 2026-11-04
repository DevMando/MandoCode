using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;

namespace MandoCode.Components;

// UI-owning commands stay with the component; reusable command operations live in Services/Commands.
public partial class App
{
    private async Task<CliCommandResult> ExecuteSlashCommandAsync(string input)
    {
        var parsed = CliCommand.Parse(input);
        var command = parsed.DispatchText;

        // Handle special commands
        if (command == "update")
        {
            await HandleUpdateCommandAsync();
            if (ToolUpdater.Pending) return CliCommandResult.Exit;
            return CliCommandResult.Continue;
        }
        if (command == "exit")
        {
            StopMusicVisualizer();
            MusicPlayer.Dispose();
            ThemeService.Dispose();
            TuiConsole.WriteLine("Goodbye!");
            Lifetime.StopApplication();
            return CliCommandResult.Exit;
        }

        if (command == "compact")
        {
            if (await AI.CompactHistoryAsync())
            {
                try { if (Pane is null || Pane.Id == 1) SessionResumeStore.Save(ProjectRoot.ProjectRoot, AI.ExportHistoryJson()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("Could not save compacted context: {0}", ex.Message); }
                SaveAgentHistory(false);
                TuiConsole.MarkupLine("[green]Conversation context compacted into a recap. The visible transcript is unchanged.[/]");
            }
            else
            {
                TuiConsole.MarkupLine("[dim]Not enough conversation context to compact yet.[/]");
            }
            return CliCommandResult.Continue;
        }

        if (command == "clear")
        {
            await AI.ClearHistoryAsync();
            StateMachine.ClearHistory();
            SessionResumeStore.Delete(ProjectRoot.ProjectRoot);   // cleared means cleared — --continue too
            TuiConsole.Clear();
            _messages.Clear();
            if (Pane is not null) Pane.Services.GetRequiredService<AgentArchiveStore>().Delete(Pane.PersistKey);
            TuiConsole.MarkupLine("[yellow]Conversation context wiped completely. Start a new conversation.[/]");
            return CliCommandResult.Continue;
        }

        if (command == "plan-resume")
        {
            await HandlePlanCommandAsync("resume");
            return CliCommandResult.Continue;
        }

        if (command == "plan-discard")
        {
            await HandlePlanCommandAsync("discard");
            return CliCommandResult.Continue;
        }

        if (command == "plan" || command.StartsWith("plan "))
        {
            _messages.Add(new ChatMsg { Role = "user", Text = input });
            // GetCommandName lower-cases for dispatch. Recover arguments from the original
            // input so paths, identifiers, and quoted goal text keep their casing.
            await HandlePlanCommandAsync(parsed.Arguments);
            return CliCommandResult.Continue;
        }

        if (command is "history" or "agent-history")
        {
            await ShowHistoryAsync();
            return CliCommandResult.Continue;
        }

        if (command == "keybindings")
        {
            CliReferenceCommands.ShowKeybindings();
            return CliCommandResult.Continue;
        }
        if (command == "help")
        {
            // Append separate entries to the selected pane so the complete reference
            // remains scrollable even when it is taller than the terminal viewport.
            using var helpOutput = Pane is null ? null : TuiConsole.Enter(Pane.Session);
            CliReferenceCommands.ShowHelp(Config);
            return CliCommandResult.Continue;
        }
        if (command == "config" || command.StartsWith("config "))
        {
            // "/config set <key> <value>" — inline setter, no wizard round-trip.
            // Args are parsed from the RAW input: GetCommandName lowercases the
            // command string, which would mangle case-sensitive values.
            var rawConfigArgs = parsed.Arguments;

            if (rawConfigArgs.StartsWith("set", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConfigSetCommandAsync(rawConfigArgs[3..].Trim());
                return CliCommandResult.Continue;
            }
            if (rawConfigArgs.Length > 0)
            {
                TuiConsole.MarkupLine("[yellow]Unknown /config subcommand.[/]");
                TuiConsole.MarkupLine("[dim]Usage: [deepskyblue1]/config[/] (guided wizard) or [deepskyblue1]/config set <key> <value>[/][/]");
                TuiConsole.WriteLine();
                return CliCommandResult.Continue;
            }

            await HandleConfigCommandAsync();
            return CliCommandResult.Continue;
        }

        if (command == "transcript-save" || command.StartsWith("transcript-save ", StringComparison.Ordinal))
        {
            var args = parsed.Arguments;
            await CliReferenceCommands.SaveTranscriptAsync(Pane?.Session ?? TuiConsole.Current,
                Pane?.Name ?? "MandoCode", ProjectRoot.ProjectRoot, Config.GetEffectiveModelName(), args);
            return CliCommandResult.Continue;
        }
        if (command is "context-snap-create" or "context-snap-import") { await CaptureSnapshot(); await ShowSnapshots(command == "context-snap-create"); return CliCommandResult.Continue; }
        if (command == "agent-settings") { _isProcessing = false; await ToggleSettings(); return CliCommandResult.Continue; }
        if (command == "agent-file-explorer")
        {
            _fileExplorerOpen = !_fileExplorerOpen;
            _gitChangesOpen = false;
            await InvokeAsync(StateHasChanged);
            return CliCommandResult.Continue;
        }
        if (command is "git-changes" or "agent-git-changes")
        {
            await ToggleGitChanges();
            return CliCommandResult.Continue;
        }
        if (command == "change-directory" || command.StartsWith("change-directory "))
        {
            var rawArgs = parsed.Arguments;
            string? target;
            try { target = rawArgs.Length > 0 ? rawArgs : await PickProjectDirectoryAsync(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException or System.IO.IOException)
            {
                TuiConsole.WriteLine($"Couldn't open the folder picker: {ex.Message}");
                return CliCommandResult.Continue;
            }
            if (!string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    var directory = Shell.ChangeDirectory(target);
                    Skills.Reload();
                    await AI.RefreshSettingsAsync(Config);
                    AI.AppendUserNote(
                        $"[Project root changed to: {directory}. Earlier messages refer to the previous folder — " +
                        "re-read any file you need rather than reusing paths or contents from before this point.]");
                    TuiConsole.WriteLine($"Changed directory to {directory}");
                    TuiConsole.WriteLine("Switching to the new project — this conversation is kept.");
                }
                catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
                {
                    TuiConsole.WriteLine($"Couldn't change directory: {ex.Message}");
                }
                await InvokeAsync(StateHasChanged);
            }
            return CliCommandResult.Continue;
        }
        if (command == "ollama-pull") { await ShowOllamaPull(); return CliCommandResult.Continue; }
        if (command == "model")
        {
            await HandleModelCommandAsync();
            return CliCommandResult.Continue;
        }

        if (command == "setup")
        {
            // Hide the HomeView component (static info + ready + help table)
            // while the wizard runs imperatively — without this the VDOM
            // redraws that block on top of the wizard's output every time
            // SetOnboardingStatus fires StateHasChanged. forceInteractive
            // ensures the wizard always shows visible UI rather than hitting
            // the silent fast path, so users don't end up with a blank screen.
            _setupActive = true;
            await InvokeAsync(StateHasChanged);

            try
            {
                await RunOnboardingFlowAsync(forceInteractive: true);
            }
            finally
            {
                _setupActive = false;
            }

            // Re-probe after the flow so /setup can also recover the connection state.
            var probe = await OllamaSetupHelper.ProbeAsync(Config.OllamaEndpoint);
            _isConnected = probe.Ok;
            if (_isConnected)
            {
                await ValidateCurrentModelAsync();
            }
            await InvokeAsync(StateHasChanged);
            return CliCommandResult.Continue;
        }

        if (command == "learn")
        {
            await HandleLearnCommandAsync();
            return CliCommandResult.Continue;
        }

        if (command == "retry")
        {
            await HandleRetryCommandAsync();
            return CliCommandResult.Continue;
        }
        if (command == "ollama-serve")
        {
            await HandleRetryCommandAsync();
            return CliCommandResult.Continue;
        }

        if (command == "copy")
        {
            HandleCopyCommand();
            return CliCommandResult.Continue;
        }

        if (command == "copy-code")
        {
            HandleCopyCodeCommand();
            return CliCommandResult.Continue;
        }

        if (command == "rabbit")
        {
            HandleRabbitCommand();
            return CliCommandResult.Continue;
        }

        // Music commands
        if (command == "music") { HandleMusicCommand("play"); return CliCommandResult.Continue; }
        if (command == "music-stop") { HandleMusicCommand("stop"); return CliCommandResult.Continue; }
        if (command == "music-pause") { HandleMusicCommand("pause"); return CliCommandResult.Continue; }
        if (command == "music-next") { HandleMusicCommand("next"); return CliCommandResult.Continue; }
        if (command == "music-playlist") { HandleMusicPlaySelection(); return CliCommandResult.Continue; }
        if (command == "music-list") { HandleMusicCommand("list"); return CliCommandResult.Continue; }
        if (command.StartsWith("music-vol"))
        {
            HandleMusicCommand(command);
            return CliCommandResult.Continue;
        }

        if (command == "command" || command.StartsWith("command "))
        {
            var shellCmd = command.Length > 7 ? command[8..].Trim() : "";
            await Shell.HandleShellCommandAsync(shellCmd);
            return CliCommandResult.Continue;
        }

        if (command == "skills")
        {
            await ShowIntegrationsAsync(1);
            return CliCommandResult.Continue;
        }

        if (command == "force-skill" || command.StartsWith("force-skill "))
        {
            var skillName = command.Length > "force-skill".Length
                ? command["force-skill".Length..].Trim()
                : "";
            await HandleForceSkillCommandAsync(skillName);
            return CliCommandResult.Continue;
        }

        if (command == "mcp")
        {
            await ShowIntegrationsAsync(0);
            return CliCommandResult.Continue;
        }

        if (command == "mcp tools" || command.StartsWith("mcp tools "))
        {
            var serverFilter = command.Length > "mcp tools".Length
                ? command["mcp tools".Length..].Trim()
                : null;
            await HandleMcpToolsCommandAsync(string.IsNullOrWhiteSpace(serverFilter) ? null : serverFilter);
            return CliCommandResult.Continue;
        }

        if (command == "mcp-reload")
        {
            await HandleMcpReloadCommandAsync();
            return CliCommandResult.Continue;
        }

        // Unknown command
        TuiConsole.MarkupLine($"[red]Unknown command: {Spectre.Console.Markup.Escape(input)}[/]");
        TuiConsole.MarkupLine("[dim]Type /help for available commands[/]");
        TuiConsole.WriteLine();
        return CliCommandResult.Continue;
    }
}
