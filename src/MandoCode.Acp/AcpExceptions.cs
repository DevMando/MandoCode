using System.Text.Json;

namespace MandoCode.Acp;

/// <summary>An error response (or an error to be sent as a response) in JSON-RPC terms.</summary>
public sealed class JsonRpcException : Exception
{
    public int Code { get; }
    /// <summary>The optional <c>data</c> member of the error object.</summary>
    public JsonElement? ErrorData { get; }

    public JsonRpcException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public override string ToString() => $"JSON-RPC error {Code}: {Message}";
}

/// <summary>
/// The connection to the agent ended (the stream reached EOF, the write side broke, or the
/// connection was disposed) while a request was pending or about to be sent.
/// </summary>
public sealed class AcpConnectionClosedException : Exception
{
    /// <summary>Exit code of the agent process, when known.</summary>
    public int? ExitCode { get; }

    /// <summary>The last lines the agent wrote to stderr, when known.</summary>
    public string? StderrTail { get; }

    public AcpConnectionClosedException(string message, int? exitCode = null, string? stderrTail = null, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
        StderrTail = stderrTail;
    }
}

/// <summary>
/// The peer broke the wire protocol: a line that is too long, or repeated non-JSON output
/// (typically an agent that logs to stdout instead of stderr).
/// </summary>
public sealed class AcpProtocolViolationException : Exception
{
    public AcpProtocolViolationException(string message) : base(message) { }
}

/// <summary>Why an agent command could not be launched.</summary>
public enum AcpLaunchFailure
{
    /// <summary>The command (or script argument) does not exist or is not on PATH.</summary>
    NotFound,

    /// <summary>The command is not a real native executable (a .cmd/.bat shim, a shell script, ...).</summary>
    NotANativeExecutable,

    /// <summary>The command is something MandoCode refuses to run: <c>npx</c>-style downloaders and shells.</summary>
    Forbidden,

    /// <summary>The arguments are not acceptable (for example a relative script path for <c>node</c>).</summary>
    InvalidArguments,

    /// <summary>The operating system refused to start the process.</summary>
    StartFailed,
}

/// <summary>A launch was refused or failed; <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class AcpLaunchException : Exception
{
    public AcpLaunchFailure Failure { get; }

    public AcpLaunchException(AcpLaunchFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }
}

/// <summary>
/// The agent is not signed in (JSON-RPC error <c>-32000</c>, "auth required"). MandoCode does not call
/// <c>authenticate</c>: the user signs in with the agent's own tooling (for Claude Code: <c>claude auth login</c>).
/// </summary>
public sealed class AcpAuthRequiredException : Exception
{
    /// <summary>The authentication methods the agent advertised in <c>initialize</c>.</summary>
    public IReadOnlyList<Protocol.AuthMethod> AuthMethods { get; }

    public AcpAuthRequiredException(string message, IReadOnlyList<Protocol.AuthMethod> authMethods, Exception? inner = null)
        : base(message, inner)
    {
        AuthMethods = authMethods;
    }
}

/// <summary>The agent does not speak ACP protocol version 1.</summary>
public sealed class AcpProtocolVersionException : Exception
{
    public int AgentVersion { get; }

    public AcpProtocolVersionException(int agentVersion)
        : base($"The agent speaks ACP protocol version {agentVersion}, but MandoCode only supports version {Protocol.AcpProtocol.Version}.")
    {
        AgentVersion = agentVersion;
    }
}

/// <summary>An operation did not finish in time (startup handshake, for example).</summary>
public sealed class AcpTimeoutException : Exception
{
    public AcpTimeoutException(string operation, TimeSpan timeout)
        : base($"The agent did not complete '{operation}' within {timeout.TotalSeconds:0} seconds.")
    {
    }
}
