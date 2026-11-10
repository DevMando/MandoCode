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
