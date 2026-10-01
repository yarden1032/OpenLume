using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

[Collection("Renderer budget")]
public sealed class RawCameraFixtureTests
{
    public static TheoryData<string> CameraRawFiles => new()
    {
        "canon-eos-r6.cr3",
        "nikon-d2h.nef",
        "sony-ilce-7s.arw"
    };

    [Theory]
    [MemberData(nameof(CameraRawFiles))]
    public async Task RepresentativeCameraRawDecodesToAFullColorLandscapePreview(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Raw", fileName);
        Assert.True(File.Exists(path), $"Missing licensed RAW fixture: {path}");

        var metadata = await MetadataExtractor.ReadAsync(path, token: TestContext.Current.CancellationToken);
        Assert.True(metadata.PixelWidth >= 2000, $"Unexpected source width for {fileName}: {metadata.PixelWidth}");
        Assert.True(metadata.PixelHeight >= 1300, $"Unexpected source height for {fileName}: {metadata.PixelHeight}");

        using var renderer = new SkiaImageRenderer();
        var preview = await renderer.RenderPreviewAsync(path, EditRecipe.Default, 360, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(360, preview.Width);
        Assert.InRange((double)preview.Width / preview.Height, 1.45, 1.55);
        Assert.Equal("image/jpeg", preview.MimeType);
        using var bitmap = SKBitmap.Decode(preview.Data);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Pixels.Select(pixel => pixel.Red).Distinct().Count() > 20,
            $"The decoded {fileName} preview has too little tonal variation.");
        Assert.True(bitmap.Pixels.Select(pixel => pixel.Green).Distinct().Count() > 20,
            $"The decoded {fileName} preview has too little color variation.");
    }
}
