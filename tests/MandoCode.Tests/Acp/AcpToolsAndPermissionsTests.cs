using System.Diagnostics;
using MandoCode.Acp;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MandoCode.Tests.Acp;

internal static class Plain
{
    public static string Of(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        console.Write(renderable);
        return writer.ToString().Replace("\r\n", "\n").TrimEnd();
    }
}

/// <summary>Creates links for the fs guard tests; null means this machine may not create that kind of link.</summary>
internal static class Links
{
    public static bool TryDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
                return false;
        }

        // Windows without the symlink privilege: a junction needs none and is what the guard must handle as well.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>Deletes a folder without following links out of it (a junction would make a recursive delete fail or reach outside).</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                directory.Delete(recursive: false);
            else
                DeleteTree(directory.FullName);
        }

        Directory.Delete(path, recursive: true);
    }

    public static bool TryFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

public class AcpFileSystemHandlerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mandocode-acp-fs-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly List<AcpFileChange> _changes = [];
    private readonly AcpFileSystemHandler _fs;

    public AcpFileSystemHandlerTests()
    {
        _root = Path.Combine(_base, "project");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
        _fs = new AcpFileSystemHandler(_root, _changes.Add);
    }

    public void Dispose()
    {
        try { Links.DeleteTree(_base); } catch (IOException) { }
    }

    private ReadTextFileResponse Read(string path, int? line = null, int? limit = null) =>
        _fs.Read(new ReadTextFileRequest { SessionId = "s", Path = path, Line = line, Limit = limit });

    private void Write(string path, string content) =>
        _fs.Write(new WriteTextFileRequest { SessionId = "s", Path = path, Content = content });

    private string InRoot(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void AssertRefused(Action action, string fragment)
    {
        var ex = Assert.Throws<JsonRpcException>(action);
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void ARelativePath_IsRefused()
    {
        File.WriteAllText(InRoot("a.txt"), "x");

        AssertRefused(() => Read("a.txt"), "absolute");
        AssertRefused(() => Read(Path.Combine("sub", "a.txt")), "absolute");
        AssertRefused(() => Write("a.txt", "y"), "absolute");
    }

    [Fact]
    public void DotDot_ThatLeavesTheFolder_IsRefused()
    {
        AssertRefused(() => Read(Path.Combine(_root, "..", "outside", "secret.txt")), "outside");
        AssertRefused(() => Write(Path.Combine(_root, "..", "outside", "new.txt"), "x"), "outside");
        Assert.False(File.Exists(Path.Combine(_outside, "new.txt")));
    }

    [Fact]
    public void DotDot_ThatStaysInside_IsFine()
    {
        File.WriteAllText(InRoot("a.txt"), "inside");
        Directory.CreateDirectory(InRoot("sub"));

        Assert.Equal("inside", Read(Path.Combine(_root, "sub", "..", "a.txt")).Content);
    }

    [Fact]
    public void APathOutsideTheFolder_IsRefused_IncludingASiblingWithTheSamePrefix()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "x");

        AssertRefused(() => Read(Path.Combine(_outside, "secret.txt")), "outside");
        AssertRefused(() => Read(Path.Combine(sibling, "x.txt")), "outside");
        AssertRefused(() => Write(Path.Combine(sibling, "y.txt"), "y"), "outside");
    }

    [Fact]
    public void ASymlinkToAFileOutside_IsRefused()
    {
        if (!Links.TryFileLink(InRoot("link.txt"), Path.Combine(_outside, "secret.txt")))
            return; // this machine cannot create file symlinks (needs a privilege on Windows); the directory-link tests still cover the guard

        AssertRefused(() => Read(InRoot("link.txt")), "link");
        AssertRefused(() => Write(InRoot("link.txt"), "overwritten"), "link");
        Assert.Equal("secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void ASymlinkToAFileInside_IsAllowed()
    {
        File.WriteAllText(InRoot("real.txt"), "real");
        if (!Links.TryFileLink(InRoot("alias.txt"), InRoot("real.txt")))
            return;

        Assert.Equal("real", Read(InRoot("alias.txt")).Content);
    }

    [Fact]
    public void ALinkedParentDirectoryLeadingOutside_IsRefused_ForReadsAndForWrites()
    {
        Assert.True(Links.TryDirectoryLink(InRoot("escape"), _outside), "could not create a directory link or junction");

        AssertRefused(() => Read(InRoot("escape", "secret.txt")), "link");
        AssertRefused(() => Write(InRoot("escape", "planted.txt"), "x"), "link");
        AssertRefused(() => Write(InRoot("escape", "newdir", "planted.txt"), "x"), "link");
        Assert.False(File.Exists(Path.Combine(_outside, "planted.txt")));
        Assert.False(Directory.Exists(Path.Combine(_outside, "newdir")));
    }

    [Fact]
    public void ALinkedParentDirectoryFurtherUp_IsRefused()
    {
        Directory.CreateDirectory(InRoot("a"));
        Assert.True(Links.TryDirectoryLink(InRoot("a", "b"), _outside), "could not create a directory link or junction");

        AssertRefused(() => Read(InRoot("a", "b", "secret.txt")), "link");
    }

    [Fact]
    public void ALinkedParentDirectoryThatStaysInside_IsAllowed()
    {
        Directory.CreateDirectory(InRoot("real"));
        File.WriteAllText(InRoot("real", "f.txt"), "inside");
        Assert.True(Links.TryDirectoryLink(InRoot("alias"), InRoot("real")), "could not create a directory link or junction");

        Assert.Equal("inside", Read(InRoot("alias", "f.txt")).Content);
    }

    [Fact]
    public void Read_ReturnsALineRange()
    {
        File.WriteAllText(InRoot("lines.txt"), "one\ntwo\nthree\nfour\n");

        Assert.Equal("two\nthree", Read(InRoot("lines.txt"), line: 2, limit: 2).Content);
        Assert.Equal("three\nfour", Read(InRoot("lines.txt"), line: 3).Content);
        Assert.Equal("one", Read(InRoot("lines.txt"), limit: 1).Content);
        Assert.Equal("", Read(InRoot("lines.txt"), line: 99).Content);
        Assert.Equal("one\ntwo\nthree\nfour\n", Read(InRoot("lines.txt")).Content);
    }

    [Fact]
    public void Read_RefusesABigFileWhole_ButServesARange()
    {
        var big = InRoot("big.txt");
        using (var writer = File.CreateText(big))
        {
            var line = new string('x', 99);
            for (var i = 0; i < 60_000; i++)
                writer.WriteLine(line);
        }

        Assert.True(new FileInfo(big).Length > AcpFileSystemHandler.MaxReadBytes);
        AssertRefused(() => Read(big), "limited");
        Assert.Equal(new string('x', 99), Read(big, line: 59_999, limit: 1).Content);
    }

    [Fact]
    public void Read_ReportsAMissingFileAndADirectory()
    {
        var missing = Assert.Throws<JsonRpcException>(() => Read(InRoot("nope.txt")));
        Assert.Equal(AcpErrorCodes.ResourceNotFound, missing.Code);

        Directory.CreateDirectory(InRoot("dir"));
        AssertRefused(() => Read(InRoot("dir")), "directory");
    }

    [Fact]
    public void Write_CreatesDirectoriesInsideTheFolder_AndReportsTheChange()
    {
        var path = InRoot("new", "deep", "file.txt");

        Write(path, "hello");

        Assert.Equal("hello", File.ReadAllText(path));
        var change = Assert.Single(_changes);
        Assert.Null(change.OldText);
        Assert.Equal("hello", change.NewText);
    }

    [Fact]
    public void Write_ReplacesAtomically_LeavesNoTempFile_AndReportsTheOldText()
    {
        File.WriteAllText(InRoot("f.txt"), "old\n");

        Write(InRoot("f.txt"), "new\n");

        Assert.Equal("new\n", File.ReadAllText(InRoot("f.txt")));
        Assert.Equal(["f.txt"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        var change = Assert.Single(_changes);
        Assert.Equal("old\n", change.OldText);
        Assert.Equal("new\n", change.NewText);
    }

    [Fact]
    public void Write_ToADirectory_IsRefused()
    {
        Directory.CreateDirectory(InRoot("dir"));

        AssertRefused(() => Write(InRoot("dir"), "x"), "directory");
    }
}

[Collection("AcpProcesses")]
public class AcpToolsAndPermissionsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
    private static readonly string MockDll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly ApprovalPromptGate _gate = new();
    private readonly ApprovalSelectCoordinator _menu = new();
    private readonly List<string> _lines = [];
    private string _mode = "default";

    public AcpToolsAndPermissionsTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private AcpPermissionPrompter Prompter() =>
        new(_gate, _menu, () => new Noop(), _lines.Add, () => _mode);

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }

    private AcpAgentRuntime Runtime(string scenario, AcpClientHandler handler, Action<AcpAgentConfig>? tweak = null)
    {
        var config = new AcpAgentConfig { Command = _host, Args = [MockDll, scenario], StartupTimeoutSeconds = 20 };
        tweak?.Invoke(config);
        var runtime = new AcpAgentRuntime("mock", config, _work, new AcpAgentLauncher(new InMemoryTrustStore()),
            _ => Task.FromResult(true), Path.Combine(_work, "logs"), handler);
        _cleanup.Add(runtime);
        return runtime;
    }

    private async Task<string> AnswerWhenShown(string? choice)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_menu.IsActive && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.True(_menu.IsActive, "the permission menu was never shown");
        var shown = string.Join('|', _menu.Options.Select(o => o.Text));
        if (choice is null)
            _menu.Cancel();
        else
            _menu.Submit(choice);
        return shown;
    }

    private static async Task<string> ReplyAsync(IAcpAgentRuntime runtime, string text)
    {
        var turn = runtime.Prompt([new TextContent(text)]);
        var reply = new System.Text.StringBuilder();
        await foreach (var update in turn.Updates)
            if (update is AgentMessageChunk { Content: TextContent t })
                reply.Append(t.Text);
        await turn.Completion;
        return reply.ToString();
    }

    // ───────── permissions ─────────

    [Fact]
    public async Task ChoosingAnOption_AnswersWithItsOptionId_AndTheAgentsOptionsAreShownAsTheyCame()
    {
        var runtime = Runtime("permission", new AcpClientHandler(Prompter(), null));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        var reply = ReplyAsync(runtime, "go");
        var shown = await AnswerWhenShown("Allow");

        Assert.Equal("Allow|Reject", shown);
        Assert.Equal("permission: selected:allow", await reply.Within(20));
    }

    [Fact]
    public async Task RejectingAnOption_AnswersWithTheRejectOptionId()
    {
        var runtime = Runtime("permission", new AcpClientHandler(Prompter(), null));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        var reply = ReplyAsync(runtime, "go");
        await AnswerWhenShown("Reject");

        Assert.Equal("permission: selected:reject", await reply.Within(20));
    }

    [Fact]
    public async Task ClosingTheMenu_AnswersCancelled()
    {
        var runtime = Runtime("permission", new AcpClientHandler(Prompter(), null));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        var reply = ReplyAsync(runtime, "go");
        await AnswerWhenShown(choice: null);

        Assert.Equal("permission: cancelled", await reply.Within(20));
        Assert.False(_menu.IsActive);
    }

    [Fact]
    public async Task CancellingTheTurn_ClosesTheMenu_AndAnswersCancelled()
    {
        using var cts = new CancellationTokenSource();
        var prompter = Prompter();
        var request = new RequestPermissionRequest
        {
            SessionId = "s",
            ToolCall = new ToolCallUpdate { ToolCallId = "c", Title = "Edit file" },
            Options = [new PermissionOption { OptionId = "a", Name = "Allow", Kind = PermissionOptionKinds.AllowOnce }],
        };

        var answer = prompter.RequestAsync(request, cts.Token).AsTask();
        await AnswerShown();
        cts.Cancel();

        Assert.IsType<CancelledPermissionOutcome>((await answer.Within()).Outcome);
        Assert.False(_menu.IsActive);

        async Task AnswerShown()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_menu.IsActive && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.True(_menu.IsActive);
        }
    }

    [Fact]
    public async Task TheDialogSaysWhoAppliesThePermission_AndShowsTheMode()
    {
        _mode = "acceptEdits";
        var prompter = Prompter();
        var request = new RequestPermissionRequest
        {
            SessionId = "s",
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = "c", Title = "Edit config", Kind = ToolKinds.Edit,
                Locations = [new ToolCallLocation { Path = "/work/config.json", Line = 4 }],
                Content = [new DiffToolCallContent { Path = "/work/config.json", OldText = "a\n", NewText = "b\n" }],
            },
            Options = [new PermissionOption { OptionId = "a", Name = "Allow", Kind = PermissionOptionKinds.AllowOnce }],
        };

        var answer = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        await AnswerWhenShown("Allow");
        await answer.Within();

        var text = string.Join('\n', _lines);
        Assert.Contains("The agent asks for permission (edit): Edit config", text);
        Assert.Contains("/work/config.json:4", text);
        Assert.Contains("Agent mode: acceptEdits", text);
        Assert.Contains("The agent applies your answer itself", text);
    }

    [Fact]
    public async Task TheDiffOfTheProposedChange_IsThePreviewOfTheMenu()
    {
        var prompter = Prompter();
        var request = new RequestPermissionRequest
        {
            SessionId = "s",
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = "c",
                Content = [new DiffToolCallContent { Path = "/work/f.txt", OldText = "keep\nold\n", NewText = "keep\nnew\n" }],
            },
            Options = [new PermissionOption { OptionId = "a", Name = "Allow", Kind = PermissionOptionKinds.AllowOnce }],
        };

        var answer = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_menu.IsActive && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        Assert.Contains(_menu.Preview, l => l.Text.Contains("- old"));
        Assert.Contains(_menu.Preview, l => l.Text.Contains("+ new"));
        _menu.Submit("Allow");
        await answer.Within();
    }

    [Fact]
    public async Task OptionsWithTheSameName_AreToldApart_AndStillMapToTheirOwnIds()
    {
        var prompter = Prompter();
        var request = new RequestPermissionRequest
        {
            SessionId = "s",
            ToolCall = new ToolCallUpdate { ToolCallId = "c" },
            Options =
            [
                new PermissionOption { OptionId = "once", Name = "Allow", Kind = PermissionOptionKinds.AllowOnce },
                new PermissionOption { OptionId = "always", Name = "Allow", Kind = PermissionOptionKinds.AllowAlways },
            ],
        };

        var answer = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        var shown = await AnswerWhenShown("Allow (2)");

        Assert.Equal("Allow|Allow (2)", shown);
        Assert.Equal("always", ((SelectedPermissionOutcome)(await answer.Within()).Outcome).OptionId);
    }

    [Fact]
    public async Task ARequestWithoutOptions_IsCancelledWithoutAskingAnything()
    {
        var response = await Prompter().RequestAsync(new RequestPermissionRequest { SessionId = "s" }, CancellationToken.None).AsTask().Within();

        Assert.IsType<CancelledPermissionOutcome>(response.Outcome);
        Assert.False(_menu.IsActive);
    }

    [Fact]
    public async Task EscapeIsOnlyOfferedToMenusThatAskForIt()
    {
        var plain = _menu.RequestAsync([new ApprovalSelectCoordinator.Option("A", Color.Green)]);
        Assert.False(_menu.CancelOnEscape);
        _menu.Submit("A");
        Assert.Equal("A", await plain.Within());

        var cancellable = _menu.RequestAsync([new ApprovalSelectCoordinator.Option("A", Color.Green)], cancelOnEscape: true);
        Assert.True(_menu.CancelOnEscape);
        _menu.Cancel();
        Assert.Equal(ApprovalSelectCoordinator.Cancelled, await cancellable.Within());
        Assert.False(_menu.IsActive);
        Assert.False(_menu.CancelOnEscape);
    }

    // ───────── fs through the real protocol ─────────

    [Fact]
    public async Task Fs_IsAdvertisedOnlyWhenAHandlerIsConnected()
    {
        Assert.False(new AcpClientHandler(Prompter(), null).Capabilities.Fs.ReadTextFile);
        Assert.False(new AcpClientHandler(Prompter(), null).Capabilities.Fs.WriteTextFile);
        var withFs = new AcpClientHandler(null, new AcpFileSystemHandler(_work)).Capabilities.Fs;
        Assert.True(withFs.ReadTextFile);
        Assert.True(withFs.WriteTextFile);

        // Without a handler the agent gets "method not found" rather than a silent success.
        var ex = await Assert.ThrowsAsync<JsonRpcException>(async () =>
            await new AcpClientHandler(null, null).ReadTextFileAsync(new ReadTextFileRequest(), CancellationToken.None));
        Assert.Equal(AcpErrorCodes.MethodNotFound, ex.Code);
    }

    [Fact]
    public async Task AnAgentReadingAndWritingThroughFs_GoesThroughTheGuard_AndTheChangeIsReported()
    {
        var changes = new List<AcpFileChange>();
        var runtime = Runtime("fs-roundtrip", new AcpClientHandler(null, new AcpFileSystemHandler(_work, changes.Add)));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);
        var source = Path.Combine(_work, "in.txt");
        File.WriteAllText(source, "hello");

        var reply = await ReplyAsync(runtime, source).Within(20);

        Assert.Equal("read 5 chars, wrote copy", reply);
        Assert.Equal("HELLO", File.ReadAllText(source + ".out"));
        var change = Assert.Single(changes);
        Assert.Equal(source + ".out", change.Path);
        Assert.Equal("HELLO", change.NewText);
    }

    [Fact]
    public async Task AnAgentReadingOutsideTheFolder_GetsAnErrorNotTheFile()
    {
        var outside = Path.Combine(Path.GetTempPath(), "mandocode-acp-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var runtime = Runtime("fs-roundtrip", new AcpClientHandler(null, new AcpFileSystemHandler(_work)));
            await runtime.ConnectAsync(CancellationToken.None).Within(40);

            var reply = await ReplyAsync(runtime, outside).Within(20);

            Assert.StartsWith("read failed:", reply);
            Assert.Contains("outside the project folder", reply);
            Assert.DoesNotContain("secret", reply);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ───────── transcript: tool calls, plan, diff ─────────

    private AcpTranscriptAdapter Adapter(List<OperationDisplayEvent>? operations = null) =>
        new(e =>
        {
            operations?.Add(e);
            return new Text($"OP {e.OperationType} {e.FilePath} +{e.Additions} -{e.Deletions}");
        }, "/work");

    [Fact]
    public async Task ToolCalls_Plan_AndDiff_AreRenderedFromTheAgentsUpdates_AndUnknownContentIsIgnored()
    {
        var operations = new List<OperationDisplayEvent>();
        var sink = new RecordingSink();
        var runtime = Runtime("tools-plan-diff", new AcpClientHandler(null, null));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        var outcome = await new AcpTurnDriver(sink, null, Adapter(operations))
            .RunAsync(runtime, [new TextContent("go")], CancellationToken.None).Within(30);

        Assert.Equal(StopReasons.EndTurn, outcome.StopReason);
        var blocks = sink.Blocks.Select(Plain.Of).ToList();
        Assert.Equal(
        [
            "📄 Read config · /work/config.json:3",
            "  ⎿ completed",
            "✏️ Edit config",
            "  ⎿ Awaiting permission…",
            "  ⎿ terminal output not supported",
            "  ⎿ failed: disk full",
            "Plan\n  [x] Edit config",
        ], blocks);
        Assert.Equal(["Looking at the file.", "Done."], sink.Replies);
        // The call failed, so the proposed change never happened and is never shown as one.
        Assert.Empty(operations);
    }

    private static ToolCall WriteCall(string status = ToolCallStatuses.Pending) => new()
    {
        ToolCallId = "w", Title = "Write a.txt", Kind = ToolKinds.Edit, Status = status,
        Content = [new DiffToolCallContent { Path = Path.GetFullPath("/work/a.txt"), OldText = null, NewText = "one\ntwo\n" }],
    };

    private static string[] Shown(IEnumerable<IRenderable> blocks) => blocks.Select(Plain.Of).ToArray();

    [Fact]
    public void AWriteThatIsNotDecidedYet_SaysItIsAwaitingPermission_AndNeverClaimsToHaveWritten()
    {
        var operations = new List<OperationDisplayEvent>();
        var adapter = Adapter(operations);

        var pending = Shown(adapter.Apply(WriteCall()));
        var running = Shown(adapter.Apply(new ToolCallUpdate { ToolCallId = "w", Status = ToolCallStatuses.InProgress }));

        Assert.Equal(["✏️ Write a.txt", "  ⎿ Awaiting permission…"], pending);
        Assert.Equal(["  ⎿ Writing…"], running);
        Assert.Empty(operations);
    }

    [Fact]
    public void AWriteThatCompleted_ShowsTheWriteOnlyThen()
    {
        var operations = new List<OperationDisplayEvent>();
        var adapter = Adapter(operations);
        adapter.Apply(WriteCall());

        var done = Shown(adapter.Apply(new ToolCallUpdate { ToolCallId = "w", Status = ToolCallStatuses.Completed }));

        Assert.Equal(["OP Write a.txt +0 -0", "  ⎿ completed"], done);
        Assert.Equal("Write", Assert.Single(operations).OperationType);
    }

    [Fact]
    public void AWriteThatFailed_ShowsNoWrite_AndNoGuessAtTheReason()
    {
        var operations = new List<OperationDisplayEvent>();
        var adapter = Adapter(operations);
        adapter.Apply(WriteCall());

        var failed = Shown(adapter.Apply(new ToolCallUpdate { ToolCallId = "w", Status = ToolCallStatuses.Failed }));

        Assert.Equal(["  ⎿ failed"], failed);
        Assert.Empty(operations);
    }

    [Fact]
    public void ARefusedPermission_EndsTheCallAtOnce_AndTheAgentsLaterFailureIsNotRepeated()
    {
        var operations = new List<OperationDisplayEvent>();
        var adapter = Adapter(operations);
        adapter.Apply(WriteCall());

        Assert.Equal("  ⎿ permission denied", Plain.Of(adapter.NotePermission("w", allowed: false)!));
        Assert.Empty(adapter.Apply(new ToolCallUpdate { ToolCallId = "w", Status = ToolCallStatuses.Failed }));
        Assert.Empty(adapter.Apply(new ToolCallUpdate { ToolCallId = "w", Status = ToolCallStatuses.Completed }));
        Assert.Empty(operations);
    }

    [Fact]
    public void AnAllowedPermission_SaysNothingByItself()
    {
        var adapter = Adapter();
        adapter.Apply(WriteCall());

        Assert.Null(adapter.NotePermission("w", allowed: true));
    }

    private static ToolCallUpdate Failed(string text) => new()
    {
        ToolCallId = "f", Title = "Run it", Kind = ToolKinds.Execute, Status = ToolCallStatuses.Failed,
        Content = [new ContentToolCallContent { Content = new TextContent(text) }],
    };

    [Theory]
    [InlineData("```\n```", "  ⎿ failed")]
    [InlineData("```", "  ⎿ failed")]
    [InlineData("```text\n```", "  ⎿ failed")]
    [InlineData("   \n  ", "  ⎿ failed")]
    [InlineData("```\nUser refused permission to run tool\n```", "  ⎿ failed: User refused permission to run tool")]
    [InlineData("```console\nexit code 2\nmore\n```", "  ⎿ failed: exit code 2")]
    [InlineData("disk full\nsecond line", "  ⎿ failed: disk full")]
    public void TheFailureLine_NeverShowsACodeFenceAsTheMessage(string text, string expected)
    {
        var blocks = Shown(Adapter().Apply(Failed(text)));

        Assert.Equal(expected, blocks[^1]);
        Assert.DoesNotContain("`", blocks[^1]);
    }

    [Fact]
    public async Task ThePrompter_ReportsWhetherTheUserAllowedTheCall()
    {
        var decisions = new List<(string Id, bool Allowed)>();
        var prompter = new AcpPermissionPrompter(_gate, _menu, () => new Noop(), _lines.Add, () => null, decided: (id, ok) => decisions.Add((id, ok)));
        var request = new RequestPermissionRequest
        {
            SessionId = "s",
            ToolCall = new ToolCallUpdate { ToolCallId = "c1" },
            Options =
            [
                new PermissionOption { OptionId = "y", Name = "Allow", Kind = PermissionOptionKinds.AllowOnce },
                new PermissionOption { OptionId = "n", Name = "Reject", Kind = PermissionOptionKinds.RejectOnce },
            ],
        };

        var allowed = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        await AnswerWhenShown("Allow");
        await allowed.Within();
        var rejected = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        await AnswerWhenShown("Reject");
        await rejected.Within();
        var cancelled = prompter.RequestAsync(request, CancellationToken.None).AsTask();
        await AnswerWhenShown(choice: null);
        await cancelled.Within();

        Assert.Equal([("c1", true), ("c1", false), ("c1", false)], decisions);
    }

    // ───────── initialize and extension notifications ─────────

    [Fact]
    public void Initialize_AdvertisesFsExactlyWhenTheFsHandlerIsConnected()
    {
        static string Initialize(AcpClientHandler handler) => System.Text.Json.JsonSerializer.Serialize(
            new InitializeRequest
            {
                ProtocolVersion = AcpProtocol.Version,
                ClientCapabilities = handler.Capabilities,
                ClientInfo = new Implementation { Name = "MandoCode", Version = "0.16.1" },
            },
            AcpJsonContext.Default.InitializeRequest);

        var with = Initialize(new AcpClientHandler(null, new AcpFileSystemHandler(_work)));
        var without = Initialize(new AcpClientHandler(null, null));

        Assert.Contains("\"fs\":{\"readTextFile\":true,\"writeTextFile\":true}", with);
        Assert.Contains("\"terminal\":false", with);
        Assert.Contains("\"fs\":{\"readTextFile\":false,\"writeTextFile\":false}", without);
        // The same request in a JSON-RPC envelope is the 218 bytes seen in the trace of a real session.
        var envelope = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":" + with + "}";
        Assert.Equal(218, System.Text.Encoding.UTF8.GetByteCount(envelope));
    }

    private async Task<List<(string Method, string Content)>> RunWithExtensionNotification(string? debugFlag, bool trace)
    {
        var before = Environment.GetEnvironmentVariable(AcpLogPaths.ExtensionDumpEnvironmentVariable);
        Environment.SetEnvironmentVariable(AcpLogPaths.ExtensionDumpEnvironmentVariable, debugFlag);
        try
        {
            var dumped = new List<(string, string)>();
            var config = new AcpAgentConfig { Command = _host, Args = [MockDll, "ext-notification"], StartupTimeoutSeconds = 20, Trace = trace };
            var runtime = new AcpAgentRuntime("mock", config, _work, new AcpAgentLauncher(new InMemoryTrustStore()),
                _ => Task.FromResult(true), Path.Combine(_work, "logs"), extensionDump: (m, c) => { lock (dumped) dumped.Add((m, c)); });
            _cleanup.Add(runtime);
            await runtime.ConnectAsync(CancellationToken.None).Within(40);

            Assert.Equal("after extension", await ReplyAsync(runtime, "go").Within(20));
            await runtime.DisposeAsync();
            lock (dumped) return dumped.ToList();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AcpLogPaths.ExtensionDumpEnvironmentVariable, before);
        }
    }

    [Fact]
    public async Task AnExtensionNotification_IsIgnoredByDefault_AndTheTurnStillWorks()
    {
        Assert.Empty(await RunWithExtensionNotification(debugFlag: null, trace: false));
    }

    [Fact]
    public async Task WithTheDebugFlag_TheExtensionContentIsHandedToTheTerminalHook_ButNeverToTheTrace()
    {
        var dumped = await RunWithExtensionNotification(debugFlag: "1", trace: true);

        var (method, content) = Assert.Single(dumped);
        Assert.Equal("_auth/status_update", method);
        Assert.Contains("someone@example.com", content);
        var trace = string.Join('\n', TraceLines());
        Assert.Contains("<- notification _auth/status_update", trace);
        Assert.DoesNotContain("someone@example.com", trace);
        foreach (var file in Directory.GetFiles(_work, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("someone@example.com", File.ReadAllText(file));
    }

    // ───────── trace ─────────

    private string[] TraceLines()
    {
        var files = Directory.Exists(Path.Combine(_work, "logs"))
            ? Directory.GetFiles(Path.Combine(_work, "logs"), "*.trace.log")
            : [];
        if (files.Length == 0)
            return [];
        using var stream = new FileStream(files.Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public async Task TheTrace_ShowsWhichMethodsTheAgentCalled_ButNeverAnyContent()
    {
        var runtime = Runtime("fs-roundtrip", new AcpClientHandler(null, new AcpFileSystemHandler(_work)), c => c.Trace = true);
        await runtime.ConnectAsync(CancellationToken.None).Within(40);
        var source = Path.Combine(_work, "very-secret-name.txt");
        File.WriteAllText(source, "the secret file body");

        await ReplyAsync(runtime, source).Within(20);
        await runtime.DisposeAsync();

        var trace = TraceLines();
        Assert.Contains(trace, l => l.Contains("<- request fs/read_text_file") && l.Contains("id="));
        Assert.Contains(trace, l => l.Contains("<- request fs/write_text_file"));
        Assert.Contains(trace, l => l.Contains("-> response fs/read_text_file"));
        Assert.Contains(trace, l => l.Contains("-> request session/prompt"));
        Assert.Contains(trace, l => l.Contains("<- notification session/update"));
        Assert.All(trace, l => Assert.Matches(@"^\d{4}-\d\d-\d\dT[\d:.]+Z (->|<-) \w+ \S+ id=\S+ bytes=\d+$", l));
        var all = string.Join('\n', trace);
        Assert.DoesNotContain("very-secret-name", all);
        Assert.DoesNotContain("the secret file body", all);
        Assert.DoesNotContain("THE SECRET FILE BODY", all);
        Assert.DoesNotContain(_work, all);
    }

    [Fact]
    public async Task WithoutTheSetting_NoTraceFileIsCreated()
    {
        var runtime = Runtime("echo", new AcpClientHandler(null, null));
        await runtime.ConnectAsync(CancellationToken.None).Within(40);
        await ReplyAsync(runtime, "hello").Within(20);
        await runtime.DisposeAsync();

        Assert.Empty(TraceLines());
        Assert.False(Directory.Exists(Path.Combine(_work, "logs")) && Directory.GetFiles(Path.Combine(_work, "logs"), "*.trace.log").Length > 0);
    }

    [Fact]
    public void TheTraceCanAlsoBeTurnedOnByTheEnvironment()
    {
        var before = Environment.GetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, null);
            Assert.False(AcpLogPaths.TraceEnabled(new AcpAgentConfig()));
            Assert.True(AcpLogPaths.TraceEnabled(new AcpAgentConfig { Trace = true }));
            Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, "1");
            Assert.True(AcpLogPaths.TraceEnabled(new AcpAgentConfig()));
            Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, "0");
            Assert.False(AcpLogPaths.TraceEnabled(new AcpAgentConfig()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, before);
        }
    }

    [Fact]
    public void ToolState_IsKeptByToolCallId_SoAnUpdateOnlyCarriesWhatChanged()
    {
        var adapter = Adapter();

        var first = adapter.Apply(new ToolCall { ToolCallId = "t", Title = "Run tests", Kind = ToolKinds.Execute });
        var progress = adapter.Apply(new ToolCallUpdate { ToolCallId = "t", Status = ToolCallStatuses.InProgress });
        var done = adapter.Apply(new ToolCallUpdate { ToolCallId = "t", Status = ToolCallStatuses.Completed });
        var again = adapter.Apply(new ToolCallUpdate { ToolCallId = "t", Status = ToolCallStatuses.Completed });

        Assert.Equal(["⚡ Run tests", "  ⎿ Awaiting permission…"], Shown(first));
        Assert.Equal(["  ⎿ Running…"], Shown(progress));
        Assert.Equal("  ⎿ completed", Plain.Of(Assert.Single(done)));
        Assert.Empty(again);
    }

    [Fact]
    public void AnUpdateForACallThatWasNeverAnnounced_IsAnnouncedOnce()
    {
        var blocks = Adapter().Apply(new ToolCallUpdate { ToolCallId = "x", Title = "Search", Kind = ToolKinds.Search, Status = ToolCallStatuses.InProgress });

        Assert.Equal("🔎 Search", Plain.Of(Assert.Single(blocks)));
    }

    [Theory]
    [InlineData(ToolKinds.Read, "📄")]
    [InlineData(ToolKinds.Edit, "✏️")]
    [InlineData(ToolKinds.Delete, "🗑️")]
    [InlineData(ToolKinds.Search, "🔎")]
    [InlineData(ToolKinds.Execute, "⚡")]
    [InlineData(ToolKinds.Fetch, "🌐")]
    [InlineData("a_kind_from_the_future", "⚡")]
    public void TheIconFollowsTheKind(string kind, string icon) => Assert.Equal(icon, AcpTranscriptAdapter.IconFor(kind));

    [Fact]
    public void ThePlan_IsReplacedAsAWhole_NotMerged()
    {
        var adapter = Adapter();
        adapter.SetPlan(new Plan { Entries = [new PlanEntry { Content = "A", Status = PlanEntryStatuses.Pending }, new PlanEntry { Content = "B", Status = PlanEntryStatuses.Pending }] });
        adapter.SetPlan(new Plan { Entries = [new PlanEntry { Content = "C", Status = PlanEntryStatuses.InProgress }] });

        Assert.Equal("Plan 0/1", adapter.PlanSummary());
        Assert.Equal("Plan\n  [>] C", Plain.Of(adapter.TakePlan()!));
        Assert.Null(adapter.TakePlan());
    }

    [Fact]
    public void ADiffShownByTheToolCall_IsNotShownAgainWhenTheFileIsWritten_AndViceVersa()
    {
        var adapter = Adapter();
        var path = Path.GetFullPath("/work/a.txt");

        adapter.Apply(new ToolCall
        {
            ToolCallId = "t", Title = "Edit", Kind = ToolKinds.Edit,
            Content = [new DiffToolCallContent { Path = path, OldText = "a\n", NewText = "b\n" }],
        });
        var viaFs = adapter.FileChanged(new AcpFileChange(path, "a\n", "b\n"));
        var completed = Shown(adapter.Apply(new ToolCallUpdate { ToolCallId = "t", Status = ToolCallStatuses.Completed }));

        Assert.NotNull(viaFs);
        Assert.Equal(["  ⎿ completed"], completed);

        // And the other way round: the completed call shows the diff, the write that follows does not repeat it.
        adapter.BeginTurn();
        adapter.Apply(new ToolCall
        {
            ToolCallId = "t2", Title = "Edit", Kind = ToolKinds.Edit,
            Content = [new DiffToolCallContent { Path = path, OldText = "a\n", NewText = "b\n" }],
        });
        var viaTool = Shown(adapter.Apply(new ToolCallUpdate { ToolCallId = "t2", Status = ToolCallStatuses.Completed }));
        Assert.Equal(["OP Update a.txt +1 -1", "  ⎿ completed"], viaTool);
        Assert.Null(adapter.FileChanged(new AcpFileChange(path, "a\n", "b\n")));
    }

    [Fact]
    public void ANewFile_IsShownAsAWrite()
    {
        var operations = new List<OperationDisplayEvent>();

        Adapter(operations).FileChanged(new AcpFileChange(Path.GetFullPath("/work/new.txt"), null, "x\ny\n"));

        var write = Assert.Single(operations);
        Assert.Equal("Write", write.OperationType);
        Assert.True(write.IsNewFile);
    }

    [Fact]
    public void TheRealRendererAcceptsTheEvents()
    {
        var renderer = new OperationDisplayRenderer(new ProjectRootAccessor(_work));
        var adapter = new AcpTranscriptAdapter(renderer.BuildRenderable, _work);

        Assert.NotNull(adapter.FileChanged(new AcpFileChange(Path.Combine(_work, "a.txt"), "a\n", "b\n")));
        Assert.NotNull(adapter.FileChanged(new AcpFileChange(Path.Combine(_work, "b.txt"), null, "new\n")));
    }
}
