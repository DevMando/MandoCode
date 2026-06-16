using Xunit;
using ArdinCode.Services;

namespace ArdinCode.Tests;

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

    [Theory]
    // Static file servers — the exact observed loop (`python -m http.server` to "test" a game).
    [InlineData("python -m http.server")]
    [InlineData("python -m http.server 8000")]
    [InlineData("python3 -m http.server 8000")]
    [InlineData("http-server -p 8080")]
    [InlineData("npx serve")]
    [InlineData("live-server")]
    // Caught even behind a `cd X && ` prefix (matched anywhere, not anchored).
    [InlineData("cd StarFox && python -m http.server 8000")]
    [InlineData("cd StarFox && python -m http.server 8000 > server.log 2>&1 & echo started")]
    // Node/JS dev servers and watchers.
    [InlineData("npm start")]
    [InlineData("npm run dev")]
    [InlineData("yarn dev")]
    [InlineData("pnpm run serve")]
    [InlineData("vite")]
    [InlineData("vite preview")]
    [InlineData("next dev")]
    [InlineData("ng serve")]
    [InlineData("webpack serve")]
    [InlineData("webpack-dev-server")]
    // Other ecosystems.
    [InlineData("flask run")]
    [InlineData("php -S localhost:8000")]
    [InlineData("dotnet watch")]
    [InlineData("python manage.py runserver")]
    [InlineData("rails server")]
    [InlineData("hugo server -D")]
    // Wrapped — inner command is a server.
    [InlineData("powershell -Command \"npm run dev\"")]
    public void LooksLikeLongRunningCommand_BlocksServers(string command)
    {
        Assert.True(FunctionInvocationFilter.LooksLikeLongRunningCommand(command));
    }

    [Theory]
    // One-shot commands that exit on their own — must NOT be refused.
    [InlineData("git status")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("dotnet run")] // deliberately allowed — often a short-lived console app
    [InlineData("npm install")]
    [InlineData("npm run build")]
    [InlineData("vite build")] // the one-shot subcommand, not the dev server
    [InlineData("yarn build")]
    [InlineData("ng build")]
    [InlineData("ls")]
    [InlineData("echo serve the build")] // 'serve' as prose, not the command
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeLongRunningCommand_AllowsOneShotCommands(string command)
    {
        Assert.False(FunctionInvocationFilter.LooksLikeLongRunningCommand(command));
    }
}
