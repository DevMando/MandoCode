using MandoCode.Models;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Handles diff approval UI for file writes and deletes.
/// Manages per-file and global bypass state.
/// </summary>
public class DiffApprovalHandler
{
    private readonly SpinnerService _spinner;
    private readonly ProjectRootAccessor _projectRoot;

    private bool _globalWriteBypass = false;
    private readonly HashSet<string> _approvedFiles = new(StringComparer.OrdinalIgnoreCase);

    public DiffApprovalHandler(SpinnerService spinner, ProjectRootAccessor projectRoot)
    {
        _spinner = spinner;
        _projectRoot = projectRoot;
    }

    private string FileLink(string relativePath) =>
        FileLinkHelper.FileLink(_projectRoot.ProjectRoot, relativePath);

    public Task<DiffApprovalResult> HandleDiffApproval(string relativePath, string? oldContent, string newContent)
    {
        // Stop the spinner so we have clean console output
        _spinner.Stop();

        // Compute diff
        var diffLines = DiffService.ComputeDiff(oldContent, newContent);
        var isNewFile = oldContent == null;
        var displayLines = DiffService.CollapseContext(diffLines, 3);
        var additions = diffLines.Count(l => l.LineType == DiffLineType.Added);
        var deletions = diffLines.Count(l => l.LineType == DiffLineType.Removed);
        var fileName = Path.GetFileName(relativePath);

        // Always show the diff so the user can see what's changing
        RenderDiffPanel(relativePath, displayLines, additions, deletions, isNewFile);

        // If globally bypassed or this file is already approved, auto-approve without prompting
        if (_globalWriteBypass || _approvedFiles.Contains(relativePath))
        {
            AnsiConsole.MarkupLine($"[green]\u2713 Auto-approved[/]");
            AnsiConsole.WriteLine();
            _spinner.Start();
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });
        }

        // Build choices for the SelectionPrompt — now safe since our spinner is stopped
        var noAskLabel = isNewFile
            ? "Approve - okay to write & modify files don't ask me again"
            : $"Approve - Don't ask again to modify {fileName}";

        var approvalChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Apply these changes?[/]")
                .AddChoices(new[]
                {
                    "Approve",
                    noAskLabel,
                    "Deny",
                    "Provide new instructions"
                })
        );

        DiffApprovalResult result;

        if (approvalChoice == "Approve")
        {
            AnsiConsole.MarkupLine("[green]Changes approved.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (approvalChoice == noAskLabel)
        {
            AnsiConsole.MarkupLine("[green]Changes approved.[/]");
            if (isNewFile)
            {
                _globalWriteBypass = true;
                AnsiConsole.MarkupLine("[dim]All future writes will be auto-approved for this session.[/]");
            }
            else
            {
                _approvedFiles.Add(relativePath);
                AnsiConsole.MarkupLine($"[dim]Future modifications to {Spectre.Console.Markup.Escape(fileName)} will be auto-approved.[/]");
            }
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (approvalChoice == "Deny")
        {
            AnsiConsole.MarkupLine("[red]Changes denied.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }
        else // "Provide new instructions"
        {
            var instructions = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter your instructions:[/]")
            );
            AnsiConsole.MarkupLine("[yellow]Redirecting with new instructions...[/]");
            result = new DiffApprovalResult
            {
                Response = DiffApprovalResponse.NewInstructions,
                UserMessage = instructions
            };
        }

        AnsiConsole.WriteLine();

        // Resume spinner for the rest of the AI call
        _spinner.Start();

        return Task.FromResult(result);
    }

    public Task<DiffApprovalResult> HandleDeleteApproval(string relativePath, string? existingContent)
    {
        // Stop the spinner so we have clean console output
        _spinner.Stop();

        var fileName = Path.GetFileName(relativePath);
        var isFolder = existingContent != null && existingContent.StartsWith("Folder:");

        if (isFolder)
        {
            // Folder deletion — show folder listing instead of diff
            Console.WriteLine();
            Console.WriteLine($"\u001b[31m\u250c\u2500 Delete Folder: {relativePath}/ \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510\u001b[0m");
            Console.WriteLine("\u001b[31m\u2502\u001b[0m");
            foreach (var line in existingContent!.Split('\n'))
            {
                Console.WriteLine($"\u001b[31m\u2502 {line}\u001b[0m");
            }
            Console.WriteLine("\u001b[31m\u2502\u001b[0m");
            Console.WriteLine($"\u001b[31m\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518\u001b[0m");
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]This will DELETE the folder and ALL its contents: {Spectre.Console.Markup.Escape(relativePath)}/[/]");
            Console.WriteLine();
        }
        else
        {
            // File deletion — show diff as before
            var diffLines = new List<DiffLine>();
            if (!string.IsNullOrEmpty(existingContent))
            {
                var lines = existingContent.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    diffLines.Add(new DiffLine
                    {
                        LineType = DiffLineType.Removed,
                        Content = lines[i].TrimEnd('\r'),
                        OldLineNumber = i + 1
                    });
                }
            }

            var deletions = diffLines.Count;
            var displayLines = DiffService.CollapseContext(diffLines, 3);

            RenderDiffPanel(relativePath, displayLines, 0, deletions, false);
            AnsiConsole.MarkupLine($"[red]This will DELETE the file: {Spectre.Console.Markup.Escape(relativePath)}[/]");
            Console.WriteLine();
        }

        // If globally bypassed, auto-approve
        if (_globalWriteBypass)
        {
            AnsiConsole.MarkupLine($"[green]\u2713 Auto-approved deletion[/]");
            AnsiConsole.WriteLine();
            _spinner.Start();
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });
        }

        var approvalChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Delete this file?[/]")
                .AddChoices(new[]
                {
                    "Approve deletion",
                    "Approve - okay to write, modify & delete files don't ask me again",
                    "Deny",
                    "Provide new instructions"
                })
        );

        DiffApprovalResult result;

        if (approvalChoice == "Approve deletion")
        {
            AnsiConsole.MarkupLine("[green]Deletion approved.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (approvalChoice.StartsWith("Approve - okay"))
        {
            AnsiConsole.MarkupLine("[green]Deletion approved.[/]");
            _globalWriteBypass = true;
            AnsiConsole.MarkupLine("[dim]All future writes and deletions will be auto-approved for this session.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (approvalChoice == "Deny")
        {
            AnsiConsole.MarkupLine("[red]Deletion denied.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }
        else // "Provide new instructions"
        {
            var instructions = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Enter your instructions:[/]")
            );
            AnsiConsole.MarkupLine("[yellow]Redirecting with new instructions...[/]");
            result = new DiffApprovalResult
            {
                Response = DiffApprovalResponse.NewInstructions,
                UserMessage = instructions
            };
        }

        AnsiConsole.WriteLine();

        // Resume spinner for the rest of the AI call
        _spinner.Start();

        return Task.FromResult(result);
    }

    private void RenderDiffPanel(string relativePath, List<DiffLine> displayLines, int additions, int deletions, bool isNewFile)
    {
        Console.WriteLine();

        var header = $"\u250c\u2500 Diff: {FileLink(relativePath)} ";
        var padLen = Math.Max(0, 50 - header.Length);
        Console.WriteLine(header + new string('\u2500', padLen) + "\u2510");
        Console.WriteLine("\u2502");

        foreach (var line in displayLines)
        {
            var lineNum = "";

            switch (line.LineType)
            {
                case DiffLineType.Removed:
                    lineNum = line.OldLineNumber.HasValue ? $"{line.OldLineNumber,4}" : "    ";
                    Console.Write("\u2502 ");
                    Console.Write($"\u001b[31m{lineNum} - {line.Content}\u001b[0m");
                    Console.WriteLine();
                    break;

                case DiffLineType.Added:
                    lineNum = line.NewLineNumber.HasValue ? $"{line.NewLineNumber,4}" : "    ";
                    Console.Write("\u2502 ");
                    Console.Write($"\u001b[38;2;135;206;250m{lineNum} + {line.Content}\u001b[0m");
                    Console.WriteLine();
                    break;

                case DiffLineType.Unchanged:
                    lineNum = line.OldLineNumber.HasValue ? $"{line.OldLineNumber,4}" : "    ";
                    Console.Write("\u2502 ");
                    Console.Write($"\u001b[2m{lineNum}   {line.Content}\u001b[0m");
                    Console.WriteLine();
                    break;
            }
        }

        Console.WriteLine("\u2502");
        var fileLabel = isNewFile ? " (new file)" : "";
        Console.WriteLine($"\u2502  {deletions} deletion(s), {additions} addition(s){fileLabel}");
        Console.WriteLine("\u2514" + new string('\u2500', Math.Max(padLen + header.Length - 1, 40)) + "\u2518");
        Console.WriteLine();
    }
}
