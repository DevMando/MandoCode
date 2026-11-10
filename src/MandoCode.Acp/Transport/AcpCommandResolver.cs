namespace MandoCode.Acp.Transport;

/// <summary>The file-system and platform facts the resolver needs; replaceable so rules are testable on any OS.</summary>
public interface IAcpResolverEnvironment
{
    bool IsWindows { get; }

    /// <summary>The value of the PATH variable.</summary>
    string? PathVariable { get; }

    bool FileExists(string path);

    /// <summary>Reads up to <paramref name="count"/> bytes from the start of the file.</summary>
    byte[] ReadHeader(string path, int count);

    /// <summary>Unix only: whether the file has an execute bit.</summary>
    bool IsExecutable(string path);
}

/// <summary>The real machine.</summary>
public sealed class SystemResolverEnvironment : IAcpResolverEnvironment
{
    public static SystemResolverEnvironment Instance { get; } = new();

    public bool IsWindows => OperatingSystem.IsWindows();

    public string? PathVariable => Environment.GetEnvironmentVariable("PATH");

    public bool FileExists(string path) => File.Exists(path);

    public byte[] ReadHeader(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            return read == count ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>The outcome of resolving an agent command: what to execute and with which arguments.</summary>
/// <param name="ExecutablePath">Absolute path of the native executable that will be started.</param>
/// <param name="Args">The arguments, unchanged.</param>
public sealed record ResolvedCommand(string ExecutablePath, IReadOnlyList<string> Args);

/// <summary>
/// Turns the configured command into a real native executable, or refuses.
/// <para>
/// The rules exist because an agent command is code that runs with the user's permissions:
/// nothing is launched through a shell, nothing is downloaded at launch time, and what runs is
/// the file the user can see (the resolved absolute path is what gets started and what the trust
/// prompt shows, so PATH cannot change it in between).
/// </para>
/// <list type="bullet">
/// <item>Windows: only a real <c>.exe</c> (PE header). <c>.cmd</c>, <c>.bat</c>, <c>.ps1</c> are refused.</item>
/// <item>Unix: an executable ELF or Mach-O binary. Shebang scripts are refused.</item>
/// <item><c>npx</c>-style launchers and shells (<c>cmd</c>, <c>powershell</c>, <c>sh</c>, ...) are refused.</item>
/// <item>For <c>node</c> the first argument must be the absolute path of an existing script.</item>
/// </list>
/// </summary>
public static class AcpCommandResolver
{
    public const string ScriptHint =
        "Specify the path to an .exe (or a native binary), or run the adapter as `node <absolute path to script>`.";

    private static readonly string[] ScriptExtensions = [".cmd", ".bat", ".ps1", ".vbs"];

    private static readonly HashSet<string> Downloaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "npx", "npm", "pnpm", "pnpx", "yarn", "bunx", "uvx", "pipx",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "sh", "bash", "zsh", "dash", "fish", "ksh", "csh", "tcsh", "ash",
    };

    public static ResolvedCommand Resolve(string command, IReadOnlyList<string> args, IAcpResolverEnvironment? environment = null)
    {
        environment ??= SystemResolverEnvironment.Instance;

        if (string.IsNullOrWhiteSpace(command))
            throw new AcpLaunchException(AcpLaunchFailure.NotFound, "The agent has no command configured.");

        command = command.Trim();
        var name = NameWithoutExtension(command);

        if (Downloaders.Contains(name))
        {
            throw new AcpLaunchException(AcpLaunchFailure.Forbidden,
                $"`{name}` downloads and runs packages at launch time, which MandoCode does not allow. " +
                "Install the adapter yourself (with a pinned version) and run it as `node <absolute path to script>`.");
        }

        if (Shells.Contains(name))
        {
            throw new AcpLaunchException(AcpLaunchFailure.Forbidden,
                $"`{name}` is a shell. MandoCode never launches agents through a shell; point `command` at the agent itself. {ScriptHint}");
        }

        var executable = Path.IsPathRooted(command)
            ? ResolveAbsolute(command, environment)
            : ResolveOnPath(command, environment);

        ValidateArguments(executable, args, environment);
        return new ResolvedCommand(executable, args);
    }

    private static string ResolveAbsolute(string command, IAcpResolverEnvironment environment)
    {
        if (!environment.FileExists(command))
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotFound,
                $"The agent command `{command}` does not exist.");
        }

