using Xunit;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// The sink lets a host SHOW shell commands as they run. Its whole value depends on staying
/// observational: it must see the full lifecycle, it must see output the model's capped copy
/// drops, and it must never be able to affect the command it is watching.
/// </summary>
public class CommandOutputSinkTests : IDisposable
{
    private readonly string _tempRoot;

    public CommandOutputSinkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private FileSystemPlugin Plugin(ICommandOutputSink sink) =>
        new(new ProjectRootAccessor(_tempRoot), spinner: null, outputSink: sink);

    /// <summary>Echo, spelled for whichever shell ExecuteCommand will actually reach for.</summary>
    private static string Echo(string text) => $"echo {text}";

    [Fact]
    public async Task ReportsStartOutputAndExitCode()
    {
        var sink = new RecordingSink();

        await Plugin(sink).ExecuteCommand(Echo("hello-sink"));

        Assert.Equal(1, sink.Starts);
        Assert.Equal(_tempRoot, sink.WorkingDirectory);
        Assert.Contains(sink.Lines, l => l.Text.Contains("hello-sink"));
        Assert.Equal(0, sink.ExitCode);
        Assert.Null(sink.KillReason);
        Assert.Equal(1, sink.Finishes);
    }

    [Fact]
    public async Task FailingCommandStillReportsItsExitCode()
    {
        // A non-zero exit is a normal outcome, not a kill — the display has to be able to tell
        // "this finished and failed" from "this was cut off", because they mean different things
        // to someone watching.
        var sink = new RecordingSink();

        await Plugin(sink).ExecuteCommand("exit 3");

        Assert.Equal(3, sink.ExitCode);
        Assert.Null(sink.KillReason);
    }

    [Fact]
    public async Task BareCdIsNotAnnounced()
    {
        // A bare `cd` is intercepted and never starts a process. Announcing one would leave a
        // command header on the display with no output and no completion under it.
        var sink = new RecordingSink();

        await Plugin(sink).ExecuteCommand("cd .");

        Assert.Equal(0, sink.Starts);
        Assert.Equal(0, sink.Finishes);
    }

    [Fact]
    public async Task SinkSeesOutputThatTheModelsCopyTruncates()
    {
        // The 5000-character cap exists to protect the model's context. A host display has its own
        // scrollback, so inheriting that cap would hide the tail of exactly the long build output
        // someone opened the panel to watch.
        var sink = new RecordingSink();
        var big = Path.Combine(_tempRoot, "big.txt");
        await File.WriteAllLinesAsync(big, Enumerable.Range(0, 60).Select(_ => new string('x', 200)));

        // Dumping a file is the one way to produce lots of output that needs no shell-specific
        // quoting, loop syntax, or script-execution policy to survive the trip through cmd/bash.
        var dump = OperatingSystem.IsWindows() ? "type" : "cat";
        var modelCopy = await Plugin(sink).ExecuteCommand($"{dump} \"{big}\"");

        Assert.Contains("truncated at 5000 characters", modelCopy);
        var sunkChars = sink.Lines.Sum(l => l.Text.Length);
        Assert.True(sunkChars > 5000, $"sink saw only {sunkChars} characters; the cap leaked into it");
    }

    [Fact]
    public async Task AThrowingSinkCannotBreakTheCommand()
    {
        // The display is a bystander. If it faults, the agent's command must still run and still
        // report normally — the alternative is a host bug silently breaking the agent's tools.
        var result = await Plugin(new ThrowingSink()).ExecuteCommand(Echo("still-ran"));

        Assert.Contains("Exit code: 0", result);
        Assert.Contains("still-ran", result);
    }

    private sealed record OutputLine(string Text, bool IsError);

    private sealed class RecordingSink : ICommandOutputSink
    {
        private readonly object _lock = new();
        private readonly List<OutputLine> _lines = new();

        public int Starts { get; private set; }
        public int Finishes { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public int? ExitCode { get; private set; }
        public string? KillReason { get; private set; }

        public IReadOnlyList<OutputLine> Lines { get { lock (_lock) { return _lines.ToList(); } } }

        public void CommandStarted(string command, string workingDirectory)
        {
            lock (_lock) { Starts++; WorkingDirectory = workingDirectory; }
        }

        // Raised on the process's reader threads — the lock is the test asserting the contract
        // the interface documents, not incidental caution.
        public void CommandOutput(string line, bool isError)
        {
            lock (_lock) { _lines.Add(new OutputLine(line, isError)); }
        }

        public void CommandFinished(int? exitCode, string? killReason)
        {
            lock (_lock) { Finishes++; ExitCode = exitCode; KillReason = killReason; }
        }
    }

    private sealed class ThrowingSink : ICommandOutputSink
    {
        public void CommandStarted(string command, string workingDirectory) => throw new InvalidOperationException("boom");
        public void CommandOutput(string line, bool isError) => throw new InvalidOperationException("boom");
        public void CommandFinished(int? exitCode, string? killReason) => throw new InvalidOperationException("boom");
    }
}
