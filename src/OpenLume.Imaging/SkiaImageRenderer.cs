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
        var vibrance = edit.Vibrance / 100.0;
        var dehaze = edit.Dehaze / 100.0;
        var toneCurve = edit.ToneCurve!;
        var toneCurveTable = toneCurve.IsIdentity ? null : toneCurve.CreateLookupTable();
        var mixerBands = CreateMixerBands(edit.ColorMixer!);
        var hasMixerEdits = mixerBands.Any(band =>
            Math.Abs(band.Adjustment.Hue) > .001 ||
            Math.Abs(band.Adjustment.Saturation) > .001 ||
            Math.Abs(band.Adjustment.Luminance) > .001);
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
            red += warmth;
            greenChannel += green;
            blue -= warmth;
            var luminance = Math.Clamp(
                (red * .2126 + greenChannel * .7152 + blue * .0722) / 255.0,
                0,
                1);
            var shadowWeight = Math.Pow(1 - luminance, 2);
            var highlightWeight = Math.Pow(luminance, 2);
            var blackWeight = Math.Pow(1 - luminance, 4);
            var whiteWeight = Math.Pow(luminance, 4);
            var toneDelta = 255 * (
                (edit.Shadows / 100.0 * .34 * shadowWeight) +
                (edit.Highlights / 100.0 * .34 * highlightWeight) +
                (edit.Blacks / 100.0 * .24 * blackWeight) +
                (edit.Whites / 100.0 * .24 * whiteWeight));
            red += toneDelta;
            greenChannel += toneDelta;
            blue += toneDelta;
            red = ((red - 127.5) * contrast) + 127.5;
            greenChannel = ((greenChannel - 127.5) * contrast) + 127.5;
            blue = ((blue - 127.5) * contrast) + 127.5;
            var dehazeContrast = 1 + (dehaze * .55);
            var dehazeOffset = dehaze * 10;
            red = ((red - 127.5) * dehazeContrast) + 127.5 - dehazeOffset;
            greenChannel = ((greenChannel - 127.5) * dehazeContrast) + 127.5 - dehazeOffset;
            blue = ((blue - 127.5) * dehazeContrast) + 127.5 - dehazeOffset;
            if (toneCurveTable is not null)
            {
                var curveInput = Math.Clamp(
                    ((red * .2126) + (greenChannel * .7152) + (blue * .0722)) / 255.0,
                    0,
                    1);
                var toneDeltaFromCurve = (SampleLookupTable(toneCurveTable, curveInput) - curveInput) * 255;
                red += toneDeltaFromCurve;
                greenChannel += toneDeltaFromCurve;
                blue += toneDeltaFromCurve;
            }
            var postToneLuminance = (red * .2126) + (greenChannel * .7152) + (blue * .0722);
            var channelMaximum = Math.Max(red, Math.Max(greenChannel, blue));
            var channelMinimum = Math.Min(red, Math.Min(greenChannel, blue));
            var chroma = Math.Clamp((channelMaximum - channelMinimum) / 255.0, 0, 1);
            var adaptiveVibrance = 1 + (vibrance * (1 - chroma) * .8);
            var colorScale = Math.Max(0, saturation * adaptiveVibrance);
            red = postToneLuminance + ((red - postToneLuminance) * colorScale);
            greenChannel = postToneLuminance + ((greenChannel - postToneLuminance) * colorScale);
            blue = postToneLuminance + ((blue - postToneLuminance) * colorScale);
            if (hasMixerEdits)
            {
                ApplyColorMixer(ref red, ref greenChannel, ref blue, mixerBands);
            }
            var x = (index % source.Width) / (double)Math.Max(1, source.Width - 1);
            var y = (index / source.Width) / (double)Math.Max(1, source.Height - 1);
            var radius = Math.Sqrt(Math.Pow((x - .5) * 2, 2) + Math.Pow((y - .5) * 2, 2)) / Math.Sqrt(2);
            var edge = Math.Pow(Math.Clamp((radius - .2) / .8, 0, 1), 2);
            var vignetteFactor = Math.Max(.1, 1 + (edit.Vignette / 100.0 * edge * .75));
            outputPixels[index] = new SKColor(
                ToByte(red * vignetteFactor),
                ToByte(greenChannel * vignetteFactor),
                ToByte(blue * vignetteFactor),
                color.Alpha);
        }

        var toneResult = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
        {
            Pixels = outputPixels
        };
        SKBitmap result;
        try
        {
            result = ApplyPresenceAndDetail(toneResult, edit, cancellationToken);
        }
        catch
        {
            toneResult.Dispose();
            throw;
        }
        if (!ReferenceEquals(result, toneResult))
        {
            toneResult.Dispose();
        }

        var crop = edit.Crop!;
        if (Math.Abs(edit.RotationDegrees) > .001)
        {
            var rotated = Rotate(result, (float)edit.RotationDegrees);
            result.Dispose();
            result = rotated;
        }

        if (crop.HasOrientation)
        {
            var oriented = ApplyOrientation(result, crop, cancellationToken);
            result.Dispose();
            result = oriented;
        }

        if (crop.HasCrop)
        {
            var cropped = ApplyCrop(result, crop);
            result.Dispose();
            result = cropped;
        }

        return result;
    }

    private static MixerBand[] CreateMixerBands(HslColorMixer mixer)
    {
        var normalized = mixer.Normalize();
        return
        [
            new(0, normalized.Red!),
            new(30, normalized.Orange!),
            new(60, normalized.Yellow!),
            new(120, normalized.Green!),
            new(180, normalized.Aqua!),
            new(240, normalized.Blue!),
            new(275, normalized.Purple!),
            new(320, normalized.Magenta!)
        ];
    }

    private static double SampleLookupTable(double[] table, double input)
    {
        var position = Math.Clamp(input, 0, 1) * (table.Length - 1);
        var lower = (int)position;
        var upper = Math.Min(table.Length - 1, lower + 1);
        var fraction = position - lower;
        return table[lower] + ((table[upper] - table[lower]) * fraction);
    }

    private static void ApplyColorMixer(
        ref double red,
        ref double green,
        ref double blue,
        IReadOnlyList<MixerBand> bands)
    {
        RgbToHsl(
            Math.Clamp(red / 255.0, 0, 1),
            Math.Clamp(green / 255.0, 0, 1),
            Math.Clamp(blue / 255.0, 0, 1),
            out var hue,
            out var saturation,
            out var luminance);

        var hueShift = 0.0;
        var saturationShift = 0.0;
        var luminanceShift = 0.0;
        foreach (var band in bands)
        {
            var distance = Math.Abs(hue - band.Center);
            distance = Math.Min(distance, 360 - distance);
            if (distance >= 45)
            {
                continue;
            }

            var weight = .5 * (1 + Math.Cos(Math.PI * distance / 45));
            hueShift += band.Adjustment.Hue / 100.0 * 30 * weight;
            saturationShift += band.Adjustment.Saturation / 100.0 * weight;
            luminanceShift += band.Adjustment.Luminance / 100.0 * weight;
        }

        hue = (hue + hueShift + 360) % 360;
        saturation = AdjustUnitChannel(saturation, saturationShift);
        luminance = AdjustUnitChannel(luminance, luminanceShift * .75);
        HslToRgb(hue, saturation, luminance, out var mixedRed, out var mixedGreen, out var mixedBlue);
        red = mixedRed * 255;
        green = mixedGreen * 255;
        blue = mixedBlue * 255;
    }

    private static double AdjustUnitChannel(double value, double adjustment) => adjustment >= 0
        ? value + ((1 - value) * Math.Min(1, adjustment))
        : value * (1 + Math.Max(-1, adjustment));

    private static void RgbToHsl(double red, double green, double blue, out double hue, out double saturation, out double luminance)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        luminance = (maximum + minimum) / 2;
        if (delta <= 1e-9)
        {
            hue = 0;
            saturation = 0;
            return;
        }

        saturation = delta / (1 - Math.Abs((2 * luminance) - 1));
        if (maximum == red)
        {
            hue = 60 * (((green - blue) / delta) % 6);
        }
        else if (maximum == green)
        {
            hue = 60 * (((blue - red) / delta) + 2);
        }
        else
        {
            hue = 60 * (((red - green) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }
    }

    private static void HslToRgb(double hue, double saturation, double luminance, out double red, out double green, out double blue)
    {
        var chroma = (1 - Math.Abs((2 * luminance) - 1)) * saturation;
        var sector = hue / 60;
        var intermediate = chroma * (1 - Math.Abs((sector % 2) - 1));
        (red, green, blue) = sector switch
        {
            < 1 => (chroma, intermediate, 0.0),
            < 2 => (intermediate, chroma, 0.0),
            < 3 => (0.0, chroma, intermediate),
            < 4 => (0.0, intermediate, chroma),
            < 5 => (intermediate, 0.0, chroma),
            _ => (chroma, 0.0, intermediate)
        };
        var match = luminance - (chroma / 2);
        red += match;
        green += match;
        blue += match;
    }

    private readonly record struct MixerBand(double Center, HslChannelAdjustment Adjustment);

    private static SKBitmap ApplyPresenceAndDetail(
        SKBitmap source,
        EditRecipe edit,
        CancellationToken cancellationToken)
    {
        var working = source;
        var ownsWorking = false;
        try
        {
            if (edit.NoiseReduction > .001)
            {
                var denoised = MixWithBlur(
                    working,
                    sigma: .35f + (float)(edit.NoiseReduction / 100 * 2.65),
                    mix: edit.NoiseReduction / 100 * .82,
                    cancellationToken);
                if (ownsWorking) working.Dispose();
                working = denoised;
                ownsWorking = true;
            }

            if (Math.Abs(edit.Texture) > .001)
            {
                var textured = ApplyUnsharp(
                    working,
                    sigma: 1.15f,
                    amount: edit.Texture / 100 * .62,
                    cancellationToken);
                if (ownsWorking) working.Dispose();
                working = textured;
                ownsWorking = true;
            }

            if (Math.Abs(edit.Clarity) > .001)
            {
                var clarified = ApplyUnsharp(
                    working,
                    sigma: 5.5f,
                    amount: edit.Clarity / 100 * .52,
                    cancellationToken);
                if (ownsWorking) working.Dispose();
                working = clarified;
                ownsWorking = true;
            }

            if (edit.Sharpening > .001)
            {
                var sharpened = ApplyUnsharp(
                    working,
                    sigma: .8f,
                    amount: edit.Sharpening / 100 * 1.15,
                    cancellationToken);
                if (ownsWorking) working.Dispose();
                working = sharpened;
                ownsWorking = true;
            }

            if (edit.Grain > .001)
            {
                var grained = ApplyGrain(working, edit.Grain / 100, cancellationToken);
                if (ownsWorking) working.Dispose();
                working = grained;
                ownsWorking = true;
            }

            return ownsWorking ? working : source;
        }
        catch
        {
            if (ownsWorking)
            {
                working.Dispose();
            }

            throw;
        }
    }

    private static SKBitmap MixWithBlur(
        SKBitmap source,
        float sigma,
        double mix,
        CancellationToken cancellationToken)
    {
        using var blurred = Blur(source, sigma);
        var sourcePixels = source.Pixels;
        var blurredPixels = blurred.Pixels;
        var output = new SKColor[sourcePixels.Length];
        for (var index = 0; index < output.Length; index++)
        {
            if ((index & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var original = sourcePixels[index];
            var soft = blurredPixels[index];
            output[index] = new SKColor(
                ToByte(original.Red + ((soft.Red - original.Red) * mix)),
                ToByte(original.Green + ((soft.Green - original.Green) * mix)),
                ToByte(original.Blue + ((soft.Blue - original.Blue) * mix)),
                original.Alpha);
        }

        return CreateBitmap(source.Width, source.Height, output);
    }

    private static SKBitmap ApplyUnsharp(
        SKBitmap source,
        float sigma,
        double amount,
        CancellationToken cancellationToken)
    {
        using var blurred = Blur(source, sigma);
        var sourcePixels = source.Pixels;
        var blurredPixels = blurred.Pixels;
        var output = new SKColor[sourcePixels.Length];
        for (var index = 0; index < output.Length; index++)
        {
            if ((index & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var original = sourcePixels[index];
            var soft = blurredPixels[index];
            output[index] = new SKColor(
                ToByte(original.Red + ((original.Red - soft.Red) * amount)),
                ToByte(original.Green + ((original.Green - soft.Green) * amount)),
                ToByte(original.Blue + ((original.Blue - soft.Blue) * amount)),
                original.Alpha);
        }

        return CreateBitmap(source.Width, source.Height, output);
    }

    private static SKBitmap ApplyGrain(
        SKBitmap source,
        double amount,
        CancellationToken cancellationToken)
    {
        var sourcePixels = source.Pixels;
        var output = new SKColor[sourcePixels.Length];
        var amplitude = amount * 22;
        for (var index = 0; index < output.Length; index++)
        {
            if ((index & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var x = index % source.Width;
            var y = index / source.Width;
            var hash = unchecked((uint)((x * 374761393) ^ (y * 668265263) ^ (x * y * 69069)));
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            var noise = ((hash & 1023) / 511.5 - 1) * amplitude;
            var original = sourcePixels[index];
            output[index] = new SKColor(
                ToByte(original.Red + noise),
                ToByte(original.Green + noise),
                ToByte(original.Blue + noise),
                original.Alpha);
        }

        return CreateBitmap(source.Width, source.Height, output);
    }

    private static SKBitmap Blur(SKBitmap source, float sigma)
    {
        var output = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
            IsAntialias = true
        };
        canvas.DrawBitmap(source, 0, 0, paint);
        return output;
    }

    private static SKBitmap CreateBitmap(int width, int height, SKColor[] pixels) => new(
        width,
        height,
        SKColorType.Rgba8888,
        SKAlphaType.Premul)
    {
        Pixels = pixels
    };

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

    private static SKBitmap ApplyOrientation(
        SKBitmap source,
        CropGeometry geometry,
        CancellationToken cancellationToken)
    {
        var quarterTurns = geometry.QuarterTurns;
        var destinationWidth = quarterTurns is 1 or 3 ? source.Height : source.Width;
        var destinationHeight = quarterTurns is 1 or 3 ? source.Width : source.Height;
        var sourcePixels = source.Pixels;
        var destinationPixels = new SKColor[destinationWidth * destinationHeight];
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < source.Width; x++)
            {
                int destinationX;
                int destinationY;
                switch (quarterTurns)
                {
                    case 1:
                        destinationX = source.Height - 1 - y;
                        destinationY = x;
                        break;
                    case 2:
                        destinationX = source.Width - 1 - x;
                        destinationY = source.Height - 1 - y;
                        break;
                    case 3:
                        destinationX = y;
                        destinationY = source.Width - 1 - x;
                        break;
                    default:
                        destinationX = x;
                        destinationY = y;
                        break;
                }

                if (geometry.FlipHorizontal)
                {
                    destinationX = destinationWidth - 1 - destinationX;
                }

                if (geometry.FlipVertical)
                {
                    destinationY = destinationHeight - 1 - destinationY;
                }

                destinationPixels[(destinationY * destinationWidth) + destinationX] =
                    sourcePixels[(y * source.Width) + x];
            }
        }

        return CreateBitmap(destinationWidth, destinationHeight, destinationPixels);
    }

    private static SKBitmap ApplyCrop(SKBitmap source, CropGeometry geometry)
    {
        var width = Math.Clamp((int)Math.Round(source.Width * geometry.Width), 1, source.Width);
        var height = Math.Clamp((int)Math.Round(source.Height * geometry.Height), 1, source.Height);
        var left = Math.Clamp((int)Math.Round(source.Width * geometry.X), 0, source.Width - width);
        var top = Math.Clamp((int)Math.Round(source.Height * geometry.Y), 0, source.Height - height);
        var output = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        if (!source.ExtractSubset(output, new SKRectI(left, top, left + width, top + height)))
        {
            output.Dispose();
            throw new InvalidDataException("The normalized crop could not be extracted from the rendered image.");
        }

        return output;
    }
}
