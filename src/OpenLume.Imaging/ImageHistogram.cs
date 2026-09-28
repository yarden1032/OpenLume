using SkiaSharp;

namespace OpenLume.Imaging;

public sealed record ImageHistogram(
    IReadOnlyList<int> Red,
    IReadOnlyList<int> Green,
    IReadOnlyList<int> Blue,
    IReadOnlyList<int> Luminance,
    int Peak);

public static class ImageHistogramCalculator
{
    public static ImageHistogram Calculate(byte[] encodedImage, int binCount = 64)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        if (binCount is < 16 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(binCount), "Histogram bins must be between 16 and 256.");
        }

        using var bitmap = SKBitmap.Decode(encodedImage)
            ?? throw new InvalidDataException("The rendered preview could not be decoded for its histogram.");
        var red = new int[binCount];
        var green = new int[binCount];
        var blue = new int[binCount];
        var luminance = new int[binCount];

        // Bound histogram work for very large previews without changing its visible shape.
        var stride = Math.Max(1, (int)Math.Sqrt((bitmap.Width * (long)bitmap.Height) / 200_000d));
        for (var y = 0; y < bitmap.Height; y += stride)
        {
            for (var x = 0; x < bitmap.Width; x += stride)
            {
                var pixel = bitmap.GetPixel(x, y);
                red[ToBin(pixel.Red, binCount)]++;
                green[ToBin(pixel.Green, binCount)]++;
                blue[ToBin(pixel.Blue, binCount)]++;
                var luma = (int)Math.Round((0.2126 * pixel.Red) + (0.7152 * pixel.Green) + (0.0722 * pixel.Blue));
                luminance[ToBin((byte)Math.Clamp(luma, 0, 255), binCount)]++;
            }
        }

        var peak = red.Concat(green).Concat(blue).Concat(luminance).DefaultIfEmpty(1).Max();
        return new ImageHistogram(red, green, blue, luminance, Math.Max(1, peak));
    }

    private static int ToBin(byte value, int binCount) => Math.Min(binCount - 1, value * binCount / 256);
}
