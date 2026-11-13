using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Protocol;

namespace MandoCode.Services.Acp;

/// <summary>
/// What the agent may call back into: permission questions and file access. A capability is advertised only when its
/// handler is connected, so an agent is never offered something that would be refused.
/// </summary>
public sealed class AcpClientHandler : IAcpClientHandler
{
    private readonly AcpPermissionPrompter? _permissions;
    private readonly AcpFileSystemHandler? _files;

    public AcpClientHandler(AcpPermissionPrompter? permissions, AcpFileSystemHandler? files)
    {
        _permissions = permissions;
        _files = files;
    }

    public ClientCapabilities Capabilities => new()
    {
        Fs = new FileSystemCapabilities { ReadTextFile = _files is not null, WriteTextFile = _files is not null },
    };

    public ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken) =>
        _permissions is null
            ? ValueTask.FromResult(new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() })
            : _permissions.RequestAsync(request, cancellationToken);

    public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken) =>
        _files is null
            ? throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "fs/read_text_file is not supported.")
            : ValueTask.FromResult(_files.Read(request));

    public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken) =>
        _files is null
            ? throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "fs/write_text_file is not supported.")
            : ValueTask.FromResult(_files.Write(request));
}
