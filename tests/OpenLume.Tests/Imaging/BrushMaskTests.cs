using System.Text.Json;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class BrushMaskTests
{
    [Fact]
    public async Task ExportAppliesContinuousBrushAcrossTilesAndEraseProtectsOriginalRegion()
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using (var bitmap = new SKBitmap(513, 259))
            {
                bitmap.Erase(new SKColor(64, 64, 64));
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, data.ToArray(), TestContext.Current.CancellationToken);
            }
            var original = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
            var paint = Stroke(flow: 1);
            var erase = new BrushStroke(new([new MaskPoint(.5, .5)]), .03, .06, 0, 1, true);
            var mask = new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush, ExposureEv: 1,
                BrushStrokes: new([paint, erase]));
            using var renderer = new SkiaImageRenderer();
            await renderer.ExportAsync(source, destination, new EditRecipe(LocalMasks: new([mask])),
                new ExportOptions(ImageExportFormat.Png), TestContext.Current.CancellationToken);
            using var actual = SKBitmap.Decode(destination);
            Assert.Equal(128, actual.GetPixel(200, 129).Red);
            Assert.Equal(128, actual.GetPixel(300, 129).Red);
            Assert.Equal(64, actual.GetPixel(256, 129).Red);
            Assert.Equal(64, actual.GetPixel(256, 0).Red);
            Assert.Equal(original, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    private static BrushStroke Stroke(bool erase = false, double flow = .5) =>
        new(new ImmutableValues<MaskPoint>([new(.2, .5), new(.8, .5)]), .12, .18, .6, flow, erase);

    [Fact]
    public void ContinuousPassHasNoGapsAndSeparatePassesAccumulateThenErase()
    {
        var mask = new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            BrushStrokes: new([Stroke(), Stroke(), Stroke(erase: true)])).Normalize();
        Assert.Equal(.375, mask.Weight(.5, .5), 6);
        Assert.Equal(0, mask.Weight(.5, .9));
        Assert.Equal(.625, (mask with { Inverted = true }).Weight(.5, .5), 6);
        Assert.Equal(0, (mask with { Enabled = false }).Weight(.5, .5));
    }

    [Fact]
    public void ImmutableStrokeStateRoundTripsAndMigratesPreviousRecipes()
    {
        var points = new[] { new MaskPoint(.2, .5), new MaskPoint(.8, .5) };
        var stroke = Stroke() with { Points = new(points) };
        points[0] = new(0, 0);
        Assert.Equal(new(.2, .5), stroke.Points[0]);
        var recipe = new EditRecipe(LocalMasks: new([new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            ExposureEv: 1, BrushStrokes: new([stroke]))])).Normalize();
        Assert.Equal(recipe, JsonSerializer.Deserialize<EditRecipe>(JsonSerializer.Serialize(recipe))!.Normalize());
        var old = JsonSerializer.Deserialize<EditRecipe>("{\"Version\":10,\"ExposureEv\":1}")!.Normalize();
        Assert.Equal(EditRecipe.CurrentVersion, old.Version);
        Assert.Equal(1, old.ExposureEv);
        Assert.Empty(old.LocalMasks!);
    }

    [Fact]
    public void NormalizationBoundsMalformedAndOversizedInput()
    {
        var stroke = new BrushStroke(new(Enumerable.Repeat(new MaskPoint(double.NaN, 9), 3000)),
            RadiusX: 0, RadiusY: double.NaN, Feather: 9, Flow: -1).Normalize();
        Assert.Equal(2048, stroke.Points.Count);
        Assert.Equal(new(.5, 1), stroke.Points[0]);
        Assert.Equal(.001, stroke.RadiusX);
        Assert.Equal(.03, stroke.RadiusY);
        Assert.Equal(1, stroke.Feather);
        Assert.Equal(0, stroke.Flow);
        var mask = new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            BrushStrokes: new(Enumerable.Repeat(stroke, 200))).Normalize();
        Assert.Equal(8, mask.BrushStrokes!.Count);
    }

    [Theory]
    [InlineData(57, 31, 16)]
    [InlineData(513, 259, 256)]
    public void TiledCoverageMatchesReferenceIncludingTileBoundaries(int width, int height, int tileSize)
    {
        var mask = new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            BrushStrokes: new([Stroke(), Stroke(erase: true, flow: .3),
                new(new([new(.5, .1), new(.5, .9)]), .07, .12, 0, .8)])).Normalize();
        var rasterizer = new BrushRasterizer(mask, width, height);
        var coverage = new float[tileSize * tileSize];
        var scratch = new float[coverage.Length];
        for (var y = 0; y < height; y += tileSize)
            for (var x = 0; x < width; x += tileSize)
            {
                var w = Math.Min(tileSize, width - x);
                var h = Math.Min(tileSize, height - y);
                rasterizer.FillTile(x, y, w, h, coverage, scratch, TestContext.Current.CancellationToken);
                for (var row = 0; row < h; row++)
                    for (var column = 0; column < w; column++)
                        Assert.InRange(Math.Abs(coverage[row * w + column] -
                            mask.Weight((x + column + .5) / width, (y + row + .5) / height)), 0, 1e-6);
            }
    }

    [Fact]
    public void RasterizerRejectsAliasedBuffersAndHonorsCancellation()
    {
        var rasterizer = new BrushRasterizer(new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            BrushStrokes: new([Stroke()])), 32, 32);
        var buffer = new float[1024];
        Assert.Throws<ArgumentException>(() => rasterizer.FillTile(0, 0, 32, 32, buffer, buffer,
            TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => rasterizer.FillTile(0, 0, 32, 32, buffer, new float[1024], cancellation.Token));
    }
}
