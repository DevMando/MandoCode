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
    private string ProcessFileReferences(string input)
    {
        var matches = FileReferenceToken.Paths(input).ToArray();

        if (matches.Length == 0)
            return input;

        var contextBlocks = new System.Text.StringBuilder();
        var referencedFiles = new HashSet<string>();
        int totalExpansionChars = 0;
        int skippedForBudget = 0;

        foreach (var filePath in matches)
        {
            if (Pane is not null && CliAgentDirectory.IsAgentName(filePath) && new CliAgentDirectory(Pane).Resolve(filePath) is not null) continue;

            // Skip duplicates
            if (!referencedFiles.Add(filePath))
                continue;

            if (ImageFileReference.IsImage(filePath))
            {
                try
                {
                    var clipboard = (Pane?.Services ?? Services).GetService<ClipboardImageStore>();
                    var image = clipboard?.TryGet(filePath, out var pasted) == true
                        ? (Bytes: pasted, MediaType: "image/png")
                        : ImageFileReference.Read(ProjectRoot.ProjectRoot, filePath);
                    if (!AI.TryAttachImage(image.Bytes, image.MediaType, $"Referenced image: {filePath}", out var error))
                        throw new IOException(error);
                    contextBlocks.AppendLine($"Image: {filePath} — attached as image input. Inspect the supplied image directly, rather than reading its binary bytes as text.");
                    TuiConsole.MarkupLine($"📷  {Spectre.Console.Markup.Escape(filePath)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    contextBlocks.AppendLine($"Image: {filePath} — not attached: {ex.Message} Do not attempt to infer the image contents from raw bytes.");
                    TuiConsole.MarkupLine($"[yellow]{Spectre.Console.Markup.Escape($"Image not attached: {filePath}: {ex.Message}")}[/]");
                }
                continue;
            }

            // Total-budget check — stop expanding once we've accumulated enough context.
            if (totalExpansionChars >= MaxFileReferenceBudgetChars)
            {
                skippedForBudget++;
                continue;
            }

            var content = FileProvider.ReadFileContent(filePath);
            if (content != null)
            {
                // Truncate files over 10,000 chars
                if (content.Length > 10_000)
                    content = content.Substring(0, 10_000) + "\n... [truncated]";

                contextBlocks.AppendLine($"File: {filePath}");
                contextBlocks.AppendLine("```");
                contextBlocks.AppendLine(content);
                contextBlocks.AppendLine("```");
                contextBlocks.AppendLine();
                totalExpansionChars += content.Length;

                // Show consistent operation display for @file reads
                var lineCount = content.Split('\n').Length;
                var readDisplay = new OperationDisplayEvent
                {
                    OperationType = "Read",
                    FilePath = filePath,
                    LineCount = lineCount
                };
                OperationRenderer.Render(readDisplay);
            }
            else
            {
                // Check if this is a directory reference
                var dirListing = FileProvider.GetDirectoryListing(filePath);
                if (dirListing != null)
                {
                    // Dir listings can be huge for big projects — cap at 4k chars per reference.
                    if (dirListing.Length > 4_000)
                        dirListing = dirListing.Substring(0, 4_000) + "\n... [truncated]";

                    contextBlocks.AppendLine($"Directory: {filePath}/");
                    contextBlocks.AppendLine(FileAutocompleteProvider.BuildDirectoryScopeInstruction(filePath));
                    contextBlocks.AppendLine("Contents:");
                    contextBlocks.AppendLine(dirListing);
                    contextBlocks.AppendLine();
                    totalExpansionChars += dirListing.Length;

                    TuiConsole.MarkupLine($"[dim][[Directory]][/] [deepskyblue1]{Spectre.Console.Markup.Escape(filePath)}/[/]");
                }
                else
                {
                    TuiConsole.MarkupLine($"[dim][[Not found]][/] [yellow]{Spectre.Console.Markup.Escape(filePath)}[/]");
                }
            }
        }

        if (contextBlocks.Length == 0)
            return input;

        if (skippedForBudget > 0)
        {
            contextBlocks.AppendLine($"[Note: {skippedForBudget} additional @reference(s) were skipped — the total expansion budget of {MaxFileReferenceBudgetChars:N0} chars was reached. Re-reference specific files in follow-up messages if needed.]");
            contextBlocks.AppendLine();
            TuiConsole.MarkupLine($"[yellow][[Skipped]][/] [dim]{skippedForBudget} @reference(s) beyond expansion budget ({MaxFileReferenceBudgetChars:N0} chars)[/]");
        }

        var fullContext = new System.Text.StringBuilder();
        fullContext.AppendLine("--- Referenced Files ---");
        fullContext.Append(contextBlocks);
        fullContext.AppendLine("---");
        fullContext.AppendLine();
        fullContext.Append(input);

        return fullContext.ToString();
    }

}
