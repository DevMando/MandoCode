using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ImageFileReferenceTests
{
    [Fact]
    public void ReadsImageBytesWithMimeTypeInsteadOfText()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
            File.WriteAllBytes(Path.Combine(root, "photo.PNG"), bytes);
            Assert.True(ImageFileReference.IsImage("photo.PNG"));
            var image = ImageFileReference.Read(root, "photo.PNG");
            Assert.Equal(bytes, image.Bytes); Assert.Equal("image/png", image.MediaType);
            Assert.Throws<IOException>(() => ImageFileReference.Read(root, "../outside.png"));
            File.WriteAllText(Path.Combine(root, "fake.png"), "plain text");
            Assert.Throws<IOException>(() => ImageFileReference.Read(root, "fake.png"));
            File.WriteAllBytes(Path.Combine(root, "big.png"), new byte[AIService.MaxImageInputBytes + 1]);
            Assert.Throws<IOException>(() => ImageFileReference.Read(root, "big.png"));
        } finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void UnsupportedImageExtensionsStillBypassTextRouting()
    {
        Assert.True(ImageFileReference.IsImage("diagram.svg"));
        Assert.True(ImageFileReference.IsImage("scan.tiff"));
        Assert.False(ImageFileReference.IsImage("source.cs"));
        Assert.Equal("image/jpeg", ImageFileReference.DetectMediaType([255, 216, 255]));
        Assert.Equal("image/gif", ImageFileReference.DetectMediaType("GIF89a"u8.ToArray()));
        Assert.Equal("image/webp", ImageFileReference.DetectMediaType("RIFF0000WEBP"u8.ToArray()));
    }
}
