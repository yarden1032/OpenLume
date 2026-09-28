using OpenLume.Core.Domain;
using OpenLume.Imaging;
using Sdcb.LibRaw;
using SkiaSharp;
using System.Diagnostics;
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
    public async Task DehazeAndClarityIncreaseSeparationAcrossASoftEdge()
    {
        var path = await CreateSplitToneImage(new SKColor(95, 100, 105), new SKColor(165, 170, 175));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var edited = await renderer.RenderPreviewAsync(path, new EditRecipe(Dehaze: 70, Clarity: 70), 200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var editedBitmap = SKBitmap.Decode(edited.Data);
            var originalSeparation = originalBitmap.GetPixel(150, 50).Red - originalBitmap.GetPixel(50, 50).Red;
            var editedSeparation = editedBitmap.GetPixel(150, 50).Red - editedBitmap.GetPixel(50, 50).Red;

            Assert.True(editedSeparation > originalSeparation + 10,
                $"Expected stronger tonal separation; original {originalSeparation}, edited {editedSeparation}.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NoiseReductionLowersCheckerboardVariance()
    {
        var path = await CreateCheckerImage();
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var denoised = await renderer.RenderPreviewAsync(path, new EditRecipe(NoiseReduction: 100), 200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var denoisedBitmap = SKBitmap.Decode(denoised.Data);

            Assert.True(PixelVariance(denoisedBitmap) < PixelVariance(originalBitmap) * .7);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SharpeningIncreasesContrastAtAnEdge()
    {
        var path = await CreateSplitToneImage(new SKColor(85, 85, 85), new SKColor(170, 170, 170));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var sharpened = await renderer.RenderPreviewAsync(path, new EditRecipe(Sharpening: 100), 200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var sharpenedBitmap = SKBitmap.Decode(sharpened.Data);
            var originalEdge = originalBitmap.GetPixel(101, 50).Red - originalBitmap.GetPixel(98, 50).Red;
            var sharpenedEdge = sharpenedBitmap.GetPixel(101, 50).Red - sharpenedBitmap.GetPixel(98, 50).Red;

            Assert.True(sharpenedEdge > originalEdge,
                $"Expected sharper edge; original {originalEdge}, sharpened {sharpenedEdge}.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GrainIsDeterministicAndAddsVariation()
    {
        var path = await CreateImage(120, 120, new SKColor(128, 128, 128));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var first = await renderer.RenderPreviewAsync(path, new EditRecipe(Grain: 70), 200);
            var second = await renderer.RenderPreviewAsync(path, new EditRecipe(Grain: 70), 200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var grainBitmap = SKBitmap.Decode(first.Data);

            Assert.Equal(first.Data, second.Data);
            Assert.True(PixelVariance(grainBitmap) > PixelVariance(originalBitmap) + 20);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PresenceAndDetailPreviewKeepsAnInteractiveDisplayBudget()
    {
        var path = await CreateImage(2400, 1600, new SKColor(105, 125, 145));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var stopwatch = Stopwatch.StartNew();
            var result = await renderer.RenderPreviewAsync(
                path,
                new EditRecipe(Texture: 20, Clarity: 18, Dehaze: 10, Sharpening: 35, NoiseReduction: 20, Grain: 8),
                1800);
            stopwatch.Stop();

            Assert.Equal(1800, result.Width);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Presence/detail preview took {stopwatch.Elapsed}.");
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

    private static async Task<string> CreateSplitToneImage(
        SKColor? darkColor = null,
        SKColor? lightColor = null)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(lightColor ?? new SKColor(220, 220, 220));
        using var dark = new SKPaint { Color = darkColor ?? new SKColor(35, 35, 35) };
        canvas.DrawRect(0, 0, 100, 100, dark);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static async Task<string> CreateCheckerImage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(160, 160);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var value = ((x + y) & 1) == 0 ? (byte)70 : (byte)190;
            bitmap.SetPixel(x, y, new SKColor(value, value, value));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static double PixelVariance(SKBitmap bitmap)
    {
        var values = bitmap.Pixels.Select(pixel => (double)pixel.Red).ToArray();
        var mean = values.Average();
        return values.Select(value => Math.Pow(value - mean, 2)).Average();
    }
}