        EnsureNativeExecutable(command, environment);
        return Path.GetFullPath(command);
    }

    private static string ResolveOnPath(string command, IAcpResolverEnvironment environment)
    {
        if (command.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new AcpLaunchException(AcpLaunchFailure.InvalidArguments,
                $"The agent command `{command}` is a relative path. Use a bare command name found on PATH, or an absolute path.");
        }

        var directories = (environment.PathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // A relative PATH entry would resolve against whatever the current directory happens to be.
            .Where(Path.IsPathRooted)
            .ToList();

        return environment.IsWindows
            ? ResolveOnWindowsPath(command, directories, environment)
            : ResolveOnUnixPath(command, directories, environment);
    }

    private static string ResolveOnWindowsPath(string command, List<string> directories, IAcpResolverEnvironment environment)
    {
        var extension = Path.GetExtension(command);
        var isExe = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var isScript = ScriptExtensions.Any(s => s.Equals(extension, StringComparison.OrdinalIgnoreCase));

        // `claude.cmd` is rejected outright; `claude` is looked up as claude.exe, and only if there
        // is no .exe anywhere do we report the .cmd/.bat shim we found, so the user learns why.
        var exeName = isExe ? command : command + ".exe";
        string? shim = null;

        foreach (var directory in directories)
        {
            if (!isScript)
            {
                var candidate = Path.Combine(directory, exeName);
                if (environment.FileExists(candidate))
                {
                    EnsureNativeExecutable(candidate, environment);
                    return Path.GetFullPath(candidate);
                }
            }

            if (shim is null)
            {
                foreach (var scriptExtension in ScriptExtensions)
                {
                    var candidate = Path.Combine(directory, isScript ? command : command + scriptExtension);
                    if (environment.FileExists(candidate))
                    {
                        shim = candidate;
                        break;
                    }
                }
            }
        }

        if (shim is not null)
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                $"`{command}` resolves to `{shim}`, which is a script or shim, not a real executable. {ScriptHint}");
        }

        throw NotOnPath(command);
    }

    private static string ResolveOnUnixPath(string command, List<string> directories, IAcpResolverEnvironment environment)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, command);
            if (environment.FileExists(candidate) && environment.IsExecutable(candidate))
            {
                EnsureNativeExecutable(candidate, environment);
                return Path.GetFullPath(candidate);
            }
        }

        throw NotOnPath(command);
    }

    private static AcpLaunchException NotOnPath(string command) =>
        new(AcpLaunchFailure.NotFound,
            $"`{command}` was not found in PATH. Install it, or set an absolute path in the agent's `command`.");

    private static void EnsureNativeExecutable(string path, IAcpResolverEnvironment environment)
    {
        var extension = Path.GetExtension(path);

        if (environment.IsWindows)
        {
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                    $"`{path}` is not an .exe (it is `{(extension.Length == 0 ? "extensionless" : extension)}`). {ScriptHint}");
            }

            var header = environment.ReadHeader(path, 2);
            if (header.Length < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                    $"`{path}` has an .exe extension but is not a Windows executable. {ScriptHint}");
            }

            return;
        }

        if (!environment.IsExecutable(path))
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                $"`{path}` is not marked executable.");
        }

        var magic = environment.ReadHeader(path, 4);
        if (magic.Length >= 2 && magic[0] == (byte)'#' && magic[1] == (byte)'!')
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                $"`{path}` is a script (it starts with `#!`), which would run an interpreter the configuration does not show. {ScriptHint}");
        }

        if (!IsKnownNativeBinary(magic))
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotANativeExecutable,
                $"`{path}` is not a native executable (ELF or Mach-O). {ScriptHint}");
        }
    }

    private static bool IsKnownNativeBinary(byte[] magic)
    {
        if (magic.Length < 4)
            return false;

        // ELF
        if (magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F')
            return true;

        // Mach-O (32/64-bit, both byte orders) and universal ("fat") binaries.
        var value = (uint)(magic[0] << 24 | magic[1] << 16 | magic[2] << 8 | magic[3]);
        return value is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
    }

    private static void ValidateArguments(string executable, IReadOnlyList<string> args, IAcpResolverEnvironment environment)
    {
        if (!NameWithoutExtension(executable).Equals("node", StringComparison.OrdinalIgnoreCase))
            return;

        // `node` runs whatever script it is given, so that script is part of the command: it must be
        // a specific file the user installed, not something relative to wherever we happen to run.
        if (args.Count == 0)
        {
            throw new AcpLaunchException(AcpLaunchFailure.InvalidArguments,
                "`node` needs the absolute path of the adapter script as its first argument.");
        }

        var script = args[0];
        if (!Path.IsPathRooted(script))
        {
            throw new AcpLaunchException(AcpLaunchFailure.InvalidArguments,
                $"The first argument for `node` must be the absolute path of the adapter script (got `{script}`).");
        }

        if (!environment.FileExists(script))
        {
            throw new AcpLaunchException(AcpLaunchFailure.NotFound,
                $"The adapter script `{script}` does not exist. Install the adapter first (with a pinned version).");
        }
    }

    private static string NameWithoutExtension(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
