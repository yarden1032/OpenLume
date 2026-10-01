using System.Text.Json;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using OpenLume.Infrastructure.Presets;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class SharpeningMaskTests
{
    [Fact]
    public void MigrationAndXmpPreserveExpectedDefaults()
    {
        var recipe = JsonSerializer.Deserialize<EditRecipe>("{\"Version\":8,\"Sharpening\":35}")!.Normalize();
        Assert.Equal(.8, recipe.SharpeningRadius);
        Assert.Equal(0, recipe.SharpeningMasking);
        var bounded = new EditRecipe(SharpeningRadius: double.NaN, SharpeningMasking: 900).Normalize();
        Assert.Equal(.8, bounded.SharpeningRadius);
        Assert.Equal(100, bounded.SharpeningMasking);
        var imported = new XmpPresetImporter().Import("<x xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/' crs:SharpenRadius='1.5' crs:SharpenEdgeMasking='70'/>");
        Assert.Equal(1.5, imported.Recipe.SharpeningRadius);
        Assert.Equal(70, imported.Recipe.SharpeningMasking);
    }

    [Fact]
    public async Task MaskProtectsLowContrastNoiseAndRetainsStrongEdges()
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var export = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            using (var bitmap = new SKBitmap(128, 64))
            {
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var value = (byte)((x < 64 ? 60 : 190) + ((x + y) % 2 == 0 ? 3 : -3));
                        bitmap.SetPixel(x, y, new SKColor(value, value, value));
                    }
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, encoded.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            }

            using var renderer = new SkiaImageRenderer();
            var baseRecipe = new EditRecipe(Sharpening: 100, SharpeningRadius: 1);
            var unmasked = await renderer.RenderPreviewAsync(source, baseRecipe, 256, cancellationToken: TestContext.Current.CancellationToken);
            var recipe = baseRecipe with { SharpeningMasking = 100 };
            var masked = await renderer.RenderPreviewAsync(source, recipe, 256, cancellationToken: TestContext.Current.CancellationToken);
            var original = await renderer.RenderPreviewAsync(source, EditRecipe.Default, 256, cancellationToken: TestContext.Current.CancellationToken);
            using var plainBitmap = SKBitmap.Decode(original.Data);
            using var unmaskedBitmap = SKBitmap.Decode(unmasked.Data);
            using var maskedBitmap = SKBitmap.Decode(masked.Data);
            Assert.True(Variance(maskedBitmap) < Variance(unmaskedBitmap) * .7);
            var originalEdge = plainBitmap.GetPixel(64, 32).Red - plainBitmap.GetPixel(63, 32).Red;
            var maskedEdge = maskedBitmap.GetPixel(64, 32).Red - maskedBitmap.GetPixel(63, 32).Red;
            Assert.True(maskedEdge > originalEdge + 15);
            await renderer.ExportJpegAsync(source, export, recipe, 90, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(masked.Data, await File.ReadAllBytesAsync(export, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(source);
            File.Delete(export);
        }
    }

    private static double Variance(SKBitmap bitmap)
    {
        var values = Enumerable.Range(16, 32).SelectMany(x => Enumerable.Range(16, 32).Select(y => (double)bitmap.GetPixel(x, y).Red)).ToArray();
        var mean = values.Average();
        return values.Average(value => (value - mean) * (value - mean));
    }
}
