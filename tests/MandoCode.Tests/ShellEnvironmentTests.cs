using Xunit;
using System.Runtime.InteropServices;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Validates that ShellEnvironment produces sensible, OS-appropriate rules.
/// The rules string gets appended to the system prompt, so it must be non-empty
/// and mention the actual shell/OS the code will run in.
/// </summary>
public class ShellEnvironmentTests
{
    [Fact]
    public void Label_NonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ShellEnvironment.Label));
    }

    [Fact]
    public void SystemPromptRules_NonEmptyAndMentionsShell()
    {
        var rules = ShellEnvironment.SystemPromptRules;
        Assert.False(string.IsNullOrWhiteSpace(rules));
        Assert.Contains("execute_command", rules);
    }

    [Fact]
    public void WindowsLabel_MentionsCmd()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        Assert.Contains("cmd", ShellEnvironment.Label, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsRules_WarnAgainstUnixTools()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var rules = ShellEnvironment.SystemPromptRules;
        // The rules must explicitly call out that unix tools don't exist on cmd
        Assert.Contains("cmd.exe", rules);
        Assert.Contains("head", rules);
        Assert.Contains("grep", rules);
    }

    [Fact]
    public void UnixRules_AllowPosixTools()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var rules = ShellEnvironment.SystemPromptRules;
        Assert.Contains("bash", rules);
    }
}
