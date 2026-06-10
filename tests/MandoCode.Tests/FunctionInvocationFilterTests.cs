using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests the shell-file-read classifier used to steer execute_command callers back to
/// read_file_contents. The classifier protects the chat history from unbounded content
/// dumps via type/cat/head/findstr/grep — see the shell-read circuit in
/// FunctionInvocationFilter.OnFunctionInvocationAsync.
/// </summary>
public class FunctionInvocationFilterTests
{
    [Theory]
    // cmd.exe verbs.
    [InlineData("type visualizer.js")]
    [InlineData("TYPE foo.cs")]
    [InlineData("findstr /n \"switch\" visualizer.js")]
    [InlineData("more +555 visualizer.js")]
    // bash verbs.
    [InlineData("cat foo.cs")]
    [InlineData("cat -n foo.cs")]
    [InlineData("head -100 README.md")]
    [InlineData("tail -f server.log")]
    [InlineData("less LICENSE")]
    [InlineData("nl src/main.c")]
    [InlineData("grep -r 'pattern' .")]
    [InlineData("sed -n '1,100p' file.cs")]
    [InlineData("awk '{print}' data.csv")]
    // PowerShell verbs and aliases.
    [InlineData("Get-Content file.txt")]
    [InlineData("gc *.cs")]
    [InlineData("Select-String -Pattern foo file.txt")]
    [InlineData("sls foo *.cs")]
    // Leading whitespace tolerated.
    [InlineData("  cat foo.cs")]
    // Wrapped reads — a stuck model used these to dump whole files past the bare-verb check,
    // bloating context. The inner command leads with a read verb, so they must be blocked.
    [InlineData("powershell -Command \"Get-Content game.js\"")]
    [InlineData("powershell -Command \"Get-Content game.js | Select-Object -Skip 150 | Select-Object -First 300\"")]
    [InlineData("powershell -NoProfile -Command \"gc game.js\"")]
    [InlineData("pwsh -c 'cat game.js'")]
    [InlineData("cmd /c type game.js")]
    public void LooksLikeShellFileRead_BlocksKnownReaders(string command)
    {
        Assert.True(FunctionInvocationFilter.LooksLikeShellFileRead(command));
    }

    [Theory]
    // Legitimate non-read commands — must not be blocked.
    [InlineData("git status")]
    [InlineData("git diff")]
    [InlineData("git log -p")] // dumps content but via git — model intent is usually clear
    [InlineData("dotnet build")]
    [InlineData("npm install")]
    [InlineData("mkdir foo")]
    [InlineData("echo hello")]
    [InlineData("curl https://example.com")]
    // Word-boundary guards — these must not match the read verbs as prefixes.
    [InlineData("typescript --version")]
    [InlineData("category-tool list")]
    [InlineData("grepper-cli search")]
    // Piped reads — the leading verb is what counts. `git status | grep modified` is a
    // legit filter on git's stdout, not a file dump.
    [InlineData("git status | grep modified")]
    [InlineData("dotnet build | findstr error")]
    // Wrapped NON-reads — the inner command doesn't lead with a read verb, so the wrapper
    // detection must not block these.
    [InlineData("powershell -Command \"dotnet build\"")]
    [InlineData("cmd /c dotnet test")]
    [InlineData("powershell -Command \"(Get-Content x).Count\"")] // returns a count, not a dump
    // Empty and whitespace inputs.
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeShellFileRead_AllowsNonReaders(string command)
    {
        Assert.False(FunctionInvocationFilter.LooksLikeShellFileRead(command));
    }
}
