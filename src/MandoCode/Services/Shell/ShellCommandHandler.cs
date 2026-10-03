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

    public string ChangeDirectory(string target)
    {
        target = target.Trim().Trim('"');
        if (target == "~" || target.StartsWith("~/") || target.StartsWith("~\\"))
            target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target.Length > 2 ? target[2..] : "");
        var directory = Path.GetFullPath(target, _projectRoot.ProjectRoot);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"No such directory: {target}");
        _projectRoot.ProjectRoot = directory;
        _fileProvider.RefreshCache();
        return directory;
    }
    public async Task HandleShellCommandAsync(string cmd)
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
                var newDir = ChangeDirectory(target);

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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _projectRoot.ProjectRoot
            };
            // Use ArgumentList for proper escaping instead of manual string building
            psi.ArgumentList.Add(isWindows ? "/c" : "-c");
            psi.ArgumentList.Add(cmd);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start process.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // Stream output line-by-line with a cap to prevent OOM
            const int maxChars = 100_000;
            var totalChars = 0;
            var hasOutput = false;

            // Read stderr in background
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // Stream stdout line-by-line
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                if (totalChars < maxChars)
                {
                    Console.WriteLine(line);
                    totalChars += line.Length + 1;
                }
                hasOutput = true;
            }

            if (totalChars >= maxChars)
            {
                AnsiConsole.MarkupLine("[yellow][output truncated at 100k characters][/]");
            }

            var stderr = await stderrTask;
            if (!string.IsNullOrEmpty(stderr))
            {
                Console.Write($"\u001b[31m{stderr}\u001b[0m");
                hasOutput = true;
            }

            if (!hasOutput)
                AnsiConsole.MarkupLine("[dim](no output)[/]");

            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to kill timed-out process: {ex.Message}"); }
                AnsiConsole.MarkupLine("[yellow]Command timed out after 30 seconds.[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Shell error: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
