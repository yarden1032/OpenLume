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
    public async Task ColorAndRotationControlsChangePreview()
    {
        var path = await CreateImage(200, 100, new SKColor(80, 120, 160));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 300);
            var edited = await renderer.RenderPreviewAsync(
                path,
                new EditRecipe(
                    Contrast: 25,
                    Saturation: 35,
                    Temperature: 40,
                    Tint: 20,
                    RotationDegrees: 10),
                300);

            Assert.NotEqual(original.Data, edited.Data);
            Assert.True(edited.Width > original.Width);
            Assert.True(edited.Height > original.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ShadowsAndHighlightsTargetDifferentTonalRegions()
    {
        var path = await CreateSplitToneImage();
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var edited = await renderer.RenderPreviewAsync(
                path,
                new EditRecipe(Shadows: 70, Highlights: -70),
                200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var editedBitmap = SKBitmap.Decode(edited.Data);

            var darkLift = editedBitmap.GetPixel(20, 50).Red - originalBitmap.GetPixel(20, 50).Red;
            var brightReduction = originalBitmap.GetPixel(180, 50).Red - editedBitmap.GetPixel(180, 50).Red;
            Assert.True(darkLift > 20, $"Expected lifted shadows, observed {darkLift}.");
            Assert.True(brightReduction > 20, $"Expected recovered highlights, observed {brightReduction}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NegativeVignetteDarkensCornersMoreThanCenter()
    {
        var path = await CreateImage(200, 200, new SKColor(160, 160, 160));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var result = await renderer.RenderPreviewAsync(path, new EditRecipe(Vignette: -80), 200);
            using var bitmap = SKBitmap.Decode(result.Data);

            Assert.True(bitmap.GetPixel(4, 4).Red + 50 < bitmap.GetPixel(100, 100).Red);
        }
        finally
        {
            File.Delete(path);
        }
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

    [Fact]
    public async Task HistogramTracksDominantRgbChannels()
    {
        var path = await CreateImage(32, 32, new SKColor(240, 80, 20));
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var histogram = ImageHistogramCalculator.Calculate(bytes, 64);

            Assert.Equal(64, histogram.Red.Count);
            Assert.True(histogram.Red[60] > 0);
            Assert.True(histogram.Green[20] > 0);
            Assert.True(histogram.Blue[5] > 0);
            Assert.True(histogram.Peak > 0);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static async Task<string> CreateSplitToneImage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(220, 220, 220));
        using var dark = new SKPaint { Color = new SKColor(35, 35, 35) };
        canvas.DrawRect(0, 0, 100, 100, dark);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }
}
