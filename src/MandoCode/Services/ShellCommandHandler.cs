using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Handles shell escape execution (!cmd, /command, cd interception).
/// Updates ProjectRootAccessor on cd.
/// </summary>
public class ShellCommandHandler
{
    private readonly FileAutocompleteProvider _fileProvider;
    private readonly ProjectRootAccessor _projectRoot;

    public ShellCommandHandler(FileAutocompleteProvider fileProvider, ProjectRootAccessor projectRoot)
    {
        _fileProvider = fileProvider;
        _projectRoot = projectRoot;
    }

    public void HandleShellCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] !<command>  or  /command <command>");
            AnsiConsole.MarkupLine("[dim]Examples: !ls -la, !git status, !cd ..[/]");
            AnsiConsole.WriteLine();
            return;
        }

        // Intercept cd so the AI agent stays aware of the new working directory
        if (cmd == "cd" || cmd.StartsWith("cd "))
        {
            var target = cmd.Length > 2 ? cmd[3..].Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(target))
                target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                var newDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), target));
                if (!Directory.Exists(newDir))
                {
                    AnsiConsole.MarkupLine($"[red]cd: no such directory: {Spectre.Console.Markup.Escape(target)}[/]");
                    AnsiConsole.WriteLine();
                    return;
                }

                Directory.SetCurrentDirectory(newDir);
                _projectRoot.ProjectRoot = newDir;
                _fileProvider.RefreshCache();

                // OSC 9;9 — tell Windows Terminal the new CWD
                Console.Write($"\u001b]9;9;{newDir}\u0007");

                AnsiConsole.MarkupLine($"[green]Changed directory to[/] {Spectre.Console.Markup.Escape(newDir)}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]cd: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
            }
            AnsiConsole.WriteLine();
            return;
        }

        // Run the command in a child process
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var escapedCmd = cmd.Replace("\"", "\\\"");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? "/c " + cmd : "-c \"" + escapedCmd + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start process.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);

            if (!string.IsNullOrEmpty(stdout))
                Console.Write(stdout);
            if (!string.IsNullOrEmpty(stderr))
                Console.Write($"\u001b[31m{stderr}\u001b[0m");
            if (string.IsNullOrEmpty(stdout) && string.IsNullOrEmpty(stderr))
                AnsiConsole.MarkupLine("[dim](no output)[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Shell error: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
