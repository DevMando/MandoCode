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
    private readonly PlanHandoff _planHandoff;
    private readonly CancelKeyCoordinator _keyCoordinator;
    private readonly InstructionPromptCoordinator _instructionPrompt;

    private bool _globalWriteBypass = false;
    private readonly HashSet<string> _approvedFiles = new(StringComparer.OrdinalIgnoreCase);

    public DiffApprovalHandler(
        SpinnerService spinner,
        ProjectRootAccessor projectRoot,
        PlanHandoff planHandoff,
        CancelKeyCoordinator keyCoordinator,
        InstructionPromptCoordinator instructionPrompt)
    {
        _spinner = spinner;
        _projectRoot = projectRoot;
        _planHandoff = planHandoff;
        _keyCoordinator = keyCoordinator;
        _instructionPrompt = instructionPrompt;
    }

    // Labels used as both UI text and switch discriminators. Spectre markup is
    // baked into the strings so SelectionPrompt renders them in color while
    // string equality at selection time still resolves correctly.
    private const string ApproveLabel = "[green]Approve[/]";
    private const string ApproveDeletionLabel = "[green]Approve deletion[/]";
    private const string ApproveNoAskWriteLabel = "[green]Approve - okay to write & modify files don't ask me again[/]";
    private const string ApproveNoAskRunLabel = "[green]Approve - okay to run commands don't ask me again[/]";
    private const string ApproveNoAskDeleteLabel = "[green]Approve - okay to write, modify & delete files don't ask me again[/]";
    private const string DenyLabel = "[red]Deny[/]";
    private const string ProvideInstructionsLabel = "[mediumpurple1]Provide new instructions[/]";
    private const string CancelPlanLabel = "[red]Cancel the plan[/]";

    private string FileLink(string relativePath) =>
        FileLinkHelper.FileLink(_projectRoot.ProjectRoot, relativePath);

    public async Task<DiffApprovalResult> HandleDiffApproval(string relativePath, string? oldContent, string newContent)
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
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        // Build choices for the SelectionPrompt — now safe since our spinner is stopped
        var noAskLabel = isNewFile
            ? ApproveNoAskWriteLabel
            : $"[green]Approve - Don't ask again to modify {Spectre.Console.Markup.Escape(fileName)}[/]";

        var choices = new List<string>
        {
            ApproveLabel,
            noAskLabel,
            DenyLabel,
            ProvideInstructionsLabel
        };
        // "Cancel the plan" only makes sense when a plan is actually running — in a direct
        // chat, the user can just pick Deny to abort this single tool call.
        if (_planHandoff.IsExecuting) choices.Add(CancelPlanLabel);

        // Single suppression scope spans BOTH prompts and the if/else dispatch.
        // If the user picks "Provide new instructions" and starts typing ahead
        // before the TextPrompt opens, those queued keys aren't eaten by the
        // listener during the gap.
        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Apply these changes?[/]")
                    .HighlightStyle(new Style(decoration: Decoration.Invert))
                    .AddChoices(choices)
            );

            if (approvalChoice == ApproveLabel)
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
            else if (approvalChoice == DenyLabel)
            {
                AnsiConsole.MarkupLine("[red]Changes denied.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
            }
            else if (approvalChoice == CancelPlanLabel)
            {
                AnsiConsole.MarkupLine("[red]Plan cancellation requested.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
            }
            else // "Provide new instructions"
            {
                var instructions = await _instructionPrompt.RequestAsync("Enter your instructions:");
                AnsiConsole.MarkupLine("[yellow]Redirecting with new instructions...[/]");
                result = new DiffApprovalResult
                {
                    Response = DiffApprovalResponse.NewInstructions,
                    UserMessage = instructions
                };
            }
        }

        AnsiConsole.WriteLine();

        // Resume spinner for the rest of the AI call
        _spinner.Start();

        return result;
    }

    public async Task<DiffApprovalResult> HandleCommandApproval(string command)
    {
        // Stop the spinner so we have clean console output
        _spinner.Stop();

        // Render command panel — size to fit the command text
        var contentWidth = command.Length + 5; // "│  $ " prefix
        var boxWidth = Math.Max(50, contentWidth + 2); // +2 for padding before ┐/┘
        var headerText = "\u250c\u2500 Command ";
        var topPad = Math.Max(0, boxWidth - headerText.Length - 1); // -1 for ┐
        var bottomPad = Math.Max(0, boxWidth - 1); // -1 for ┘

        Console.WriteLine();
        Console.WriteLine($"\u001b[33m{headerText}{new string('\u2500', topPad)}\u2510\u001b[0m");
        Console.WriteLine("\u001b[33m\u2502\u001b[0m");
        Console.WriteLine($"\u001b[33m\u2502\u001b[0m  $ {command}");
        Console.WriteLine("\u001b[33m\u2502\u001b[0m");
        Console.WriteLine($"\u001b[33m\u2514{new string('\u2500', bottomPad)}\u2518\u001b[0m");
        Console.WriteLine();

        // If globally bypassed, auto-approve
        if (_globalWriteBypass)
        {
            AnsiConsole.MarkupLine($"[green]\u2713 Auto-approved command[/]");
            AnsiConsole.WriteLine();
            _spinner.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var cmdChoices = new List<string>
        {
            ApproveLabel,
            ApproveNoAskRunLabel,
            DenyLabel,
            ProvideInstructionsLabel
        };
        if (_planHandoff.IsExecuting) cmdChoices.Add(CancelPlanLabel);

        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Run this command?[/]")
                    .HighlightStyle(new Style(decoration: Decoration.Invert))
                    .AddChoices(cmdChoices)
            );

            if (approvalChoice == ApproveLabel)
            {
                AnsiConsole.MarkupLine("[green]Command approved.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
            }
            else if (approvalChoice == ApproveNoAskRunLabel)
            {
                AnsiConsole.MarkupLine("[green]Command approved.[/]");
                _globalWriteBypass = true;
                AnsiConsole.MarkupLine("[dim]All future writes, deletions, and commands will be auto-approved for this session.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
            }
            else if (approvalChoice == DenyLabel)
            {
                AnsiConsole.MarkupLine("[red]Command denied.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
            }
            else if (approvalChoice == CancelPlanLabel)
            {
                AnsiConsole.MarkupLine("[red]Plan cancellation requested.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
            }
            else // "Provide new instructions"
            {
                var instructions = await _instructionPrompt.RequestAsync("Enter your instructions:");
                AnsiConsole.MarkupLine("[yellow]Redirecting with new instructions...[/]");
                result = new DiffApprovalResult
                {
                    Response = DiffApprovalResponse.NewInstructions,
                    UserMessage = instructions
                };
            }
        }

        AnsiConsole.WriteLine();

        // Resume spinner for the rest of the AI call
        _spinner.Start();

        return result;
    }

    public async Task<DiffApprovalResult> HandleDeleteApproval(string relativePath, string? existingContent)
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
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var delChoices = new List<string>
        {
            ApproveDeletionLabel,
            ApproveNoAskDeleteLabel,
            DenyLabel,
            ProvideInstructionsLabel
        };
        if (_planHandoff.IsExecuting) delChoices.Add(CancelPlanLabel);

        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Delete this file?[/]")
                    .HighlightStyle(new Style(decoration: Decoration.Invert))
                    .AddChoices(delChoices)
            );

            if (approvalChoice == ApproveDeletionLabel)
            {
                AnsiConsole.MarkupLine("[green]Deletion approved.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
            }
            else if (approvalChoice == ApproveNoAskDeleteLabel)
            {
                AnsiConsole.MarkupLine("[green]Deletion approved.[/]");
                _globalWriteBypass = true;
                AnsiConsole.MarkupLine("[dim]All future writes and deletions will be auto-approved for this session.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
            }
            else if (approvalChoice == DenyLabel)
            {
                AnsiConsole.MarkupLine("[red]Deletion denied.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
            }
            else if (approvalChoice == CancelPlanLabel)
            {
                AnsiConsole.MarkupLine("[red]Plan cancellation requested.[/]");
                result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
            }
            else // "Provide new instructions"
            {
                var instructions = await _instructionPrompt.RequestAsync("Enter your instructions:");
                AnsiConsole.MarkupLine("[yellow]Redirecting with new instructions...[/]");
                result = new DiffApprovalResult
                {
                    Response = DiffApprovalResponse.NewInstructions,
                    UserMessage = instructions
                };
            }
        }

        AnsiConsole.WriteLine();

        // Resume spinner for the rest of the AI call
        _spinner.Start();

        return result;
    }

    private void RenderDiffPanel(string relativePath, List<DiffLine> displayLines, int additions, int deletions, bool isNewFile)
    {
        Console.WriteLine();

        var fileLink = FileLink(relativePath);
        var visibleLabel = $"Diff: {relativePath}";

        // Measure content widths to size box dynamically
        var summaryLabel = isNewFile ? " (new file)" : "";
        var summaryLine = $"  {deletions} deletion(s), {additions} addition(s){summaryLabel}";
        var maxContentWidth = Math.Max(visibleLabel.Length + 4, summaryLine.Length + 1);
        foreach (var line in displayLines)
        {
            var num = (line.NewLineNumber ?? line.OldLineNumber)?.ToString("0000") ?? "    ";
            var prefix = line.LineType == DiffLineType.Unchanged ? "   " : " - ";
            if (line.LineType == DiffLineType.Added) prefix = " + ";
            var lineWidth = 2 + num.Length + prefix.Length + line.Content.Length;
            maxContentWidth = Math.Max(maxContentWidth, lineWidth);
        }
        var innerWidth = Math.Max(48, maxContentWidth + 2);

        // Top border with rounded corner
        var topLabel = $"\u256d\u2500 Diff: {fileLink} ";
        var visibleTopLabel = $"\u256d\u2500 Diff: {relativePath} ";
        var topPad = Math.Max(0, innerWidth - visibleTopLabel.Length);
        Console.WriteLine(topLabel + new string('\u2500', topPad) + "\u256e");
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
        Console.WriteLine($"\u2502{summaryLine}");
        Console.WriteLine("\u2570" + new string('\u2500', innerWidth) + "\u256f");
        Console.WriteLine();
    }

    /// <summary>
    /// Prompt UI for first-call MCP tool approvals. Wired into <see cref="McpApprovalGate"/>
    /// via App.razor. The gate remembers "approve for session" decisions itself, so this
    /// handler only renders the prompt for genuinely new (server, tool) pairs.
    /// </summary>
    public Task<DiffApprovalResult> HandleMcpApproval(string serverName, string toolName, string? description)
    {
        _spinner.Stop();

        var title = $"[cyan]Allow MCP tool \"{Markup.Escape(toolName)}\" from \"{Markup.Escape(serverName)}\"?[/]";
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(title);
        if (!string.IsNullOrWhiteSpace(description))
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(description!)}[/]");
        }
        AnsiConsole.WriteLine();

        // Global write bypass covers MCP too \u2014 someone who picked "approve everything"
        // on an earlier write shouldn't be re-prompted for an MCP call.
        if (_globalWriteBypass)
        {
            AnsiConsole.MarkupLine("[green]\u2713 Auto-approved MCP tool[/]");
            AnsiConsole.WriteLine();
            _spinner.Start();
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });
        }

        var noAskMcpLabel = $"[green]Approve - don't ask again for {Spectre.Console.Markup.Escape(toolName)} this session[/]";
        var mcpChoices = new List<string>
        {
            ApproveLabel,
            noAskMcpLabel,
            DenyLabel
        };
        if (_planHandoff.IsExecuting) mcpChoices.Add(CancelPlanLabel);

        string choice;
        using (_keyCoordinator.Suppress())
        {
            choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Run this MCP tool?[/]")
                    .HighlightStyle(new Style(decoration: Decoration.Invert))
                    .AddChoices(mcpChoices)
            );
        }

        DiffApprovalResult result;
        if (choice == ApproveLabel)
        {
            AnsiConsole.MarkupLine("[green]Approved.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }
        else if (choice == noAskMcpLabel)
        {
            AnsiConsole.MarkupLine("[green]Approved for session.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.ApprovedNoAskAgain };
        }
        else if (choice == CancelPlanLabel)
        {
            AnsiConsole.MarkupLine("[red]Plan cancellation requested.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan };
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Denied.[/]");
            result = new DiffApprovalResult { Response = DiffApprovalResponse.Denied };
        }

        AnsiConsole.WriteLine();
        _spinner.Start();
        return Task.FromResult(result);
    }
}
