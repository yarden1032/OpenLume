using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using Sdcb.LibRaw;
using SkiaSharp;

namespace OpenLume.Imaging;

/// <summary>Skia-backed raster renderer with LibRaw decoding for camera originals.</summary>
public sealed class SkiaImageRenderer : IImageRenderer
{
    public async Task<RenderedImage> RenderPreviewAsync(string sourcePath, EditRecipe edit, int maxDimension, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = await LoadAndProcessAsync(sourcePath, edit, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var preview = Resize(bitmap, maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(preview);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedImage(data.ToArray(), "image/jpeg", preview.Width, preview.Height);
    }

    public async Task ExportJpegAsync(string sourcePath, string destinationPath, EditRecipe edit, int quality, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination is required.", nameof(destinationPath));
        quality = Math.Clamp(quality, 0, 100);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = await LoadAndProcessAsync(sourcePath, edit, cancellationToken).ConfigureAwait(false);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await encoded.AsStream().CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destinationPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Task<SKBitmap> LoadAndProcessAsync(string path, EditRecipe recipe, CancellationToken token) =>
        Task.Run(() => LoadAndProcess(path, recipe, token), token);

    private static SKBitmap LoadAndProcess(string path, EditRecipe recipe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SKBitmap decoded;
        if (SupportedPhotoFormats.RawExtensions.Contains(Path.GetExtension(path)))
        {
            decoded = LoadRaw(path, token);
        }
        else
        {
            decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException("Unable to decode raster image.");
        }

        using var decodedLifetime = decoded;
        var result = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var e = recipe.Normalize();
        var exposure = Math.Pow(2, e.ExposureEv);
        for (var y = 0; y < decoded.Height; y++)
            for (var x = 0; x < decoded.Width; x++)
            {
                if ((x & 127) == 0) token.ThrowIfCancellationRequested();
                var c = decoded.GetPixel(x, y);
                var r = c.Red * exposure; var g = c.Green * exposure; var b = c.Blue * exposure;
                var l = (r * .2126 + g * .7152 + b * .0722) / 255.0;
                var contrast = (e.Contrast + 100) / 100.0;
                r = (r - 127.5) * contrast + 127.5; g = (g - 127.5) * contrast + 127.5; b = (b - 127.5) * contrast + 127.5;
                var sat = (e.Saturation + 100) / 100.0;
                r = l * 255 + (r - l * 255) * sat; g = l * 255 + (g - l * 255) * sat; b = l * 255 + (b - l * 255) * sat;
                var warmth = e.Temperature / 100.0 * 30; var green = e.Tint / 100.0 * 20;
                result.SetPixel(x, y, new SKColor(ToByte(r + warmth), ToByte(g + green), ToByte(b - warmth), c.Alpha));
            }
        if (Math.Abs(e.RotationDegrees) > .001) { var rotated = Rotate(result, (float)e.RotationDegrees); result.Dispose(); return rotated; }
        return result;
    }

    private static SKBitmap LoadRaw(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var context = RawContext.OpenFile(path);
        context.Unpack();
        token.ThrowIfCancellationRequested();
        context.DcrawProcess(parameters =>
        {
            parameters.HalfSize = false;
            parameters.UseCameraWb = true;
            parameters.OutputBps = 8;
            parameters.OutputTiff = false;
        });
        using var processed = context.MakeDcrawMemoryImage();
        if (processed.Bits != 8 || processed.Channels < 3)
        {
            throw new InvalidDataException($"Unsupported LibRaw output: {processed.Bits}-bit, {processed.Channels} channels.");
        }

        var source = processed.AsSpan<byte>();
        var bitmap = new SKBitmap(processed.Width, processed.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var stride = processed.Channels;
        for (var y = 0; y < processed.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < processed.Width; x++)
            {
                var offset = ((y * processed.Width) + x) * stride;
                bitmap.SetPixel(x, y, new SKColor(source[offset], source[offset + 1], source[offset + 2]));
            }
        }

        return bitmap;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    private static SKBitmap Resize(SKBitmap source, int max) { var scale = Math.Min(1d, Math.Min((double)max / source.Width, (double)max / source.Height)); if (scale >= 1) return source.Copy(); var b = new SKBitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))); using var c = new SKCanvas(b); c.DrawBitmap(source, new SKRect(0, 0, b.Width, b.Height)); return b; }
    private static SKBitmap Rotate(SKBitmap source, float degrees) { var r = new SKRect(0, 0, source.Width, source.Height); var m = SKMatrix.CreateRotationDegrees(degrees, source.Width / 2f, source.Height / 2f); r = m.MapRect(r); var b = new SKBitmap(Math.Max(1, (int)Math.Ceiling(r.Width)), Math.Max(1, (int)Math.Ceiling(r.Height))); using var c = new SKCanvas(b); c.Translate(-r.Left, -r.Top); c.RotateDegrees(degrees, source.Width / 2f, source.Height / 2f); c.DrawBitmap(source, 0, 0); return b; }
}
