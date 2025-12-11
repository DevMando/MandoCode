using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace MandoCode.Plugins;

/// <summary>
/// Provides Git operations for the AI assistant.
/// </summary>
public class GitPlugin
{
    private readonly string _projectRoot;

    public GitPlugin(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>
    /// Gets the current git status.
    /// </summary>
    [KernelFunction]
    [Description("Shows the current git status including modified, staged, and untracked files.")]
    public async Task<string> GitStatus()
    {
        return await RunGitCommand("status");
    }

    /// <summary>
    /// Shows the git diff for uncommitted changes.
    /// </summary>
    [KernelFunction]
    [Description("Shows the diff for uncommitted changes in the working directory.")]
    public async Task<string> GitDiff()
    {
        return await RunGitCommand("diff");
    }

    /// <summary>
    /// Shows the git diff for staged changes.
    /// </summary>
    [KernelFunction]
    [Description("Shows the diff for staged changes that will be included in the next commit.")]
    public async Task<string> GitDiffStaged()
    {
        return await RunGitCommand("diff --staged");
    }

    /// <summary>
    /// Commits staged changes with a message.
    /// </summary>
    [KernelFunction]
    [Description("Commits all staged changes with the provided commit message. Make sure files are staged first.")]
    public async Task<string> GitCommit(
        [Description("The commit message describing the changes")] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Error: Commit message cannot be empty.";
        }

        return await RunGitCommand($"commit -m \"{message}\"");
    }

    /// <summary>
    /// Stages a file for commit.
    /// </summary>
    [KernelFunction]
    [Description("Stages a file for the next commit. Use relative path from project root.")]
    public async Task<string> GitAdd(
        [Description("Relative path to the file to stage")] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: File path cannot be empty.";
        }

        return await RunGitCommand($"add \"{filePath}\"");
    }

    /// <summary>
    /// Shows the git log.
    /// </summary>
    [KernelFunction]
    [Description("Shows the commit history. Optionally specify the number of commits to show (default is 10).")]
    public async Task<string> GitLog(
        [Description("Number of commits to show (default: 10)")] int count = 10)
    {
        return await RunGitCommand($"log --oneline -n {count}");
    }

    private async Task<string> RunGitCommand(string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = _projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return string.IsNullOrWhiteSpace(error)
                    ? $"Git command failed with exit code {process.ExitCode}"
                    : $"Git error: {error}";
            }

            return string.IsNullOrWhiteSpace(output)
                ? "Command executed successfully (no output)"
                : output;
        }
        catch (Exception ex)
        {
            return $"Error running git command: {ex.Message}";
        }
    }
}
