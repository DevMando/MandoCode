using System.Diagnostics;
using System.Text;

namespace MandoCode.Services;

public interface IClipboardImageReader
{
    Task<byte[]?> ReadPngAsync(CancellationToken cancellationToken);
}

public interface IClipboardTextReader
{
    Task<string?> ReadTextAsync(CancellationToken cancellationToken);
}

/// <summary>Read only on an explicit paste, using Windows' STA clipboard API in a hidden helper.</summary>
public sealed class WindowsClipboardImageReader : IClipboardImageReader, IClipboardTextReader
{
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $png = [System.Windows.Forms.Clipboard]::GetData('PNG')
        if ($png -is [System.IO.MemoryStream]) {
            if ($png.Length -gt 4194304) { throw 'Clipboard image exceeds the 4 MiB attachment limit.' }
            [Console]::Out.Write([Convert]::ToBase64String($png.ToArray()))
            exit 0
        }
        $image = [System.Windows.Forms.Clipboard]::GetImage()
        if ($null -eq $image) { exit 0 }
        try {
            if ([long]$image.Width * $image.Height -gt 16777216) { throw 'Clipboard image is too large. Copy a smaller snippet.' }
            $stream = New-Object System.IO.MemoryStream
            try {
                $image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                if ($stream.Length -gt 4194304) { throw 'Clipboard image exceeds the 4 MiB attachment limit.' }
                [Console]::Out.Write([Convert]::ToBase64String($stream.ToArray()))
            } finally { $stream.Dispose() }
        } finally { $image.Dispose() }
        """;

    internal const string TextScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Windows.Forms
        if (![System.Windows.Forms.Clipboard]::ContainsText()) { exit 0 }
        $text = [System.Windows.Forms.Clipboard]::GetText()
        if ($text.Length -gt 1000000) { throw 'Clipboard text is too large.' }
        [Console]::Out.Write([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text)))
        """;
    public async Task<byte[]?> ReadPngAsync(CancellationToken cancellationToken) => await ReadBytesAsync(Script, cancellationToken);
    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(TextScript, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }
    private async Task<byte[]?> ReadBytesAsync(string script, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Clipboard paste is currently supported on Windows. Use terminal paste for text or @ to attach an image file.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not open the clipboard reader.");
        try {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var encoded = (await output).Trim();
            var details = await error;
            if (process.ExitCode != 0) throw new IOException(details.Contains("4 MiB", StringComparison.Ordinal) || details.Contains("too large", StringComparison.Ordinal)
                ? script == TextScript ? "Clipboard text is too large (maximum 1,000,000 characters)." : "Clipboard image is too large. Copy a smaller snippet (maximum 4 MiB)."
                : "Could not read the clipboard. Copy it again and retry Alt+V.");
            return encoded.Length == 0 ? null : Convert.FromBase64String(encoded);
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }
}

/// <summary>Agent-local image references. Never writes clipboard content into the project.</summary>
public sealed class ClipboardImageStore
{
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    internal const int Capacity = 8;
    public string Add(byte[] png)
    {
        if (png.Length > AIService.MaxImageInputBytes) throw new IOException("Clipboard image exceeds the 4 MiB attachment limit.");
        if (ImageFileReference.DetectMediaType(png) != "image/png") throw new IOException("The clipboard did not contain a valid PNG image.");
        var reference = "clipboard-" + Guid.NewGuid().ToString("N") + ".png";
        _images.Add(reference, png.ToArray()); _order.Enqueue(reference);
        while (_order.Count > Capacity) _images.Remove(_order.Dequeue());
        return reference;
    }
    public bool TryGet(string reference, out byte[] png) => _images.TryGetValue(reference, out png!);
}