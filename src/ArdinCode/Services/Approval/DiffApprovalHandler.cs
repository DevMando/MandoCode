using ArdinCode.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArdinCode.Services;

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
    private readonly ApprovalSelectCoordinator _approvalSelect;
    private readonly ApprovalPromptGate _promptGate;

    private bool _globalWriteBypass = false;
    private readonly HashSet<string> _approvedFiles = new(StringComparer.OrdinalIgnoreCase);

    public DiffApprovalHandler(
        SpinnerService spinner,
        ProjectRootAccessor projectRoot,
        PlanHandoff planHandoff,
        CancelKeyCoordinator keyCoordinator,
        InstructionPromptCoordinator instructionPrompt,
        ApprovalSelectCoordinator approvalSelect,
        ApprovalPromptGate promptGate)
    {
        _spinner = spinner;
        _projectRoot = projectRoot;
        _planHandoff = planHandoff;
        _keyCoordinator = keyCoordinator;
        _instructionPrompt = instructionPrompt;
        _approvalSelect = approvalSelect;
        _promptGate = promptGate;
    }

    // Labels used as both UI text and switch discriminators. These are now PLAIN text
    // (no Spectre markup): the VDOM ApprovalSelect carries each option's color separately
    // and parses its Content as markup, so an embedded color tag would be double-applied
    // and a literal '[' would corrupt the render. The returned choice is the option's
    // Text verbatim, so plain-string equality below still resolves correctly.
    private const string ApproveLabel = "Approve";
    private const string ApproveDeletionLabel = "Approve deletion";
    private const string ApproveNoAskWriteLabel = "Approve - okay to write & modify files don't ask me again";
    private const string ApproveNoAskRunLabel = "Approve - okay to run commands don't ask me again";
    private const string ApproveNoAskDeleteLabel = "Approve - okay to write, modify & delete files don't ask me again";
    private const string DenyLabel = "Deny";
    private const string ProvideInstructionsLabel = "Provide new instructions";
    private const string CancelPlanLabel = "Cancel the plan";

    // Option palette, applied by ApprovalSelect. Green = proceed; warm gold (rgb(255,200,80),
    // the app's second accent, also used for files in the autocomplete panel and the
    // submitted-prompt echo) = deny / redirect; red stays reserved for the destructive
    // "Cancel the plan".
    private static readonly Color ApproveColor = Color.Green;
    private static readonly Color RedirectColor = new(255, 200, 80);
    private static readonly Color DestructiveColor = Color.Red;

    private string FileLink(string relativePath) =>
        FileLinkHelper.FileLink(_projectRoot.ProjectRoot, relativePath);

    public async Task<DiffApprovalResult> HandleDiffApproval(string relativePath, string? oldContent, string newContent)
    {
        // Serialize against every other approval prompt — AllowConcurrentInvocation means
        // two approval-gated tool calls can arrive at once, and two concurrent Spectre
        // prompts steal each other's keys (the invisible one then blocks its function
        // forever). Held for the whole method so the diff render, the prompt, and the
        // spinner restart all happen as one atomic console interaction.
        using var promptHold = await _promptGate.AcquireAsync();

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

        // Build choices for the menu — now safe since our spinner is stopped. The
        // not-new-file label embeds the file name, so escape it: ApprovalSelect parses each
        // option's text as Spectre markup, and the same escaped text is what's returned and
        // compared below.
        var noAskLabel = isNewFile
            ? ApproveNoAskWriteLabel
            : $"Approve - Don't ask again to modify {Spectre.Console.Markup.Escape(fileName)}";

        var choices = new List<ApprovalSelectCoordinator.Option>
        {
            new(ApproveLabel, ApproveColor),
            new(noAskLabel, ApproveColor),
            new(DenyLabel, RedirectColor),
            new(ProvideInstructionsLabel, RedirectColor)
        };
        // "Cancel the plan" only makes sense when a plan is actually running — in a direct
        // chat, the user can just pick Deny to abort this single tool call.
        if (_planHandoff.IsExecuting) choices.Add(new(CancelPlanLabel, DestructiveColor));

        // Title is rendered imperatively to scrollback — ApprovalSelect holds only the
        // option rows (see ApprovalSelect.razor).
        AnsiConsole.MarkupLine("[deepskyblue1]Apply these changes?[/]");

        // Single suppression scope spans BOTH prompts and the if/else dispatch. Suppress()
        // is still required even though the menu is now VDOM: it stops App's background
        // Escape listener from reading the same console and stealing keys from the keyboard
        // pump — exactly the way the pump used to steal them from Spectre's prompt.
        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = await _approvalSelect.RequestAsync(choices);

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
        // Serialized — see the note in HandleDiffApproval.
        using var promptHold = await _promptGate.AcquireAsync();

        // Stop the spinner so we have clean console output
        _spinner.Stop();

        // Render command panel — matches the rounded/dim border style used
        // for fenced code blocks (see MarkdownHtmlRenderer.TranslateCodeBlock).
        string highlighted;
        try
        {
            highlighted = SyntaxHighlighter.Highlight($"$ {command}", "bash");
        }
        catch
        {
            highlighted = Markup.Escape($"$ {command}");
        }

        var commandPanel = new Panel(new Markup(highlighted))
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);
        commandPanel.Header = new PanelHeader("[deepskyblue1] Command [/]", Justify.Left);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(commandPanel);
        AnsiConsole.WriteLine();

        // If globally bypassed, auto-approve
        if (_globalWriteBypass)
        {
            AnsiConsole.MarkupLine($"[green]\u2713 Auto-approved command[/]");
            AnsiConsole.WriteLine();
            _spinner.Start();
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        var cmdChoices = new List<ApprovalSelectCoordinator.Option>
        {
            new(ApproveLabel, ApproveColor),
            new(ApproveNoAskRunLabel, ApproveColor),
            new(DenyLabel, RedirectColor),
            new(ProvideInstructionsLabel, RedirectColor)
        };
        if (_planHandoff.IsExecuting) cmdChoices.Add(new(CancelPlanLabel, DestructiveColor));

        // Title to scrollback; the VDOM menu holds only the option rows. Suppress() still
        // gates App's Escape listener so it can't steal keys from the keyboard pump.
        AnsiConsole.MarkupLine("[deepskyblue1]Run this command?[/]");

        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = await _approvalSelect.RequestAsync(cmdChoices);

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
        // Serialized — see the note in HandleDiffApproval.
        using var promptHold = await _promptGate.AcquireAsync();

        // Stop the spinner so we have clean console output
        _spinner.Stop();

        var fileName = Path.GetFileName(relativePath);
        var isFolder = existingContent != null && existingContent.StartsWith("Folder:");

        if (isFolder)
        {
            // Folder deletion — render with rounded panel matching the Command
            // and code-snippet styling, but with a red border.
            var folderRows = new List<Spectre.Console.Rendering.IRenderable>();
            foreach (var line in existingContent!.Split('\n'))
            {
                folderRows.Add(new Markup(Markup.Escape(line.TrimEnd('\r'))));
            }

            var folderPanel = new Panel(new Rows(folderRows))
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("red"))
                .Padding(1, 0);
            folderPanel.Header = new PanelHeader($"[red] Delete Folder: {Markup.Escape(relativePath)}/ [/]", Justify.Left);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(folderPanel);
            AnsiConsole.WriteLine();
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

        var delChoices = new List<ApprovalSelectCoordinator.Option>
        {
            new(ApproveDeletionLabel, ApproveColor),
            new(ApproveNoAskDeleteLabel, ApproveColor),
            new(DenyLabel, RedirectColor),
            new(ProvideInstructionsLabel, RedirectColor)
        };
        if (_planHandoff.IsExecuting) delChoices.Add(new(CancelPlanLabel, DestructiveColor));

        // Title to scrollback; the VDOM menu holds only the option rows. Suppress() still
        // gates App's Escape listener so it can't steal keys from the keyboard pump.
        AnsiConsole.MarkupLine("[deepskyblue1]Delete this file?[/]");

        DiffApprovalResult result;
        using (_keyCoordinator.Suppress())
        {
            var approvalChoice = await _approvalSelect.RequestAsync(delChoices);

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
        var fullPath = Path.GetFullPath(Path.Combine(_projectRoot.ProjectRoot, relativePath));
        var fileUri = new Uri(fullPath).AbsoluteUri;
        var escapedPath = Markup.Escape(relativePath);
        var summaryLabel = isNewFile ? " (new file)" : "";
        var summaryLine = $"{deletions} deletion(s), {additions} addition(s){summaryLabel}";

        var rows = new List<IRenderable>();
        foreach (var line in displayLines)
        {
            string num;
            string markup;
            switch (line.LineType)
            {
                case DiffLineType.Removed:
                    num = line.OldLineNumber.HasValue ? $"{line.OldLineNumber,4}" : "    ";
                    markup = $"[red]{num} - {Markup.Escape(line.Content)}[/]";
                    break;
                case DiffLineType.Added:
                    num = line.NewLineNumber.HasValue ? $"{line.NewLineNumber,4}" : "    ";
                    markup = $"[rgb(135,206,250)]{num} + {Markup.Escape(line.Content)}[/]";
                    break;
                default:
                    num = line.OldLineNumber.HasValue ? $"{line.OldLineNumber,4}" : "    ";
                    markup = $"[dim]{num}   {Markup.Escape(line.Content)}[/]";
                    break;
            }
            rows.Add(new Markup(markup));
        }

        rows.Add(new Markup(string.Empty));
        rows.Add(new Markup($"[dim]{Markup.Escape(summaryLine)}[/]"));

        var diffPanel = new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);
        diffPanel.Header = new PanelHeader($"[deepskyblue1] Diff: [link={fileUri}]{escapedPath}[/] [/]", Justify.Left);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(diffPanel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Prompt UI for first-call MCP tool approvals. Wired into <see cref="McpApprovalGate"/>
    /// via App.razor. The gate remembers "approve for session" decisions itself, so this
    /// handler only renders the prompt for genuinely new (server, tool) pairs.
    /// </summary>
    public async Task<DiffApprovalResult> HandleMcpApproval(string serverName, string toolName, string? description)
    {
        // Serialized — see the note in HandleDiffApproval.
        using var promptHold = await _promptGate.AcquireAsync();

        _spinner.Stop();

        var title = $"[deepskyblue1]Allow MCP tool \"{Markup.Escape(toolName)}\" from \"{Markup.Escape(serverName)}\"?[/]";
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
            return new DiffApprovalResult { Response = DiffApprovalResponse.Approved };
        }

        // Embeds the tool name, so escape it — ApprovalSelect parses option text as markup
        // and returns it verbatim for the comparison below.
        var noAskMcpLabel = $"Approve - don't ask again for {Spectre.Console.Markup.Escape(toolName)} this session";
        var mcpChoices = new List<ApprovalSelectCoordinator.Option>
        {
            new(ApproveLabel, ApproveColor),
            new(noAskMcpLabel, ApproveColor),
            new(DenyLabel, RedirectColor)
        };
        if (_planHandoff.IsExecuting) mcpChoices.Add(new(CancelPlanLabel, DestructiveColor));

        // Title to scrollback; the VDOM menu holds only the option rows. Suppress() still
        // gates App's Escape listener so it can't steal keys from the keyboard pump.
        AnsiConsole.MarkupLine("[deepskyblue1]Run this MCP tool?[/]");

        string choice;
        using (_keyCoordinator.Suppress())
        {
            choice = await _approvalSelect.RequestAsync(mcpChoices);
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
        return result;
    }
}
