using MandoCode.Acp.Protocol;

namespace MandoCode.Acp.Client;

/// <summary>
/// What the application does when the agent calls back into the client. All methods may be
/// invoked concurrently (a permission prompt can be open while the agent also reads a file).
/// </summary>
public interface IAcpClientHandler
{
    /// <summary>
    /// The capabilities advertised to the agent in <c>initialize</c>. Only advertise what the
    /// handler implements: methods for unadvertised capabilities are answered with "method not found".
    /// </summary>
    ClientCapabilities Capabilities => new();

    /// <summary>
    /// Asks the user to approve a tool call. Return the chosen option, or a cancelled outcome.
    /// The token is cancelled when the turn is cancelled or the connection closes.
    /// </summary>
    ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() });

    /// <summary>Implements <c>fs/read_text_file</c> (only called when <see cref="Capabilities"/> advertises it).</summary>
    ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken) =>
        throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "fs/read_text_file is not supported.");

    /// <summary>Implements <c>fs/write_text_file</c> (only called when <see cref="Capabilities"/> advertises it).</summary>
    ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken) =>
        throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "fs/write_text_file is not supported.");
}

/// <summary>A handler that advertises nothing and refuses every permission request. Used by connection probes.</summary>
public sealed class DenyAllClientHandler : IAcpClientHandler
{
    public static DenyAllClientHandler Instance { get; } = new();
}
