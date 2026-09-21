using OpenLume.Core.Domain;
using OpenLume.Imaging;
using Sdcb.LibRaw;
using SkiaSharp;
namespace OpenLume.Tests.Imaging;

public sealed class SkiaImageRendererTests
{
    [Fact]
    public async Task PreviewPreservesAspectAndMaxDimension()
    {
        var path = await CreateImage(400, 200);
        try
        {
            var result = await new SkiaImageRenderer().RenderPreviewAsync(path, EditRecipe.Default, 100);
            Assert.Equal(100, result.Width);
            Assert.Equal(50, result.Height);
            Assert.Equal("image/jpeg", result.MimeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExposureChangesRenderedBrightness()
    {
        var path = await CreateImage(20, 20, new SKColor(60, 60, 60));
        try
        {
            var renderer = new SkiaImageRenderer();
            var result = await renderer.RenderPreviewAsync(path, new EditRecipe(ExposureEv: 2), 100);
            using var decoded = SKBitmap.Decode(result.Data);
            Assert.NotNull(decoded);
            Assert.True(decoded.GetPixel(0, 0).Red > 150);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportWritesJpegAndLeavesNoTempFile()
    {
        var source = await CreateImage(32, 16);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            await new SkiaImageRenderer().ExportJpegAsync(source, destination, EditRecipe.Default, 85);
            Assert.True(File.Exists(destination));
            Assert.True(new FileInfo(destination).Length > 0);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + ".*.tmp"));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public void LibRawRuntimeLoadsAndReportsSupportedCameras()
    {
        Assert.NotEmpty(RawContext.Version);
        Assert.NotEmpty(RawContext.SupportedCameras);
    }

    private static async Task<string> CreateImage(int width, int height, SKColor? color = null)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? new SKColor(100, 120, 140));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }
}
