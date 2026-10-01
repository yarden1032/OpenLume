using System.Text.Json;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class LocalMaskTests
{
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
