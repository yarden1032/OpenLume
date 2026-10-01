using BitMiracle.LibTiff.Classic;
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
    private static readonly Lazy<byte[]> SrgbIccProfile = new(LoadSrgbIccProfile);
    private readonly object _previewCacheLock = new();
    private readonly List<CachedPreview> _previewCache = [];
    private bool _disposed;

    private sealed record PreviewCacheKey(
        string Path,
        long Length,
        long LastWriteUtcTicks,
        int MaxDimension);

    private sealed record CachedPreview(PreviewCacheKey Key, SKBitmap Bitmap);
    private readonly record struct SampledPixel(double Red, double Green, double Blue, double Alpha);

    public Task ExportJpegAsync(
        string sourcePath,
        string destinationPath,
        EditRecipe edit,
        int quality,
        CancellationToken cancellationToken = default) =>
        ExportAsync(sourcePath, destinationPath, edit, ImageExportFormat.Jpeg, quality, cancellationToken);

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

    public Task ExportAsync(
        string sourcePath,
        string destinationPath,
        EditRecipe edit,
        ImageExportFormat format,
        int quality = 92,
        CancellationToken cancellationToken = default) =>
        ExportAsync(sourcePath, destinationPath, edit, new ExportOptions(format, quality), cancellationToken);

    public async Task ExportAsync(
        string sourcePath,
        string destinationPath,
        EditRecipe edit,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination is required.", nameof(destinationPath));
        }

        options = options.Normalize();
        cancellationToken.ThrowIfCancellationRequested();
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(sourcePath), fullDestinationPath, pathComparison))
        {
            throw new IOException("Export cannot replace the original photo. Choose a new filename.");
        }

        if (File.Exists(fullDestinationPath) || Directory.Exists(fullDestinationPath))
        {
            throw new IOException("Export cannot replace an existing file. Choose a new filename.");
        }

        var destinationExtension = Path.GetExtension(fullDestinationPath);
        if (!string.Equals(destinationExtension, "." + options.Extension, StringComparison.OrdinalIgnoreCase) &&
            !(options.Format == ImageExportFormat.Jpeg && string.Equals(destinationExtension, ".jpeg", StringComparison.OrdinalIgnoreCase)) &&
            !(options.Format == ImageExportFormat.Tiff && string.Equals(destinationExtension, ".tiff", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"The destination extension must match {options.Format}.", nameof(destinationPath));

        using var bitmap = await Task.Run(() =>
        {
            using var source = LoadSource(sourcePath, halfSizeRaw: false, cancellationToken);
            var developed = ApplyRecipe(source, edit, cancellationToken);
            if (options.MaxDimension == 0) return developed;
            using (developed) return Resize(developed, options.MaxDimension, smooth: true);
        }, cancellationToken).ConfigureAwait(false);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = options.Format switch
        {
            ImageExportFormat.Jpeg => image.Encode(SKEncodedImageFormat.Jpeg, options.Quality),
            ImageExportFormat.Png => image.Encode(SKEncodedImageFormat.Png, options.Quality),
            ImageExportFormat.Tiff => null,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        if (options.Format != ImageExportFormat.Tiff && encoded is null)
        {
            throw new InvalidDataException("Unable to encode the exported image.");
        }

        var directory = Path.GetDirectoryName(fullDestinationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (options.Format == ImageExportFormat.Tiff)
            {
                using (File.Create(temporaryPath))
                {
                }

                WriteTiff(temporaryPath, bitmap, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                await using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65_536,
                    FileOptions.Asynchronous);
                await encoded!.AsStream().CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Never replace a destination created while encoding/writing was in progress.
            // It could be another catalog original, not just a previous export.
            File.Move(temporaryPath, fullDestinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteTiff(string path, SKBitmap bitmap, CancellationToken cancellationToken)
    {
        using var output = Tiff.Open(path, "w") ?? throw new IOException("Unable to create the TIFF export.");
        SetTiffField(output, TiffTag.IMAGEWIDTH, bitmap.Width);
        SetTiffField(output, TiffTag.IMAGELENGTH, bitmap.Height);
        SetTiffField(output, TiffTag.SAMPLESPERPIXEL, (short)3);
        SetTiffField(output, TiffTag.BITSPERSAMPLE, (short)8);
        SetTiffField(output, TiffTag.ORIENTATION, Orientation.TOPLEFT);
        SetTiffField(output, TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        SetTiffField(output, TiffTag.PHOTOMETRIC, Photometric.RGB);
        SetTiffField(output, TiffTag.COMPRESSION, Compression.LZW);
        SetTiffField(output, TiffTag.ROWSPERSTRIP, Math.Min(bitmap.Height, 64));

        var profileBytes = SrgbIccProfile.Value;
        if (!output.SetField(TiffTag.ICCPROFILE, profileBytes.Length, profileBytes))
        {
            throw new InvalidDataException("Unable to embed the sRGB profile in the TIFF export.");
        }

        var pixels = bitmap.Pixels;
        var row = new byte[checked(bitmap.Width * 3)];
        for (var y = 0; y < bitmap.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = pixels[(y * bitmap.Width) + x];
                var offset = x * 3;
                row[offset] = color.Red;
                row[offset + 1] = color.Green;
                row[offset + 2] = color.Blue;
            }

            if (!output.WriteScanline(row, y))
            {
                throw new InvalidDataException($"Unable to encode TIFF scanline {y}.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void SetTiffField(Tiff output, TiffTag tag, object value)
    {
        if (!output.SetField(tag, value))
        {
            throw new InvalidDataException($"Unable to set TIFF tag {tag}.");
        }
    }

    private static byte[] LoadSrgbIccProfile()
    {
        using var profile = typeof(SkiaImageRenderer).Assembly.GetManifestResourceStream(
            "OpenLume.Imaging.Color.sRGB_v4_ICC_preference.icc")
            ?? throw new InvalidOperationException("The bundled sRGB ICC profile is missing.");
        using var bytes = new MemoryStream();
        profile.CopyTo(bytes);
        return bytes.ToArray();
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
        if (SupportedPhotoFormats.RawExtensions.Contains(Path.GetExtension(path)))
        {
            return LoadRaw(path, halfSizeRaw, cancellationToken);
        }

        var bounds = SKBitmap.DecodeBounds(path);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("Unable to read raster image dimensions.");
        }

        using var srgb = SKColorSpace.CreateSrgb();
        var target = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        return SKBitmap.Decode(path, target) ?? throw new InvalidDataException("Unable to decode raster image.");
    }

    private static SKBitmap ApplyRecipe(SKBitmap source, EditRecipe recipe, CancellationToken cancellationToken)
    {
        var edit = recipe.Normalize();
        using var opticsResult = edit.Optics!.IsNeutral
            ? null
            : ApplyOptics(source, edit.Optics, cancellationToken);
        var workingSource = opticsResult ?? source;
        var sourcePixels = workingSource.Pixels;
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

        var toneResult = CreateBitmap(workingSource.Width, workingSource.Height, outputPixels);
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

        try { ApplyLocalMasks(result, edit.LocalMasks!, cancellationToken); }
        catch { result.Dispose(); throw; }

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

    private static void ApplyLocalMasks(SKBitmap bitmap, IReadOnlyList<LocalMask> masks, CancellationToken cancellationToken)
    {
        var active = masks.Where(mask => mask.HasAdjustments).Select(mask => new PreparedLocalMask(mask, bitmap.Width)).ToArray();
        if (active.Length == 0) return;
        var pixels = bitmap.Pixels;
        var vertical = new double[active.Length];
        for (var y = 0; y < bitmap.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedY = (y + .5) / bitmap.Height;
            for (var m = 0; m < active.Length; m++)
            {
                ref readonly var prepared = ref active[m];
                var dy = normalizedY - prepared.CenterY;
                vertical[m] = prepared.Radial ? dy * dy / prepared.RadiusYSquared : dy * prepared.AxisY;
            }
            for (var x = 0; x < bitmap.Width; x++)
            {
                var index = y * bitmap.Width + x;
                var original = pixels[index];
                double red = original.Red, green = original.Green, blue = original.Blue;
                for (var m = 0; m < active.Length; m++)
                {
                    ref readonly var prepared = ref active[m];
                    var distance = prepared.Horizontal[x] + vertical[m];
                    double weight;
                    if (prepared.Radial)
                        weight = distance > 1 ? 0 : distance <= prepared.InnerSquared ? 1 :
                            Math.Clamp((1 - Math.Sqrt(distance)) * prepared.InverseFeather, 0, 1);
                    else weight = Math.Clamp(.5 - distance * prepared.InverseWidth, 0, 1);
                    weight = weight * weight * (3 - 2 * weight);
                    weight = (prepared.Inverted ? 1 - weight : weight) * prepared.Density;
                    if (weight <= .000001) continue;
                    var luminance = (red * .2126 + green * .7152 + blue * .0722) * prepared.LuminanceScale;
                    red = Math.Clamp(red + (red * prepared.ColorScale + luminance + prepared.RedBias - red) * weight, 0, 255);
                    green = Math.Clamp(green + (green * prepared.ColorScale + luminance + prepared.GreenBias - green) * weight, 0, 255);
                    blue = Math.Clamp(blue + (blue * prepared.ColorScale + luminance + prepared.BlueBias - blue) * weight, 0, 255);
                }
                pixels[index] = new SKColor(ToByte(red), ToByte(green), ToByte(blue), original.Alpha);
            }
        }
        bitmap.Pixels = pixels;
    }

    private readonly struct PreparedLocalMask
    {
        public readonly double[] Horizontal;
        public readonly double CenterY, RadiusYSquared, AxisY, InnerSquared, InverseFeather, InverseWidth, Density;
        public readonly double ColorScale, LuminanceScale, RedBias, GreenBias, BlueBias;
        public readonly bool Radial, Inverted;

        public PreparedLocalMask(LocalMask mask, int width)
        {
            Radial = mask.Kind == LocalMaskKind.Radial;
            Inverted = mask.Inverted;
            CenterY = mask.CenterY;
            RadiusYSquared = mask.RadiusY * mask.RadiusY;
            AxisY = Math.Sin(mask.AngleDegrees * Math.PI / 180);
            InnerSquared = Math.Pow(1 - mask.Feather, 2);
            InverseFeather = mask.Feather <= .000001 ? 0 : 1 / mask.Feather;
            InverseWidth = 1 / (2 * mask.RadiusX);
            Density = mask.Density;
            var axisX = Math.Cos(mask.AngleDegrees * Math.PI / 180);
            Horizontal = new double[width];
            for (var x = 0; x < width; x++)
            {
                var dx = (x + .5) / width - mask.CenterX;
                Horizontal[x] = Radial ? dx * dx / (mask.RadiusX * mask.RadiusX) : dx * axisX;
            }
            var contrast = 1 + mask.Contrast / 100;
            var scale = Math.Pow(2, mask.ExposureEv) * contrast;
            var saturation = 1 + mask.Saturation / 100;
            ColorScale = scale * saturation;
            LuminanceScale = scale * (1 - saturation);
            var r = mask.Temperature * .35 * contrast + 127.5 * (1 - contrast);
            var g = mask.Tint * .25 * contrast + 127.5 * (1 - contrast);
            var b = -mask.Temperature * .35 * contrast + 127.5 * (1 - contrast);
            var luminance = (r * .2126 + g * .7152 + b * .0722) * (1 - saturation);
            RedBias = r * saturation + luminance;
            GreenBias = g * saturation + luminance;
            BlueBias = b * saturation + luminance;
        }
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
            if (edit.ColorNoiseReduction > .001)
            {
                working = ReduceColorNoise(working, edit.ColorNoiseReduction / 100, cancellationToken);
                ownsWorking = true;
            }

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
                    sigma: (float)edit.SharpeningRadius,
                    amount: edit.Sharpening / 100 * 1.15,
                    cancellationToken,
                    masking: edit.SharpeningMasking / 100);
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

    private static SKBitmap ReduceColorNoise(SKBitmap source, double amount, CancellationToken cancellationToken)
    {
        using var blurred = Blur(source, .6f + (float)amount * 1.4f);
        var pixels = source.Pixels;
        var softPixels = blurred.Pixels;
        var output = new SKColor[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var original = pixels[index];
            var soft = softPixels[index];
            var luminance = original.Red * .2126 + original.Green * .7152 + original.Blue * .0722;
            var softLuminance = soft.Red * .2126 + soft.Green * .7152 + soft.Blue * .0722;
            // Reduce smoothing across luminance edges while preserving the original luminance.
            var mix = amount / (1 + Math.Abs(luminance - softLuminance) / 20);
            output[index] = new SKColor(
                ToByte(original.Red + (soft.Red - softLuminance - original.Red + luminance) * mix),
                ToByte(original.Green + (soft.Green - softLuminance - original.Green + luminance) * mix),
                ToByte(original.Blue + (soft.Blue - softLuminance - original.Blue + luminance) * mix),
                original.Alpha);
        }

        return CreateBitmap(source.Width, source.Height, output);
    }

    private static SKBitmap ApplyUnsharp(
        SKBitmap source,
        float sigma,
        double amount,
        CancellationToken cancellationToken,
        double masking = 0)
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
            var localAmount = amount;
            if (masking > .000001)
            {
                var luminanceDelta = Math.Abs(
                    (original.Red - soft.Red) * .2126 +
                    (original.Green - soft.Green) * .7152 +
                    (original.Blue - soft.Blue) * .0722);
                var threshold = masking * 24;
                var weight = Math.Clamp((luminanceDelta - threshold * .25) / Math.Max(.001, threshold * .75), 0, 1);
                localAmount *= weight * weight * (3 - 2 * weight);
            }
            output[index] = new SKColor(
                ToByte(original.Red + ((original.Red - soft.Red) * localAmount)),
                ToByte(original.Green + ((original.Green - soft.Green) * localAmount)),
                ToByte(original.Blue + ((original.Blue - soft.Blue) * localAmount)),
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
        var output = CreateSrgbBitmap(source.Width, source.Height, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
            IsAntialias = true
        };
        canvas.DrawBitmap(source, 0, 0, paint);
        return output;
    }

    private static SKBitmap CreateBitmap(int width, int height, SKColor[] pixels)
    {
        var bitmap = CreateSrgbBitmap(width, height, SKAlphaType.Premul);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static SKBitmap CreateSrgbBitmap(int width, int height, SKAlphaType alphaType)
    {
        using var srgb = SKColorSpace.CreateSrgb();
        return new SKBitmap(width, height, SKColorType.Rgba8888, alphaType, srgb);
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

        return CreateBitmap(processed.Width, processed.Height, pixels);
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static SKBitmap Resize(SKBitmap source, int maxDimension, bool smooth = false)
    {
        var scale = Math.Min(
            1d,
            Math.Min((double)maxDimension / source.Width, (double)maxDimension / source.Height));
        if (scale >= 1)
        {
            return source.Copy();
        }

        var bitmap = CreateSrgbBitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)), SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        if (smooth)
        {
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, bitmap.Width, bitmap.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        else canvas.DrawBitmap(source, new SKRect(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static SKBitmap Rotate(SKBitmap source, float degrees)
    {
        var bounds = new SKRect(0, 0, source.Width, source.Height);
        var matrix = SKMatrix.CreateRotationDegrees(degrees, source.Width / 2f, source.Height / 2f);
        bounds = matrix.MapRect(bounds);
        var bitmap = CreateSrgbBitmap(
            Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height)), SKAlphaType.Premul);
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

    private static SKBitmap ApplyOptics(
        SKBitmap source,
        OpticsCorrections corrections,
        CancellationToken cancellationToken)
    {
        var normalized = corrections.Normalize();
        var sourcePixels = source.Pixels;
        var outputPixels = new SKColor[sourcePixels.Length];
        var halfWidth = Math.Max(1, (source.Width - 1) / 2d);
        var halfHeight = Math.Max(1, (source.Height - 1) / 2d);
        var distortion = normalized.Distortion / 100d * .35;
        var aberration = normalized.ChromaticAberration / 100d * .012;
        if (aberration > .000001 && !HasGreenContrast(sourcePixels, cancellationToken)) aberration = 0;
        var midpoint = .08 + (normalized.VignetteMidpoint / 100d * .82);
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var normalizedY = (y - halfHeight) / halfHeight;
            for (var x = 0; x < source.Width; x++)
            {
                var normalizedX = (x - halfWidth) / halfWidth;
                var radiusSquared = (normalizedX * normalizedX) + (normalizedY * normalizedY);
                var distortionScale = 1 + (distortion * radiusSquared);
                var baseX = halfWidth + (normalizedX * distortionScale * halfWidth);
                var baseY = halfHeight + (normalizedY * distortionScale * halfHeight);
                var redScale = 1 + (aberration * radiusSquared);
                var blueScale = 1 - (aberration * radiusSquared);
                var basePixel = Math.Abs(distortion) < .000001
                    ? ToSampledPixel(sourcePixels[(y * source.Width) + x])
                    : SamplePixel(sourcePixels, source.Width, source.Height, baseX, baseY);
                var red = basePixel.Red;
                var blue = basePixel.Blue;
                if (aberration > .000001 &&
                    (Math.Abs(red - basePixel.Green) > 1 || Math.Abs(blue - basePixel.Green) > 1) &&
                    HasNeutralEdge(
                    sourcePixels, source.Width, source.Height, baseX, baseY,
                    halfWidth, halfHeight, aberration * radiusSquared))
                {
                    var redOutward = SampleChannel(sourcePixels, source.Width, source.Height,
                        halfWidth + ((baseX - halfWidth) * redScale),
                        halfHeight + ((baseY - halfHeight) * redScale), channel: 0);
                    var redInward = SampleChannel(sourcePixels, source.Width, source.Height,
                        halfWidth + ((baseX - halfWidth) * blueScale),
                        halfHeight + ((baseY - halfHeight) * blueScale), channel: 0);
                    var blueInward = SampleChannel(sourcePixels, source.Width, source.Height,
                        halfWidth + ((baseX - halfWidth) * blueScale),
                        halfHeight + ((baseY - halfHeight) * blueScale), channel: 2);
                    var blueOutward = SampleChannel(sourcePixels, source.Width, source.Height,
                        halfWidth + ((baseX - halfWidth) * redScale),
                        halfHeight + ((baseY - halfHeight) * redScale), channel: 2);
                    red = ClosestTo(basePixel.Green, red, redInward, redOutward);
                    blue = ClosestTo(basePixel.Green, blue, blueInward, blueOutward);
                }
                var radius = Math.Clamp(Math.Sqrt(radiusSquared / 2), 0, 1);
                var edge = Math.Clamp((radius - midpoint) / Math.Max(.01, 1 - midpoint), 0, 1);
                var vignetteFactor = Math.Clamp(
                    1 + (normalized.LensVignette / 100d * edge * edge * 1.35),
                    .15,
                    2.5);
                outputPixels[(y * source.Width) + x] = new SKColor(
                    ToByte(red * vignetteFactor),
                    ToByte(basePixel.Green * vignetteFactor),
                    ToByte(blue * vignetteFactor),
                    ToByte(basePixel.Alpha));
            }
        }

        return CreateBitmap(source.Width, source.Height, outputPixels);
    }

    private static bool HasNeutralEdge(
        SKColor[] pixels, int width, int height, double x, double y,
        double centerX, double centerY, double displacement)
    {
        var dx = x - centerX;
        var dy = y - centerY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < .000001) return false;

        // Look beyond the possible fringe, along the same radial sampling direction.
        // Green proximity alone cannot distinguish aberration from a real colored subject.
        var scale = (2 * displacement) + (4 / distance);
        var firstX = x - (dx * scale);
        var firstY = y - (dy * scale);
        var secondX = x + (dx * scale);
        var secondY = y + (dy * scale);
        if (firstX < 0 || firstY < 0 || secondX < 0 || secondY < 0 ||
            firstX > width - 1 || secondX > width - 1 ||
            firstY > height - 1 || secondY > height - 1) return false;

        // Classification only needs nearby source colors; reserve bilinear sampling for corrections.
        var first = pixels[((int)Math.Round(firstY) * width) + (int)Math.Round(firstX)];
        if (!IsNeutralOpaque(first)) return false;
        var second = pixels[((int)Math.Round(secondY) * width) + (int)Math.Round(secondX)];
        return IsNeutralOpaque(second) &&
            Math.Abs(first.Green - second.Green) > 51;
    }

    private static bool IsNeutralOpaque(SKColor pixel) =>
        pixel.Alpha > 252 && Math.Abs(pixel.Red - pixel.Green) < 20 &&
        Math.Abs(pixel.Blue - pixel.Green) < 20;

    private static bool HasGreenContrast(SKColor[] pixels, CancellationToken cancellationToken)
    {
        // No sampled edge can exceed the green contrast of the entire source image.
        // Flat or low-contrast images therefore need no per-pixel fringe classification.
        var minimum = 255;
        var maximum = 0;
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            var green = pixels[index].Green;
            minimum = Math.Min(minimum, green);
            maximum = Math.Max(maximum, green);
            if (maximum - minimum > 51) return true;
        }

        return false;
    }

    private static double SampleChannel(
        SKColor[] pixels,
        int width,
        int height,
        double x,
        double y,
        int channel)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 0;
        }

        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = Math.Min(width - 1, left + 1);
        var bottom = Math.Min(height - 1, top + 1);
        var xFraction = x - left;
        var yFraction = y - top;
        var topValue = Lerp(
            ReadChannel(pixels[(top * width) + left], channel),
            ReadChannel(pixels[(top * width) + right], channel),
            xFraction);
        var bottomValue = Lerp(
            ReadChannel(pixels[(bottom * width) + left], channel),
            ReadChannel(pixels[(bottom * width) + right], channel),
            xFraction);
        return Lerp(topValue, bottomValue, yFraction);
    }

    private static SampledPixel SamplePixel(SKColor[] pixels, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return default;
        }

        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = Math.Min(width - 1, left + 1);
        var bottom = Math.Min(height - 1, top + 1);
        var xFraction = x - left;
        var yFraction = y - top;
        var topLeft = pixels[(top * width) + left];
        var topRight = pixels[(top * width) + right];
        var bottomLeft = pixels[(bottom * width) + left];
        var bottomRight = pixels[(bottom * width) + right];
        return new SampledPixel(
            Bilinear(topLeft.Red, topRight.Red, bottomLeft.Red, bottomRight.Red, xFraction, yFraction),
            Bilinear(topLeft.Green, topRight.Green, bottomLeft.Green, bottomRight.Green, xFraction, yFraction),
            Bilinear(topLeft.Blue, topRight.Blue, bottomLeft.Blue, bottomRight.Blue, xFraction, yFraction),
            Bilinear(topLeft.Alpha, topRight.Alpha, bottomLeft.Alpha, bottomRight.Alpha, xFraction, yFraction));
    }

    private static SampledPixel ToSampledPixel(SKColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);

    private static double Bilinear(
        double topLeft,
        double topRight,
        double bottomLeft,
        double bottomRight,
        double xFraction,
        double yFraction) =>
        Lerp(Lerp(topLeft, topRight, xFraction), Lerp(bottomLeft, bottomRight, xFraction), yFraction);

    private static byte ReadChannel(SKColor color, int channel) => channel switch
    {
        0 => color.Red,
        1 => color.Green,
        2 => color.Blue,
        _ => color.Alpha
    };

    private static double Lerp(double first, double second, double amount) =>
        first + ((second - first) * amount);

    private static double ClosestTo(double target, double first, double second, double third)
    {
        var closest = first;
        if (Math.Abs(second - target) < Math.Abs(closest - target))
        {
            closest = second;
        }

        return Math.Abs(third - target) < Math.Abs(closest - target) ? third : closest;
    }

    private static SKBitmap ApplyCrop(SKBitmap source, CropGeometry geometry)
    {
        var width = Math.Clamp((int)Math.Round(source.Width * geometry.Width), 1, source.Width);
        var height = Math.Clamp((int)Math.Round(source.Height * geometry.Height), 1, source.Height);
        var left = Math.Clamp((int)Math.Round(source.Width * geometry.X), 0, source.Width - width);
        var top = Math.Clamp((int)Math.Round(source.Height * geometry.Y), 0, source.Height - height);
        var output = CreateSrgbBitmap(width, height, source.AlphaType);
        if (!source.ExtractSubset(output, new SKRectI(left, top, left + width, top + height)))
        {
            output.Dispose();
            throw new InvalidDataException("The normalized crop could not be extracted from the rendered image.");
        }

        return output;
    }
}
