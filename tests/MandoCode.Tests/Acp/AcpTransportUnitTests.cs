using MandoCode.Acp;
using MandoCode.Acp.Transport;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpEnvironmentTests
{
    private static AcpLaunchSpec Spec(bool pass = false, Dictionary<string, string>? env = null) =>
        new("agent", "cmd", [], env, pass, "/work");

    private static readonly KeyValuePair<string, string>[] Inherited =
    [
        new("PATH", "/usr/bin"),
        new("HOME", "/home/u"),
        new("ANTHROPIC_API_KEY", "sk-secret"),
    ];

    [Fact]
    public void ApiKey_IsNotPassedByDefault()
    {
        var env = AcpEnvironment.Build(Inherited, Spec(), caseInsensitiveNames: false);

        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/u", env["HOME"]);
    }

    [Fact]
    public void ApiKey_IsPassedOnlyWhenTheEntryOptsIn()
    {
        var env = AcpEnvironment.Build(Inherited, Spec(pass: true), caseInsensitiveNames: false);
        Assert.Equal("sk-secret", env["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void ApiKey_RemovalIsCaseInsensitive_OnWindowsStyleEnvironments()
    {
        KeyValuePair<string, string>[] inherited = [new("Anthropic_Api_Key", "sk-secret"), new("Path", "C:\\bin")];

        var env = AcpEnvironment.Build(inherited, Spec(), caseInsensitiveNames: true);

        Assert.DoesNotContain(env.Keys, k => k.Equals("ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("C:\\bin", env["PATH"]);
    }

    [Fact]
    public void EntryEnv_OverridesInheritedValues_AndAddsNewOnes()
    {
        var env = AcpEnvironment.Build(Inherited,
            Spec(env: new Dictionary<string, string> { ["HOME"] = "/override", ["EXTRA"] = "1" }),
            caseInsensitiveNames: false);

        Assert.Equal("/override", env["HOME"]);
        Assert.Equal("1", env["EXTRA"]);
    }

    [Fact]
    public void ExplicitEnvValue_Wins_EvenForTheApiKey()
    {
        // The user wrote it into the agent entry on purpose (and the trust prompt shows env names).
        var env = AcpEnvironment.Build(Inherited,
            Spec(env: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-explicit" }),
            caseInsensitiveNames: false);

        Assert.Equal("sk-explicit", env["ANTHROPIC_API_KEY"]);
    }
}

public class StderrLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mandocode-acp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RingBuffer_KeepsOnlyTheLastLines()
    {
        var log = new StderrLog(capacity: 3);
        for (var i = 1; i <= 10; i++)
            log.Append("line " + i);

        Assert.Equal("line 8\nline 9\nline 10", log.Tail());
        Assert.Equal("line 10", log.Tail(1));
    }

    [Fact]
    public void FileLog_IsWritten_AndRotatedWhenItGrows()
    {
        var path = Path.Combine(_directory, "logs", "agent.log");
        var log = new StderrLog(capacity: 5, filePath: path, maxFileBytes: 2048);

        for (var i = 0; i < 100; i++)
            log.Append(new string('a', 60) + i);
        log.Close();

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"), "the log should have been rotated");
        Assert.True(new FileInfo(path).Length <= 4096);
        Assert.Contains("a99", File.ReadAllText(path));
    }

    [Fact]
    public void UnwritableLogPath_DoesNotThrow()
    {
        var blocker = Path.Combine(_directory, "blocker");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocker, "i am a file, not a directory");

        var log = new StderrLog(filePath: Path.Combine(blocker, "agent.log"));
        log.Append("still works in memory");

        Assert.Equal("still works in memory", log.Tail());
    }
}

/// <summary>
/// The resolver's rules, exercised against a fake machine so both the Windows and the Unix rules run
/// on any host. Paths are built with the host's separators; only the OS-specific rules are simulated.
/// </summary>
public class AcpCommandResolverTests
{
    private static readonly string Bin = Path.Combine(Path.GetTempPath(), "mc-resolver", "bin");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "mc-resolver", "other");

    private static readonly byte[] Pe = [(byte)'M', (byte)'Z', 0x90, 0x00];
    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
    private static readonly byte[] MachO = [0xCF, 0xFA, 0xED, 0xFE];
    private static readonly byte[] Shebang = "#!/usr/bin/env node\n"u8.ToArray();
    private static readonly byte[] Text = "hello"u8.ToArray();

    private sealed class FakeMachine(bool windows) : IAcpResolverEnvironment
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Executable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsWindows => windows;
        public string? PathVariable { get; set; }
        public bool FileExists(string path) => Files.ContainsKey(path);
        public byte[] ReadHeader(string path, int count) => Files[path].Take(count).ToArray();
        public bool IsExecutable(string path) => Executable.Contains(path);

        public FakeMachine Add(string path, byte[] content, bool executable = true)
        {
            Files[path] = content;
            if (executable)
                Executable.Add(path);
            return this;
        }
    }

    private static FakeMachine Windows(params string[] pathDirs) =>
        new(windows: true) { PathVariable = string.Join(Path.PathSeparator, pathDirs) };

    private static FakeMachine Unix(params string[] pathDirs) =>
        new(windows: false) { PathVariable = string.Join(Path.PathSeparator, pathDirs) };

    private static AcpLaunchException Refused(string command, IReadOnlyList<string> args, IAcpResolverEnvironment machine) =>
        Assert.Throws<AcpLaunchException>(() => AcpCommandResolver.Resolve(command, args, machine));

    // ───────────── Windows rules ─────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("claude.exe")]
    public void Windows_ExeOnPath_IsAccepted_AndTheAbsolutePathIsReturned(string command)
    {
        var exe = Path.Combine(Bin, "claude.exe");
        var machine = Windows(Bin).Add(exe, Pe);

        var resolved = AcpCommandResolver.Resolve(command, ["--flag"], machine);

        Assert.Equal(Path.GetFullPath(exe), resolved.ExecutablePath);
        Assert.Equal(["--flag"], resolved.Args);
    }

    [Fact]
    public void Windows_CmdShimOnly_IsRejected_WithTheExplanation()
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, "claude.cmd"), Text);

        var ex = Refused("claude", [], machine);

        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, ex.Failure);
        Assert.Contains("an .exe", ex.Message);
        Assert.Contains("node <absolute path to script>", ex.Message);
    }

    [Theory]
    [InlineData("claude.cmd")]
    [InlineData("claude.bat")]
    [InlineData("claude.ps1")]
    public void Windows_ExplicitScriptExtensions_AreRejected(string command)
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, command), Text);
        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, Refused(command, [], machine).Failure);
    }

    [Fact]
    public void Windows_ExeIsPreferredOverAShimInALaterDirectory()
    {
        var machine = Windows(Bin, Other)
            .Add(Path.Combine(Bin, "claude.cmd"), Text)
            .Add(Path.Combine(Other, "claude.exe"), Pe);

        var resolved = AcpCommandResolver.Resolve("claude", [], machine);

        Assert.Equal(Path.GetFullPath(Path.Combine(Other, "claude.exe")), resolved.ExecutablePath);
    }

    [Fact]
    public void Windows_ExeThatIsNotAPortableExecutable_IsRejected()
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, "fake.exe"), Text);
        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, Refused("fake", [], machine).Failure);
    }

    [Fact]
    public void AbsolutePath_ToACmd_IsRejected_AndToAMissingFile_IsNotFound()
    {
        var cmd = Path.Combine(Bin, "tool.cmd");
        var machine = Windows(Bin).Add(cmd, Text);

        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, Refused(cmd, [], machine).Failure);
        Assert.Equal(AcpLaunchFailure.NotFound, Refused(Path.Combine(Bin, "missing.exe"), [], machine).Failure);
    }

    [Fact]
    public void Windows_AbsoluteExe_IsAccepted()
    {
        var exe = Path.Combine(Bin, "agent.exe");
        var resolved = AcpCommandResolver.Resolve(exe, [], Windows().Add(exe, Pe));
        Assert.Equal(Path.GetFullPath(exe), resolved.ExecutablePath);
    }

    // ───────────── Unix rules ─────────────

    [Theory]
    [MemberData(nameof(NativeBinaries))]
    public void Unix_ElfAndMachO_AreAccepted(byte[] magic)
    {
        var path = Path.Combine(Bin, "agent");
        var resolved = AcpCommandResolver.Resolve("agent", [], Unix(Bin).Add(path, magic));
        Assert.Equal(Path.GetFullPath(path), resolved.ExecutablePath);
    }

    public static IEnumerable<object[]> NativeBinaries() => [[Elf], [MachO]];

    [Fact]
    public void Unix_ShebangScript_IsRejected()
    {
        var machine = Unix(Bin).Add(Path.Combine(Bin, "claude"), Shebang);

        var ex = Refused("claude", [], machine);

        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, ex.Failure);
        Assert.Contains("#!", ex.Message);
    }

    [Fact]
    public void Unix_UnknownBinaryFormat_AndNonExecutableFiles_AreRejected()
    {
        var unknown = Unix(Bin).Add(Path.Combine(Bin, "blob"), Text);
        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, Refused("blob", [], unknown).Failure);

        // Present on PATH but without the execute bit: the shell would skip it, so it is "not found".
        var noExec = Unix(Bin).Add(Path.Combine(Bin, "tool"), Elf, executable: false);
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("tool", [], noExec).Failure);
    }

    [Fact]
    public void Unix_FirstExecutableOnPathWins()
    {
        var machine = Unix(Bin, Other)
            .Add(Path.Combine(Bin, "agent"), Elf)
            .Add(Path.Combine(Other, "agent"), Elf);

        Assert.Equal(Path.GetFullPath(Path.Combine(Bin, "agent")), AcpCommandResolver.Resolve("agent", [], machine).ExecutablePath);
    }

    // ───────────── rules for every OS ─────────────

    [Fact]
    public void CommandNotOnPath_IsNotFound()
    {
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("nothing", [], Windows(Bin)).Failure);
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("nothing", [], Unix(Bin)).Failure);
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("  ", [], Unix(Bin)).Failure);
    }

    [Fact]
    public void RelativePathEntries_AreIgnored()
    {
        // "." or "bin" in PATH would resolve against the current directory, i.e. against the opened repository.
        var machine = Unix(".", "bin").Add(Path.Combine(".", "agent"), Elf).Add(Path.Combine("bin", "agent"), Elf);
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("agent", [], machine).Failure);
    }

    [Theory]
    [InlineData("./agent")]
    [InlineData(@".\agent.exe")]
    [InlineData("tools/agent")]
    public void RelativeCommandPaths_AreRefused(string command)
    {
        Assert.Equal(AcpLaunchFailure.InvalidArguments, Refused(command, [], Unix(Bin)).Failure);
    }

    [Theory]
    [InlineData("npx")]
    [InlineData("npx.cmd")]
    [InlineData("NPX.EXE")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    [InlineData("bunx")]
    [InlineData("uvx")]
    public void PackageDownloaders_AreForbidden_EvenIfTheyExist(string command)
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, "npx.exe"), Pe).Add(Path.Combine(Bin, "pnpm.exe"), Pe);

        var ex = Refused(command, ["-y", "some-adapter"], machine);

        Assert.Equal(AcpLaunchFailure.Forbidden, ex.Failure);
        Assert.Contains("node <absolute path to script>", ex.Message);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("cmd.exe")]
    [InlineData("powershell")]
    [InlineData("pwsh.exe")]
    [InlineData("sh")]
    [InlineData("bash")]
    [InlineData("/bin/sh")]
    public void Shells_AreForbidden(string command)
    {
        Assert.Equal(AcpLaunchFailure.Forbidden, Refused(command, ["-c", "evil"], Unix(Bin)).Failure);
    }

    // ───────────── node adapters ─────────────

    private static readonly string Script = Path.Combine(Path.GetTempPath(), "mc-resolver", "adapter", "dist", "index.js");

    private static FakeMachine NodeMachine()
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, "node.exe"), Pe);
        machine.Files[Script] = Text;
        return machine;
    }

    [Fact]
    public void Node_WithAnAbsoluteExistingScript_IsAccepted()
    {
        var resolved = AcpCommandResolver.Resolve("node", [Script, "--stdio"], NodeMachine());

        Assert.Equal(Path.GetFullPath(Path.Combine(Bin, "node.exe")), resolved.ExecutablePath);
        Assert.Equal([Script, "--stdio"], resolved.Args);
    }

    [Theory]
    [InlineData("dist/index.js")]
    [InlineData(@"..\adapter\index.js")]
    [InlineData("index.js")]
    public void Node_WithARelativeScript_IsRejected(string script)
    {
        var ex = Refused("node", [script], NodeMachine());
        Assert.Equal(AcpLaunchFailure.InvalidArguments, ex.Failure);
        Assert.Contains("absolute path", ex.Message);
    }

    [Theory]
    [InlineData("-e")]
    [InlineData("--eval")]
    public void Node_WithInlineEvalAsFirstArgument_IsRejected(string flag)
    {
        Assert.Equal(AcpLaunchFailure.InvalidArguments, Refused("node", [flag, "process.exit()"], NodeMachine()).Failure);
    }

    [Fact]
    public void Node_WithoutArguments_OrWithAMissingScript_IsRejected()
    {
        Assert.Equal(AcpLaunchFailure.InvalidArguments, Refused("node", [], NodeMachine()).Failure);

        var missing = Path.Combine(Path.GetTempPath(), "mc-resolver", "nowhere", "index.js");
        Assert.Equal(AcpLaunchFailure.NotFound, Refused("node", [missing], NodeMachine()).Failure);
    }

    [Fact]
    public void Node_ItselfMustBeARealBinary()
    {
        var machine = Windows(Bin).Add(Path.Combine(Bin, "node.cmd"), Text);
        machine.Files[Script] = Text;

        Assert.Equal(AcpLaunchFailure.NotANativeExecutable, Refused("node", [Script], machine).Failure);
    }
}
