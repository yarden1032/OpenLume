using System.Diagnostics;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using Sdcb.LibRaw;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

[CollectionDefinition("Renderer budget", DisableParallelization = true)]
public sealed class RendererBudgetTestGroup;

[Collection("Renderer budget")]
public sealed class SkiaImageRendererTests
{
    [Theory]
    [InlineData(ExportFormat.Jpeg)]
    [InlineData(ExportFormat.Png)]
    public async Task ExportOptionsApplyCropBeforeSizeLimitAndEmbedSrgb(ExportFormat format)
    {
        var source = await CreateImage(120, 80, new SKColor(60, 80, 100));
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + (format == ExportFormat.Png ? ".png" : ".jpg"));
        try
        {
            var original = await File.ReadAllBytesAsync(source);
            using var renderer = new SkiaImageRenderer();
            await renderer.ExportAsync(source, destination,
                new EditRecipe(ExposureEv: 1, Crop: new CropGeometry(Width: .5)),
                new ExportOptions(format, Quality: 100, MaxDimension: 40));
            using var codec = SKCodec.Create(destination);
            Assert.Equal(format == ExportFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.Equal(30, codec.Info.Width);
            Assert.Equal(40, codec.Info.Height);
            Assert.True(codec.Info.ColorSpace?.IsSrgb);
            using var bitmap = SKBitmap.Decode(destination);
            AssertColorNear(new SKColor(120, 160, 200), bitmap.GetPixel(15, 20), 3);
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + ".*.tmp"));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public async Task PngExportIsLosslessAndDoesNotUpscale()
    {
        var source = await CreateImage(32, 16, new SKColor(61, 83, 107));
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using var renderer = new SkiaImageRenderer();
            await renderer.ExportAsync(source, destination, EditRecipe.Default,
                new ExportOptions(ExportFormat.Png, Quality: 1, MaxDimension: 200));
            using var bitmap = SKBitmap.Decode(destination);
            Assert.Equal(32, bitmap.Width);
            Assert.Equal(16, bitmap.Height);
            Assert.Equal(new SKColor(61, 83, 107), bitmap.GetPixel(10, 10));
            await Assert.ThrowsAsync<IOException>(() => renderer.ExportAsync(source, destination,
                EditRecipe.Default, new ExportOptions(ExportFormat.Png)));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public void ExportOptionsRejectInvalidFormatAndDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportOptions((ExportFormat)99).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportOptions(MaxDimension: -1).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportOptions(MaxDimension: 32769).Normalize());
        Assert.Equal(100, new ExportOptions(Quality: 900).Normalize().Quality);
        Assert.Equal(0, new ExportOptions(Quality: -50).Normalize().Quality);
    }

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
    public async Task WideGamutRasterConvertsToAndEmbedsSrgbInPreviewAndJpegExport()
    {
        var source = await CreateDisplayP3Image();
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            var originalBytes = await File.ReadAllBytesAsync(source);
            using var sourceCodec = SKCodec.Create(source);
            Assert.NotNull(sourceCodec?.Info.ColorSpace);
            Assert.False(sourceCodec!.Info.ColorSpace!.IsSrgb);
            using var sourceColorSpace = SKColorSpace.CreateSrgb();
            var srgbInfo = new SKImageInfo(
                sourceCodec.Info.Width, sourceCodec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul, sourceColorSpace);
            using var expected = SKBitmap.Decode(source, srgbInfo);
            Assert.NotNull(expected);
            using var unconverted = SKBitmap.Decode(source);
            Assert.NotNull(unconverted);
            Assert.NotEqual(unconverted!.GetPixel(24, 24), expected!.GetPixel(24, 24));

            using var renderer = new SkiaImageRenderer();
            var preview = await renderer.RenderPreviewAsync(source, EditRecipe.Default, 200);
            await renderer.ExportJpegAsync(source, destination, EditRecipe.Default, 100);

            using var previewData = SKData.CreateCopy(preview.Data);
            using var previewCodec = SKCodec.Create(previewData);
            using var exportCodec = SKCodec.Create(destination);
            Assert.True(previewCodec?.Info.ColorSpace?.IsSrgb);
            Assert.True(exportCodec?.Info.ColorSpace?.IsSrgb);
            using var previewBitmap = SKBitmap.Decode(previewData);
            using var exportBitmap = SKBitmap.Decode(destination);
            Assert.NotNull(previewBitmap);
            Assert.NotNull(exportBitmap);
            AssertColorNear(expected.GetPixel(24, 24), previewBitmap!.GetPixel(24, 24), tolerance: 8);
            AssertColorNear(expected.GetPixel(24, 24), exportBitmap!.GetPixel(24, 24), tolerance: 8);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
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
                new EditRecipe(
                    Texture: 20,
                    Clarity: 18,
                    Dehaze: 10,
                    Sharpening: 35,
                    NoiseReduction: 20,
                    Grain: 8,
                    Optics: new OpticsCorrections(
                        Distortion: -8,
                        ChromaticAberration: 20,
                        LensVignette: 12,
                        VignetteMidpoint: 45),
                    ToneCurve: new ParametricToneCurve(Highlights: -12, Lights: 8, Darks: -6, Shadows: 5),
                    ColorMixer: new HslColorMixer(
                        Orange: new HslChannelAdjustment(Hue: -6, Saturation: 8, Luminance: 4),
                        Blue: new HslChannelAdjustment(Hue: 5, Saturation: 10, Luminance: -5))),
                1800);
            stopwatch.Stop();

            Assert.Equal(1800, result.Width);
            // Coverage instrumentation on the Windows CI runner adds substantial per-pixel overhead.
            // Keep the normal runtime target strict while allowing only that known test environment headroom.
            var budget = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(5);
            Assert.True(stopwatch.Elapsed < budget,
                $"Presence/detail preview took {stopwatch.Elapsed}; budget was {budget}.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ToneCurveTargetsBrightAndDarkRegionsDifferently()
    {
        var path = await CreateSplitToneImage(new SKColor(35, 35, 35), new SKColor(200, 200, 200));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var edited = await renderer.RenderPreviewAsync(
                path,
                new EditRecipe(ToneCurve: new ParametricToneCurve(Highlights: 85)),
                200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var editedBitmap = SKBitmap.Decode(edited.Data);

            var shadowChange = Math.Abs(editedBitmap.GetPixel(25, 50).Red - originalBitmap.GetPixel(25, 50).Red);
            var highlightChange = editedBitmap.GetPixel(175, 50).Red - originalBitmap.GetPixel(175, 50).Red;
            Assert.True(highlightChange > 8, $"Expected lifted highlights, observed {highlightChange}.");
            Assert.True(shadowChange < 8, $"Expected stable shadows, observed {shadowChange}.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ToneCurvePreviewAndExportUseTheSameRenderPath()
    {
        var source = await CreateSplitToneImage(new SKColor(45, 55, 65), new SKColor(185, 195, 205));
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            using var renderer = new SkiaImageRenderer();
            var recipe = new EditRecipe(ToneCurve: new ParametricToneCurve(
                Highlights: -22,
                Lights: 16,
                Darks: -12,
                Shadows: 18));
            var preview = await renderer.RenderPreviewAsync(source, recipe, 200);
            await renderer.ExportJpegAsync(source, destination, recipe, 90);

            Assert.Equal(preview.Data, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task ColorMixerTargetsTheSelectedHueBand()
    {
        var path = await CreateColorBandImage();
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 200);
            var edited = await renderer.RenderPreviewAsync(
                path,
                new EditRecipe(ColorMixer: new HslColorMixer(
                    Red: new HslChannelAdjustment(Luminance: -80))),
                200);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var editedBitmap = SKBitmap.Decode(edited.Data);

            var redChange = ColorDistance(originalBitmap.GetPixel(25, 25), editedBitmap.GetPixel(25, 25));
            var blueChange = ColorDistance(originalBitmap.GetPixel(175, 25), editedBitmap.GetPixel(175, 25));
            Assert.True(redChange > 80, $"Expected a strong red adjustment, observed {redChange}.");
            Assert.True(blueChange < 12, $"Expected blue to remain stable, observed {blueChange}.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ColorMixerRenderingIsDeterministic()
    {
        var path = await CreateColorBandImage();
        try
        {
            using var renderer = new SkiaImageRenderer();
            var recipe = new EditRecipe(ColorMixer: new HslColorMixer(
                Orange: new HslChannelAdjustment(Hue: 35, Saturation: 22, Luminance: -9),
                Blue: new HslChannelAdjustment(Hue: -18, Saturation: 15, Luminance: 12)));
            var first = await renderer.RenderPreviewAsync(path, recipe, 200);
            var second = await renderer.RenderPreviewAsync(path, recipe, 200);

            Assert.Equal(first.Data, second.Data);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRejectsOriginalIncludingEquivalentPathAndPreservesBytes(bool equivalentPath)
    {
        var source = await CreateImage(32, 16);
        try
        {
            var before = await File.ReadAllBytesAsync(source);
            var destination = equivalentPath
                ? Path.Combine(Path.GetDirectoryName(source)!, ".", Path.GetFileName(source))
                : source;
            using var renderer = new SkiaImageRenderer();
            var error = await Assert.ThrowsAsync<IOException>(() => renderer.ExportJpegAsync(
                source, destination, new EditRecipe(ExposureEv: 2), 85));
            Assert.Contains("original", error.Message);
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(source)!, "." + Path.GetFileName(source) + ".*.tmp"));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task ExportPreservesAnyExistingDestination()
    {
        var source = await CreateImage(32, 16);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        byte[] existing = [1, 2, 3, 4];
        try
        {
            await File.WriteAllBytesAsync(destination, existing);
            using var renderer = new SkiaImageRenderer();
            await Assert.ThrowsAsync<IOException>(() => renderer.ExportJpegAsync(source, destination, EditRecipe.Default, 85));
            Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + ".*.tmp"));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public async Task CancelledExportDoesNotCreateOutputOrChangeSource()
    {
        var source = await CreateImage(32, 16);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            var before = await File.ReadAllBytesAsync(source);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var renderer = new SkiaImageRenderer();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.ExportJpegAsync(
                source, destination, EditRecipe.Default, 85, cancellation.Token));
            Assert.False(File.Exists(destination));
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public async Task CropAndQuarterTurnProduceExpectedDimensions()
    {
        var source = await CreateImage(120, 80);
        try
        {
            using var renderer = new SkiaImageRenderer();
            var recipe = new EditRecipe(Crop: new CropGeometry(
                X: .25,
                Y: .1,
                Width: .5,
                Height: .75,
                QuarterTurns: 1));

            var result = await renderer.RenderPreviewAsync(source, recipe, 500);

            Assert.Equal(40, result.Width);
            Assert.Equal(90, result.Height);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task FlipHorizontalChangesPixelPlacementWithoutChangingDimensions()
    {
        var source = await CreateSplitToneImage(
            new SKColor(220, 35, 35),
            new SKColor(35, 55, 220));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var result = await renderer.RenderPreviewAsync(
                source,
                new EditRecipe(Crop: CropGeometry.FullFrame with { FlipHorizontal = true }),
                500);
            using var bitmap = SKBitmap.Decode(result.Data);

            Assert.Equal(200, bitmap.Width);
            Assert.Equal(100, bitmap.Height);
            Assert.True(bitmap.GetPixel(20, 50).Blue > bitmap.GetPixel(20, 50).Red);
            Assert.True(bitmap.GetPixel(180, 50).Red > bitmap.GetPixel(180, 50).Blue);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task CropGeometryPreviewAndExportUseTheSameRenderPath()
    {
        var source = await CreateColorBandImage();
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            using var renderer = new SkiaImageRenderer();
            var recipe = new EditRecipe(
                RotationDegrees: 1.5,
                Crop: new CropGeometry(.1, .15, .72, .65, 3, true));
            var preview = await renderer.RenderPreviewAsync(source, recipe, 500);
            await renderer.ExportJpegAsync(source, destination, recipe, 90);

            Assert.Equal(preview.Data, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task LensDistortionWarpsEdgesWhilePreservingDimensions()
    {
        var source = await CreateImage(160, 100, new SKColor(150, 150, 150));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var result = await renderer.RenderPreviewAsync(
                source,
                new EditRecipe(Optics: new OpticsCorrections(Distortion: 100)),
                500);
            using var bitmap = SKBitmap.Decode(result.Data);

            Assert.Equal(160, bitmap.Width);
            Assert.Equal(100, bitmap.Height);
            Assert.True(bitmap.GetPixel(3, 3).Red + 60 < bitmap.GetPixel(80, 50).Red);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task ChromaticAberrationCorrectionReducesEdgeFringing()
    {
        var source = await CreateChromaticFringeImage();
        try
        {
            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(source, EditRecipe.Default, 500);
            var corrected = await renderer.RenderPreviewAsync(
                source,
                new EditRecipe(Optics: new OpticsCorrections(ChromaticAberration: 100)),
                500);
            using var originalBitmap = SKBitmap.Decode(original.Data);
            using var correctedBitmap = SKBitmap.Decode(corrected.Data);
            var originalSeparation = ChannelSeparation(originalBitmap);
            var correctedSeparation = ChannelSeparation(correctedBitmap);

            Assert.True(correctedSeparation < originalSeparation * .85,
                $"Expected reduced color fringing; original {originalSeparation:F0}, corrected {correctedSeparation:F0}.");
        }
        finally { File.Delete(source); }
    }

    [Theory]
    [InlineData(255, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, 255, 0, 0, 0)]
    [InlineData(255, 0, 255, 255, 255, 255)]
    [InlineData(0, 255, 0, 255, 0, 0)]
    [InlineData(255, 255, 255, 255, 255, 255)]
    public async Task ChromaticAberrationPreservesCleanColorEdgesAndImageBorders(
        byte red, byte green, byte blue, byte backgroundRed, byte backgroundGreen, byte backgroundBlue)
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using (var bitmap = new SKBitmap(200, 100))
            {
                bitmap.Erase(new SKColor(backgroundRed, backgroundGreen, backgroundBlue));
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint { Color = new SKColor(red, green, blue) };
                canvas.DrawRect(20, 0, 160, 100, paint);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, data.ToArray());
            }

            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(source, EditRecipe.Default, 500);
            var corrected = await renderer.RenderPreviewAsync(source,
                new EditRecipe(Optics: new OpticsCorrections(ChromaticAberration: 100)), 500);
            Assert.Equal(original.Data, corrected.Data);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task PositiveLensVignetteCorrectionLiftsEdgesMoreThanCenter()
    {
        var source = await CreateImage(180, 180, new SKColor(105, 105, 105));
        try
        {
            using var renderer = new SkiaImageRenderer();
            var result = await renderer.RenderPreviewAsync(
                source,
                new EditRecipe(Optics: new OpticsCorrections(
                    LensVignette: 80,
                    VignetteMidpoint: 35)),
                500);
            using var bitmap = SKBitmap.Decode(result.Data);

            Assert.True(bitmap.GetPixel(5, 5).Red > bitmap.GetPixel(90, 90).Red + 45);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task OpticsPreviewAndExportUseTheSameRenderPath()
    {
        var source = await CreateOpticsLineImage();
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            using var renderer = new SkiaImageRenderer();
            var recipe = new EditRecipe(Optics: new OpticsCorrections(-16, 32, 14, 48));
            var preview = await renderer.RenderPreviewAsync(source, recipe, 500);
            await renderer.ExportJpegAsync(source, destination, recipe, 90);

            Assert.Equal(preview.Data, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
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

    private static async Task<string> CreateDisplayP3Image()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var colorSpace = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bitmap = new SKBitmap(new SKImageInfo(
            48, 48, SKColorType.Rgba8888, SKAlphaType.Opaque, colorSpace));
        bitmap.Erase(new SKColor(230, 80, 35));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static void AssertColorNear(SKColor expected, SKColor actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, tolerance);
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

    private static async Task<string> CreateColorBandImage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(200, 50);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(35, 80, 220));
        using var red = new SKPaint { Color = new SKColor(220, 45, 35) };
        canvas.DrawRect(0, 0, 100, 50, red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static async Task<string> CreateOpticsLineImage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.Black);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, StrokeWidth = 3 };
        canvas.DrawLine(22, 0, 22, 100, paint);
        canvas.DrawLine(178, 0, 178, 100, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static async Task<string> CreateChromaticFringeImage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.Black);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)(x >= 18 && x < 182 ? 255 : 0),
                    (byte)(x >= 20 && x < 180 ? 255 : 0),
                    (byte)(x >= 22 && x < 178 ? 255 : 0)));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray());
        return path;
    }

    private static double ChannelSeparation(SKBitmap bitmap) => bitmap.Pixels.Sum(pixel =>
        Math.Abs(pixel.Red - pixel.Green) + Math.Abs(pixel.Blue - pixel.Green));

    private static int ColorDistance(SKColor first, SKColor second) =>
        Math.Abs(first.Red - second.Red) + Math.Abs(first.Green - second.Green) + Math.Abs(first.Blue - second.Blue);

    private static double PixelVariance(SKBitmap bitmap)
    {
        var values = bitmap.Pixels.Select(pixel => (double)pixel.Red).ToArray();
        var mean = values.Average();
        return values.Select(value => Math.Pow(value - mean, 2)).Average();
    }
}
