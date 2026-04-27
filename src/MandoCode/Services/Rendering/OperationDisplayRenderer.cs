using System.Text;
using MandoCode.Models;
using MandoCode.Translators;
using Spectre.Console;
using Spectre.Console.Rendering;

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
        AnsiConsole.Write(BuildRenderable(e));
    }

    /// <summary>
    /// Builds the full operation display as an IRenderable without writing to the console.
    /// </summary>
    public IRenderable BuildRenderable(OperationDisplayEvent e)
    {
        var sb = new StringBuilder();
        sb.AppendLine();

        switch (e.OperationType)
        {
            case "Write":
                BuildWriteDisplay(sb, e);
                break;

            case "Update":
                BuildUpdateDisplay(sb, e);
                break;

            case "Read":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mRead(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                if (_config.EnableTokenTracking && _tokenTracker.LastOperation is { IsEstimate: true } readOp
                    && readOp.OperationLabel.StartsWith("Read"))
                {
                    sb.AppendLine($"  ⎿  \u001b[2mRead {e.LineCount} lines ~{TokenTrackingService.FormatTokenCount(readOp.PromptTokens)} tokens\u001b[0m");
                }
                else
                {
                    sb.AppendLine($"  ⎿  \u001b[2mRead {e.LineCount} lines\u001b[0m");
                }
                break;

            case "Delete":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mDelete(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                sb.AppendLine($"  ⎿  \u001b[2mDeleted {e.LineCount} lines\u001b[0m");
                break;

            case "DeleteFolder":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mDeleteFolder(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                sb.AppendLine($"  ⎿  \u001b[2mDeleted folder and all contents\u001b[0m");
                break;

            case "CreateFolder":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mCreateFolder(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
                sb.AppendLine($"  ⎿  \u001b[2mCreated directory\u001b[0m");
                break;

            case "List":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[2mListed project files\u001b[0m");
                break;

            case "Glob":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[2mGlob(\u001b[0m{e.FilePath}\u001b[2m)\u001b[0m");
                break;

            case "Search":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[2mSearch(\u001b[0m\"{e.FilePath}\"\u001b[2m)\u001b[0m");
                break;

            case "Command":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mCommand(\u001b[0m{e.FilePath}\u001b[1m)\u001b[0m");
                if (!e.ApprovalWasShown && !string.IsNullOrEmpty(e.ContentPreview))
                {
                    AppendPreviewLines(sb, e.ContentPreview, 10);
                }
                break;

            case "WebSearch":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mWebSearch(\u001b[0m\"{e.FilePath}\"\u001b[1m)\u001b[0m");
                if (!string.IsNullOrEmpty(e.ContentPreview))
                {
                    AppendPreviewLines(sb, e.ContentPreview, 5);
                }
                break;

            case "WebFetch":
                sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mWebFetch(\u001b[0m{e.FilePath}\u001b[1m)\u001b[0m");
                if (!string.IsNullOrEmpty(e.ContentPreview))
                {
                    AppendPreviewLines(sb, e.ContentPreview, 5);
                }
                break;
        }

        return new AnsiPassthroughRenderable(sb.ToString());
    }

    /// <summary>
    /// Appends truncated preview lines with an overflow indicator.
    /// </summary>
    private static void AppendPreviewLines(StringBuilder sb, string contentPreview, int maxLines)
    {
        var lines = contentPreview.Split('\n');
        var count = Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
        {
            var display = lines[i].Length > 120 ? lines[i][..120] + "..." : lines[i];
            sb.AppendLine($"\u001b[2m     {display}\u001b[0m");
        }
        if (lines.Length > count)
        {
            sb.AppendLine($"\u001b[2m     … +{lines.Length - count} lines\u001b[0m");
        }
    }

    /// <summary>
    /// Builds a new file write display: header, line count, content preview, remaining lines.
    /// </summary>
    private void BuildWriteDisplay(StringBuilder sb, OperationDisplayEvent e)
    {
        sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mWrite(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");
        sb.AppendLine($"  ⎿  \u001b[2mWrote {e.LineCount} lines to {FileLink(e.FilePath)}\u001b[0m");

        // Show content preview unless the user already reviewed the diff via approval
        if (!e.ApprovalWasShown && !string.IsNullOrEmpty(e.ContentPreview))
        {
            foreach (var line in e.ContentPreview.Split('\n'))
            {
                var display = line.Length > 120 ? line[..120] + "..." : line;
                sb.AppendLine($"\u001b[2m     {display}\u001b[0m");
            }

            if (e.RemainingLines > 0)
            {
                sb.AppendLine($"\u001b[2m     … +{e.RemainingLines} lines\u001b[0m");
            }
        }
    }

    /// <summary>
    /// Builds an update display for an existing file: header, change summary, inline diff.
    /// </summary>
    private void BuildUpdateDisplay(StringBuilder sb, OperationDisplayEvent e)
    {
        sb.AppendLine($"\u001b[32m●\u001b[0m \u001b[1mUpdate(\u001b[0m{FileLink(e.FilePath)}\u001b[1m)\u001b[0m");

        // Build summary line: "Added N lines" / "Removed N lines" / "Added N, removed M lines"
        var parts = new List<string>();
        if (e.Additions > 0) parts.Add($"Added {e.Additions} line{(e.Additions != 1 ? "s" : "")}");
        if (e.Deletions > 0) parts.Add($"Removed {e.Deletions} line{(e.Deletions != 1 ? "s" : "")}");
        sb.AppendLine($"  ⎿  \u001b[2m{string.Join(", ", parts)}\u001b[0m");

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
                        sb.AppendLine($"\u001b[32m      {padNum} + {content}\u001b[0m");
                        break;
                    case DiffLineType.Removed:
                        sb.AppendLine($"\u001b[31m      {padNum} - {content}\u001b[0m");
                        break;
                    case DiffLineType.Unchanged:
                        sb.AppendLine($"\u001b[2m      {padNum}   {content}\u001b[0m");
                        break;
                }
            }
        }
    }
}
