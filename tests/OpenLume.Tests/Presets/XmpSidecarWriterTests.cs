using System.Xml.Linq;
using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Presets;

namespace OpenLume.Tests.Presets;

public sealed class XmpSidecarWriterTests
{
    [Fact]
    public void ExportsSupportedDevelopRecipeAsAdobeCameraRawXmp()
    {
        var recipe = new EditRecipe(
            ExposureEv: 1.25,
            Contrast: -20,
            Saturation: 12,
            Temperature: 10,
            Tint: -4,
            RotationDegrees: -2.5,
            Highlights: -35,
            Shadows: 28,
            Whites: 9,
            Blacks: -7,
            Vibrance: 18,
            Vignette: -12,
            Texture: 14,
            Clarity: 8,
            Dehaze: 6,
            Sharpening: 35,
            NoiseReduction: 22,
            Grain: 18,
            ColorMixer: HslColorMixer.Neutral with { Red = new(-12, 22, 8) },
            ToneCurve: new ParametricToneCurve(18, 7, -12, -25, 20, 48, 78),
            Crop: new CropGeometry(.1, .2, .75, .7, 1, true, false),
            Optics: new OpticsCorrections(-18, 40, 22, 41),
            ColorNoiseReduction: 16,
            SharpeningRadius: 1.2,
            SharpeningMasking: 17,
            LocalMasks: new LocalMaskCollection([new LocalMask(Guid.NewGuid(), ExposureEv: .5)]));

        var result = XmpSidecarWriter.CreateSidecar("portrait.nef", recipe);
        var imported = new XmpPresetImporter().Import(result.Xmp);

        Assert.True(imported.Success);
        Assert.Equal(recipe.ExposureEv, imported.Recipe.ExposureEv);
        Assert.Equal(recipe.Contrast, imported.Recipe.Contrast);
        Assert.Equal(recipe.Temperature, imported.Recipe.Temperature);
        Assert.Equal(recipe.Highlights, imported.Recipe.Highlights);
        Assert.Equal(recipe.NoiseReduction, imported.Recipe.NoiseReduction);
        Assert.Equal(recipe.ColorNoiseReduction, imported.Recipe.ColorNoiseReduction);
        Assert.Equal(recipe.SharpeningRadius, imported.Recipe.SharpeningRadius);
        Assert.Equal(recipe.ColorMixer!.Red, imported.Recipe.ColorMixer!.Red);
        Assert.Equal(recipe.ToneCurve, imported.Recipe.ToneCurve);
        Assert.Equal(recipe.RotationDegrees, imported.Recipe.RotationDegrees);
        Assert.Equal(recipe.Crop!.X, imported.Recipe.Crop!.X, 6);
        Assert.Equal(recipe.Crop.Width, imported.Recipe.Crop.Width, 6);
        Assert.Equal(OrientationMatrix(recipe.Crop.QuarterTurns, recipe.Crop.FlipHorizontal, recipe.Crop.FlipVertical),
            OrientationMatrix(imported.Recipe.Crop.QuarterTurns, imported.Recipe.Crop.FlipHorizontal, imported.Recipe.Crop.FlipVertical));
        Assert.Equal(recipe.Optics!.Distortion, imported.Recipe.Optics!.Distortion);
        Assert.Equal(100, imported.Recipe.Optics.ChromaticAberration);
        Assert.Equal(recipe.Optics.LensVignette, imported.Recipe.Optics.LensVignette);
        Assert.Empty(imported.UnsupportedParameters);
        Assert.Contains(result.Warnings, warning => warning.Contains("local masks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("on/off", StringComparison.OrdinalIgnoreCase));

        var xmp = XDocument.Parse(result.Xmp);
        XNamespace crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        Assert.NotNull(xmp.Descendants(rdf + "Description").Single().Attribute(rdf + "about"));
        Assert.Equal("portrait.nef", xmp.Descendants(crs + "RawFileName").Single().Value);
    }

    [Theory]
    [InlineData(0, false, false, 1)]
    [InlineData(0, true, false, 2)]
    [InlineData(0, false, true, 4)]
    [InlineData(0, true, true, 3)]
    [InlineData(1, false, false, 6)]
    [InlineData(1, true, false, 5)]
    [InlineData(1, false, true, 7)]
    [InlineData(1, true, true, 8)]
    [InlineData(2, false, false, 3)]
    [InlineData(2, true, false, 4)]
    [InlineData(2, false, true, 2)]
    [InlineData(2, true, true, 1)]
    [InlineData(3, false, false, 8)]
    [InlineData(3, true, false, 7)]
    [InlineData(3, false, true, 5)]
    [InlineData(3, true, true, 6)]
    public void ExportsAndReimportsAllExifOrientationCombinations(
        int quarterTurns,
        bool flipHorizontal,
        bool flipVertical,
        int expectedOrientation)
    {
        var recipe = new EditRecipe(Crop: new CropGeometry(
            .1, .2, .6, .5, quarterTurns, flipHorizontal, flipVertical));

        var result = XmpSidecarWriter.CreateSidecar("photo.jpg", recipe);
        var imported = new XmpPresetImporter().Import(result.Xmp);

        Assert.True(imported.Success);
        var crop = imported.Recipe.Crop!;
        Assert.Equal(.1, crop.X, 6);
        Assert.Equal(.2, crop.Y, 6);
        Assert.Equal(.6, crop.Width, 6);
        Assert.Equal(.5, crop.Height, 6);

        XNamespace crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
        Assert.Equal(expectedOrientation.ToString(System.Globalization.CultureInfo.InvariantCulture), XDocument.Parse(result.Xmp)
            .Descendants(crs + "Orientation").Single().Value);
    }

    private static string OrientationMatrix(int quarterTurns, bool flipHorizontal, bool flipVertical)
    {
        (int X, int Y) x = (1, 0);
        (int X, int Y) y = (0, 1);
        (int X, int Y) Rotate((int X, int Y) point) => quarterTurns switch
        {
            1 => (-point.Y, point.X),
            2 => (-point.X, -point.Y),
            3 => (point.Y, -point.X),
            _ => point
        };
        x = Rotate(x);
        y = Rotate(y);
        if (flipHorizontal) { x = (-x.X, x.Y); y = (-y.X, y.Y); }
        if (flipVertical) { x = (x.X, -x.Y); y = (y.X, -y.Y); }
        return $"{x.X},{x.Y};{y.X},{y.Y}";
    }


    [Fact]
    public async Task WritesSidecarAtomicallyPreservingExistingMetadataAndOriginalPhoto()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlume-xmp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            var sidecar = Path.Combine(root, "photo.xmp");
            var originalBytes = new byte[] { 1, 3, 5, 7 };
            await File.WriteAllBytesAsync(source, originalBytes, TestContext.Current.CancellationToken);
            const string existingXmp = """
                <x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                  xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <rdf:RDF><rdf:Description rdf:about="" dc:format="image/jpeg" crs:Exposure2012="-1" crs:AutoTone="True">
                    <crs:Exposure2012>-2</crs:Exposure2012>
                    <dc:creator><rdf:Seq><rdf:li>Photographer</rdf:li></rdf:Seq></dc:creator>
                  </rdf:Description></rdf:RDF>
                </x:xmpmeta>
                """;
            await File.WriteAllTextAsync(sidecar, existingXmp, System.Text.Encoding.Unicode, TestContext.Current.CancellationToken);

            var result = await XmpSidecarWriter.WriteSidecarAsync(
                source,
                sidecar,
                new EditRecipe(ExposureEv: 1.5),
                TestContext.Current.CancellationToken);

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", result.Xmp, StringComparison.OrdinalIgnoreCase);
            var written = XDocument.Load(sidecar);
            XNamespace crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            var description = written.Descendants(rdf + "Description").Single();
            Assert.Equal("image/jpeg", description.Attribute(dc + "format")!.Value);
            Assert.Equal("Photographer", written.Descendants(rdf + "li").Single().Value);
            Assert.Equal("True", description.Attribute(crs + "AutoTone")!.Value);
            Assert.Equal("1.5", written.Descendants(crs + "Exposure2012").Single().Value);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), file => file.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsMalformedExistingSidecarAndNeverOverwritesOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlume-xmp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var photo = Path.Combine(root, "photo.dng");
            var malformedSidecar = Path.Combine(root, "photo.xmp");
            await File.WriteAllBytesAsync(photo, [8, 6, 4, 2], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(malformedSidecar, "not xmp", TestContext.Current.CancellationToken);
            var malformedBefore = await File.ReadAllBytesAsync(malformedSidecar, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => XmpSidecarWriter.WriteSidecarAsync(
                photo, malformedSidecar, EditRecipe.Default, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => XmpSidecarWriter.WriteSidecarAsync(
                photo, photo, EditRecipe.Default, TestContext.Current.CancellationToken));

            Assert.Equal(malformedBefore, await File.ReadAllBytesAsync(malformedSidecar, TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 8, 6, 4, 2 }, await File.ReadAllBytesAsync(photo, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
