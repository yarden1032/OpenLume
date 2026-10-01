using System.Text.Json;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class LocalMaskTests
{
    [Fact]
    public async Task OptimizedMaskPipelineMatchesReferenceColorMathAndLayerOrder()
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using (var bitmap = new SKBitmap(4, 4))
            {
                bitmap.Erase(new SKColor(60, 80, 100));
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, data.ToArray(), TestContext.Current.CancellationToken);
            }
            LocalMask[] masks = [
                new(Guid.NewGuid(), CenterX: .375, CenterY: .375, ExposureEv: .7, Contrast: 37, Saturation: -40, Temperature: -25, Tint: 15),
                new(Guid.NewGuid(), Kind: LocalMaskKind.Linear, ExposureEv: -.3, Contrast: -10, Saturation: 20, Inverted: true, Density: .6)];
            double red = 60, green = 80, blue = 100;
            foreach (var mask in masks)
            {
                var weight = mask.Normalize().Weight(.375, .375);
                var exposure = Math.Pow(2, mask.ExposureEv);
                var r = red * exposure + mask.Temperature * .35;
                var g = green * exposure + mask.Tint * .25;
                var b = blue * exposure - mask.Temperature * .35;
                var contrast = 1 + mask.Contrast / 100;
                r = (r - 127.5) * contrast + 127.5;
                g = (g - 127.5) * contrast + 127.5;
                b = (b - 127.5) * contrast + 127.5;
                var luminance = r * .2126 + g * .7152 + b * .0722;
                var saturation = 1 + mask.Saturation / 100;
                red = Math.Clamp(red + (luminance + (r - luminance) * saturation - red) * weight, 0, 255);
                green = Math.Clamp(green + (luminance + (g - luminance) * saturation - green) * weight, 0, 255);
                blue = Math.Clamp(blue + (luminance + (b - luminance) * saturation - blue) * weight, 0, 255);
            }
            using var renderer = new SkiaImageRenderer();
            await renderer.ExportAsync(source, destination, new EditRecipe(LocalMasks: new LocalMaskCollection(masks)),
                new ExportOptions(ImageExportFormat.Png), TestContext.Current.CancellationToken);
            using var actual = SKBitmap.Decode(destination);
            var pixel = actual.GetPixel(1, 1);
            Assert.InRange(Math.Abs(pixel.Red - Math.Round(red)), 0, 1);
            Assert.InRange(Math.Abs(pixel.Green - Math.Round(green)), 0, 1);
            Assert.InRange(Math.Abs(pixel.Blue - Math.Round(blue)), 0, 1);
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public void MaskWeightsSupportFeatherInversionDensityAndLinearAxis()
    {
        var radial = new LocalMask(Guid.NewGuid(), Feather: .5).Normalize();
        Assert.Equal(1, radial.Weight(.5, .5));
        Assert.Equal(0, radial.Weight(.9, .5));
        Assert.Equal(.5, radial.Weight(.6875, .5), 6);
        Assert.Equal(.4, (radial with { Inverted = true, Density = .4 }).Weight(.9, .5), 6);
        Assert.Equal(0, (radial with { Enabled = false, Inverted = true }).Weight(.9, .5));
        var linear = radial with { Kind = LocalMaskKind.Linear, AngleDegrees = 90 };
        Assert.Equal(1, linear.Weight(.5, .1));
        Assert.Equal(0, linear.Weight(.5, .9));
        Assert.Equal(.5, linear.Weight(.5, .5), 6);
    }

    [Fact]
    public void RecipeMasksMigrateBoundAndRoundTripWithValueEquality()
    {
        var migrated = JsonSerializer.Deserialize<EditRecipe>("{\"Version\":9,\"ExposureEv\":1}")!.Normalize();
        Assert.Empty(migrated.LocalMasks!);
        Assert.Equal(1, migrated.ExposureEv);
        Assert.Equal(EditRecipe.CurrentVersion, migrated.Version);
        var mask = new LocalMask(Guid.NewGuid(), ExposureEv: double.NaN, RadiusX: -3, Density: 5, Kind: (LocalMaskKind)999);
        var recipe = new EditRecipe(LocalMasks: new LocalMaskCollection([mask, mask])).Normalize();
        Assert.Single(recipe.LocalMasks!);
        Assert.Equal(.01, recipe.LocalMasks![0].RadiusX);
        Assert.Equal(0, recipe.LocalMasks[0].ExposureEv);
        Assert.Equal(1, recipe.LocalMasks[0].Density);
        Assert.Equal(LocalMaskKind.Radial, recipe.LocalMasks[0].Kind);
        Assert.Equal(recipe, JsonSerializer.Deserialize<EditRecipe>(JsonSerializer.Serialize(recipe))!.Normalize());
        var suggestion = new DevelopSuggestion(Guid.NewGuid(), "Global only", .8, new EditRecipe(ExposureEv: 1), [], [],
            DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow, [nameof(EditRecipe.ExposureEv)]);
        Assert.Equal(recipe.LocalMasks, suggestion.MergeOnto(recipe).LocalMasks);
    }

    [Fact]
    public async Task RadialMaskAffectsOnlyItsRegionAndPreviewExportStayIdenticalThroughCrop()
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            using (var bitmap = new SKBitmap(100, 80))
            {
                bitmap.Erase(new SKColor(60, 60, 60));
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(source, data.ToArray(), cancellationToken: TestContext.Current.CancellationToken);
            }
            var original = await File.ReadAllBytesAsync(source, cancellationToken: TestContext.Current.CancellationToken);
            var mask = new LocalMask(Guid.NewGuid(), CenterX: .3, RadiusX: .15, RadiusY: .2, ExposureEv: 1);
            var recipe = new EditRecipe(LocalMasks: new LocalMaskCollection([mask]));
            using var renderer = new SkiaImageRenderer();
            var preview = await renderer.RenderPreviewAsync(source, recipe, 200, cancellationToken: TestContext.Current.CancellationToken);
            using var decoded = SKBitmap.Decode(preview.Data);
            Assert.InRange(decoded.GetPixel(30, 40).Red, 115, 125);
            Assert.InRange(decoded.GetPixel(80, 40).Red, 57, 63);
            recipe = recipe with { Crop = new CropGeometry(X: .2, Width: .5, QuarterTurns: 1) };
            var cropped = await renderer.RenderPreviewAsync(source, recipe, 200, cancellationToken: TestContext.Current.CancellationToken);
            await renderer.ExportJpegAsync(source, destination, recipe, 90, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(cropped.Data, await File.ReadAllBytesAsync(destination, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(source, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }
}
