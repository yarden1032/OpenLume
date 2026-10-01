using System.Text.Json;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using OpenLume.Infrastructure.Presets;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class ColorNoiseReductionTests
{
    [Fact]
    public void LegacyRecipesStayNeutralAndUntrustedValuesAreBounded()
    {
        var recipe = JsonSerializer.Deserialize<EditRecipe>("{\"Version\":7,\"ExposureEv\":1}")!.Normalize();
        Assert.Equal(0, recipe.ColorNoiseReduction);
        Assert.Equal(1, recipe.ExposureEv);
        Assert.Equal(0, new EditRecipe(ColorNoiseReduction: double.NaN).Normalize().ColorNoiseReduction);
        Assert.Equal(100, new EditRecipe(ColorNoiseReduction: 500).Normalize().ColorNoiseReduction);
        var imported = new XmpPresetImporter().Import("<x xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/' crs:ColorNoiseReduction='42'/>");
        Assert.Equal(42, imported.Recipe.ColorNoiseReduction);
    }

    [Fact]
    public async Task ReducesChromaVariationPreservesLuminanceAndMatchesExport()
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
                        var luminance = x < 64 ? 70 : 180;
                        var noise = ((x / 2 + y / 2) % 2 == 0 ? 1 : -1) * 25;
                        bitmap.SetPixel(x, y, new SKColor((byte)(luminance + noise), (byte)(luminance - noise * .2973), (byte)luminance));
                    }
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, encoded.ToArray());
            }

            using var renderer = new SkiaImageRenderer();
            var original = await renderer.RenderPreviewAsync(source, EditRecipe.Default, 256);
            var recipe = new EditRecipe(ColorNoiseReduction: 100);
            var edited = await renderer.RenderPreviewAsync(source, recipe, 256);
            using var before = SKBitmap.Decode(original.Data);
            using var after = SKBitmap.Decode(edited.Data);
            var beforeChroma = before.Pixels.Average(pixel => Math.Abs(pixel.Red - pixel.Green));
            var afterChroma = after.Pixels.Average(pixel => Math.Abs(pixel.Red - pixel.Green));
            Assert.True(afterChroma < beforeChroma * .5);
            Assert.InRange(Math.Abs(Luminance(before.GetPixel(30, 30)) - Luminance(after.GetPixel(30, 30))), 0, 3);
            Assert.True(Luminance(after.GetPixel(90, 30)) - Luminance(after.GetPixel(30, 30)) > 100);
            await renderer.ExportJpegAsync(source, export, recipe, 90);
            Assert.Equal(edited.Data, await File.ReadAllBytesAsync(export));
        }
        finally
        {
            File.Delete(source);
            File.Delete(export);
        }
    }

    private static double Luminance(SKColor pixel) => pixel.Red * .2126 + pixel.Green * .7152 + pixel.Blue * .0722;
}
