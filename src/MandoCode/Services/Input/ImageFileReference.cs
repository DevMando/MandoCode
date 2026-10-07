namespace MandoCode.Services;

/// <summary>Routes image references without treating binary image data as prompt text.</summary>
public static class ImageFileReference
{
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".svg" or ".tif" or ".tiff" or ".ico" or ".avif";
    public static (byte[] Bytes, string MediaType) Read(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new IOException("The image must be inside the project directory.");
        using var file = File.OpenRead(full);
        if (file.Length > AIService.MaxImageInputBytes) throw new IOException("The image exceeds the 4 MiB attachment limit.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        var type = DetectMediaType(bytes);
        if (type is null) throw new IOException("Use a PNG, JPEG, GIF, or WebP image. This file has an unsupported or invalid image format.");
        return (bytes, type);
    }
    internal static string? DetectMediaType(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return "image/jpeg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
