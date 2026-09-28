using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using Sdcb.LibRaw;
using SkiaSharp;

namespace OpenLume.Imaging;

/// <summary>Skia-backed raster renderer with LibRaw decoding for camera originals.</summary>
public sealed class SkiaImageRenderer : IImageRenderer, IDisposable
{
    private const int PreviewCacheCapacity = 3;
    private const int MinimumCachedDimension = 1_000;
    private readonly object _previewCacheLock = new();
    private readonly List<CachedPreview> _previewCache = [];
    private bool _disposed;

    private sealed record PreviewCacheKey(
        string Path,
        long Length,
        long LastWriteUtcTicks,
        int MaxDimension);

    private sealed record CachedPreview(PreviewCacheKey Key, SKBitmap Bitmap);

    public async Task<RenderedImage> RenderPreviewAsync(
        string sourcePath,
        EditRecipe edit,
        int maxDimension,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        using var source = await LoadPreviewSourceAsync(sourcePath, maxDimension, cancellationToken)
            .ConfigureAwait(false);
        using var preview = await Task.Run(
            () => ApplyRecipe(source, edit, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(preview);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedImage(data.ToArray(), "image/jpeg", preview.Width, preview.Height);
    }

    public async Task ExportJpegAsync(
        string sourcePath,
        string destinationPath,
        EditRecipe edit,
        int quality,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination is required.", nameof(destinationPath));
        }

        quality = Math.Clamp(quality, 0, 100);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = await Task.Run(() =>
        {
            using var source = LoadSource(sourcePath, halfSizeRaw: false, cancellationToken);
            return ApplyRecipe(source, edit, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous))
            {
                await encoded.AsStream().CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_previewCacheLock)
        {
            foreach (var entry in _previewCache)
            {
                entry.Bitmap.Dispose();
            }

            _previewCache.Clear();
            _disposed = true;
        }
    }

    private Task<SKBitmap> LoadPreviewSourceAsync(
        string sourcePath,
        int maxDimension,
        CancellationToken cancellationToken) =>
        Task.Run(() => LoadPreviewSource(sourcePath, maxDimension, cancellationToken), cancellationToken);

    private SKBitmap LoadPreviewSource(
        string sourcePath,
        int maxDimension,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Source image was not found.", fullPath);
        }

        var key = new PreviewCacheKey(fullPath, file.Length, file.LastWriteTimeUtc.Ticks, maxDimension);
        if (maxDimension >= MinimumCachedDimension)
        {
            lock (_previewCacheLock)
            {
                var cached = _previewCache.FirstOrDefault(entry => entry.Key == key);
                if (cached is not null)
                {
                    _previewCache.Remove(cached);
                    _previewCache.Add(cached);
                    return cached.Bitmap.Copy();
                }
            }
        }

        using var decoded = LoadSource(fullPath, halfSizeRaw: true, cancellationToken);
        var resized = Resize(decoded, maxDimension);
        if (maxDimension >= MinimumCachedDimension)
        {
            lock (_previewCacheLock)
            {
                _previewCache.Add(new CachedPreview(key, resized.Copy()));
                while (_previewCache.Count > PreviewCacheCapacity)
                {
                    _previewCache[0].Bitmap.Dispose();
                    _previewCache.RemoveAt(0);
                }
            }
        }

        return resized;
    }

    private static SKBitmap LoadSource(string path, bool halfSizeRaw, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SupportedPhotoFormats.RawExtensions.Contains(Path.GetExtension(path))
            ? LoadRaw(path, halfSizeRaw, cancellationToken)
            : SKBitmap.Decode(path) ?? throw new InvalidDataException("Unable to decode raster image.");
    }

    private static SKBitmap ApplyRecipe(SKBitmap source, EditRecipe recipe, CancellationToken cancellationToken)
    {
        var edit = recipe.Normalize();
        var sourcePixels = source.Pixels;
        var outputPixels = new SKColor[sourcePixels.Length];
        var exposure = Math.Pow(2, edit.ExposureEv);
        var contrast = (edit.Contrast + 100) / 100.0;
        var saturation = (edit.Saturation + 100) / 100.0;
        var warmth = edit.Temperature / 100.0 * 30;
        var green = edit.Tint / 100.0 * 20;
        for (var index = 0; index < sourcePixels.Length; index++)
        {
            if ((index & 65_535) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var color = sourcePixels[index];
            var red = color.Red * exposure;
            var greenChannel = color.Green * exposure;
            var blue = color.Blue * exposure;
            var luminance = (red * .2126 + greenChannel * .7152 + blue * .0722) / 255.0;
            red = ((red - 127.5) * contrast) + 127.5;
            greenChannel = ((greenChannel - 127.5) * contrast) + 127.5;
            blue = ((blue - 127.5) * contrast) + 127.5;
            red = (luminance * 255) + ((red - (luminance * 255)) * saturation);
            greenChannel = (luminance * 255) + ((greenChannel - (luminance * 255)) * saturation);
            blue = (luminance * 255) + ((blue - (luminance * 255)) * saturation);
            outputPixels[index] = new SKColor(
                ToByte(red + warmth),
                ToByte(greenChannel + green),
                ToByte(blue - warmth),
                color.Alpha);
        }

        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
        {
            Pixels = outputPixels
        };
        if (Math.Abs(edit.RotationDegrees) <= .001)
        {
            return result;
        }

        var rotated = Rotate(result, (float)edit.RotationDegrees);
        result.Dispose();
        return rotated;
    }

    private static SKBitmap LoadRaw(string path, bool halfSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var context = RawContext.OpenFile(path);
        context.Unpack();
        cancellationToken.ThrowIfCancellationRequested();
        context.DcrawProcess(parameters =>
        {
            parameters.HalfSize = halfSize;
            parameters.UseCameraWb = true;
            parameters.OutputBps = 8;
            parameters.OutputTiff = false;
        });
        using var processed = context.MakeDcrawMemoryImage();
        if (processed.Bits != 8 || processed.Channels < 3)
        {
            throw new InvalidDataException(
                $"Unsupported LibRaw output: {processed.Bits}-bit, {processed.Channels} channels.");
        }

        var source = processed.AsSpan<byte>();
        var pixels = new SKColor[processed.Width * processed.Height];
        var stride = processed.Channels;
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 65_535) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offset = index * stride;
            pixels[index] = new SKColor(source[offset], source[offset + 1], source[offset + 2]);
        }

        return new SKBitmap(processed.Width, processed.Height, SKColorType.Rgba8888, SKAlphaType.Opaque)
        {
            Pixels = pixels
        };
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static SKBitmap Resize(SKBitmap source, int maxDimension)
    {
        var scale = Math.Min(
            1d,
            Math.Min((double)maxDimension / source.Width, (double)maxDimension / source.Height));
        if (scale >= 1)
        {
            return source.Copy();
        }

        var bitmap = new SKBitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawBitmap(source, new SKRect(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static SKBitmap Rotate(SKBitmap source, float degrees)
    {
        var bounds = new SKRect(0, 0, source.Width, source.Height);
        var matrix = SKMatrix.CreateRotationDegrees(degrees, source.Width / 2f, source.Height / 2f);
        bounds = matrix.MapRect(bounds);
        var bitmap = new SKBitmap(
            Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height)));
        using var canvas = new SKCanvas(bitmap);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.RotateDegrees(degrees, source.Width / 2f, source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return bitmap;
    }
}
