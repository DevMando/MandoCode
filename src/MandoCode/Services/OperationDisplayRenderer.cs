using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Renders Claude-Code-style operation displays with tree connectors,
/// content previews, and inline diffs.
/// </summary>
public class OperationDisplayRenderer
{
    private readonly ProjectRootAccessor _projectRoot;
    private readonly MandoCodeConfig _config;
    private readonly TokenTrackingService _tokenTracker;

    public OperationDisplayRenderer(ProjectRootAccessor projectRoot, MandoCodeConfig config, TokenTrackingService tokenTracker)
    {
        _projectRoot = projectRoot;
        _config = config;
        _tokenTracker = tokenTracker;
    }

    private string FileLink(string relativePath) =>
        FileLinkHelper.FileLink(_projectRoot.ProjectRoot, relativePath);

    /// <summary>
    /// Dispatches rendering based on operation type.
    /// </summary>
    public void Render(OperationDisplayEvent e)
    {
        Console.WriteLine();

        switch (e.OperationType)
        {
            case "Write":
                RenderWriteDisplay(e);
                break;

            case "Update":
                RenderUpdateDisplay(e);
                break;

            case "Read":
                Console.WriteLine($"\u001b[1m● Read(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                if (_config.EnableTokenTracking && _tokenTracker.LastOperation is { IsEstimate: true } readOp
                    && readOp.OperationLabel.StartsWith("Read"))
                {
                    Console.WriteLine($"  ⎿  \u001b[2mRead {e.LineCount} lines ~{TokenTrackingService.FormatTokenCount(readOp.PromptTokens)} tokens\u001b[0m");
                }
                else
                {
                    Console.WriteLine($"  ⎿  \u001b[2mRead {e.LineCount} lines\u001b[0m");
                }
                break;

            case "Delete":
                Console.WriteLine($"\u001b[1m● Delete(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                Console.WriteLine($"  ⎿  \u001b[2mDeleted {e.LineCount} lines\u001b[0m");
                break;

            case "DeleteFolder":
                Console.WriteLine($"\u001b[1m● DeleteFolder(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                Console.WriteLine($"  ⎿  \u001b[2mDeleted folder and all contents\u001b[0m");
                break;

            case "CreateFolder":
                Console.WriteLine($"\u001b[1m● CreateFolder(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                Console.WriteLine($"  ⎿  \u001b[2mCreated directory\u001b[0m");
                break;

            case "List":
                Console.WriteLine($"\u001b[2m● Listed project files\u001b[0m");
                break;

            case "Glob":
                Console.WriteLine($"\u001b[2m● Glob(\u001b[0m{FileLink(e.FilePath)}\u001b[2m)\u001b[0m");
                break;

            case "Search":
                Console.WriteLine($"\u001b[2m● Search(\u001b[0m\"{FileLink(e.FilePath)}\"\u001b[2m)\u001b[0m");
                break;
        }
    }

    /// <summary>
    /// Renders a new file write: header, line count, content preview, remaining lines.
    /// </summary>
    private void RenderWriteDisplay(OperationDisplayEvent e)
    {
        Console.WriteLine($"\u001b[1m● Write(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
        Console.WriteLine($"  ⎿  \u001b[2mWrote {e.LineCount} lines to {FileLink(e.FilePath)}\u001b[0m");

        // Show content preview unless the user already reviewed the diff via approval
        if (!e.ApprovalWasShown && !string.IsNullOrEmpty(e.ContentPreview))
        {
            foreach (var line in e.ContentPreview.Split('\n'))
            {
                var display = line.Length > 120 ? line[..120] + "..." : line;
                Console.WriteLine($"\u001b[2m     {display}\u001b[0m");
            }

            if (e.RemainingLines > 0)
            {
                Console.WriteLine($"\u001b[2m     … +{e.RemainingLines} lines\u001b[0m");
            }
        }
    }

    /// <summary>
    /// Renders an update to an existing file: header, change summary, inline diff.
    /// </summary>
    private void RenderUpdateDisplay(OperationDisplayEvent e)
    {
        Console.WriteLine($"\u001b[1m● Update(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");

        // Build summary line: "Added N lines" / "Removed N lines" / "Added N, removed M lines"
        var parts = new List<string>();
        if (e.Additions > 0) parts.Add($"Added {e.Additions} line{(e.Additions != 1 ? "s" : "")}");
        if (e.Deletions > 0) parts.Add($"Removed {e.Deletions} line{(e.Deletions != 1 ? "s" : "")}");
        Console.WriteLine($"  ⎿  \u001b[2m{string.Join(", ", parts)}\u001b[0m");

        // Show inline diff unless the user already reviewed via approval
        if (!e.ApprovalWasShown && e.InlineDiff != null)
        {
            foreach (var d in e.InlineDiff)
            {
                var num = (d.NewLineNumber ?? d.OldLineNumber)?.ToString() ?? "";
                var padNum = num.PadLeft(4);
                var content = d.Content.Length > 120 ? d.Content[..120] + "..." : d.Content;

                switch (d.LineType)
                {
                    case DiffLineType.Added:
                        Console.WriteLine($"\u001b[32m      {padNum} + {content}\u001b[0m");
                        break;
                    case DiffLineType.Removed:
                        Console.WriteLine($"\u001b[31m      {padNum} - {content}\u001b[0m");
                        break;
                    case DiffLineType.Unchanged:
                        Console.WriteLine($"\u001b[2m      {padNum}   {content}\u001b[0m");
                        break;
                }
            }
        }
    }
}
