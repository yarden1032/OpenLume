using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenLume.Core.Domain;

namespace OpenLume.Infrastructure.Presets;

public static class XmpSidecarWriter
{
    private const string CameraRawNamespace = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmpMetaNamespace = "adobe:ns:meta/";
    private const long MaximumDocumentBytes = 1_000_000;

    private static readonly string[] MixerChannels =
        ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];
    private static readonly string[] MixerComponents = ["Hue", "Saturation", "Luminance"];
    private static readonly HashSet<string> RecipePropertiesToReplace = CreateRecipePropertySet();

    private static readonly XNamespace Crs = CameraRawNamespace;
    private static readonly XNamespace Rdf = RdfNamespace;
    private static readonly XNamespace XmpMeta = XmpMetaNamespace;

    public static XmpSidecarExportResult CreateSidecar(
        string sourcePhotoPath,
        EditRecipe recipe,
        string? existingXmp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePhotoPath);
        ArgumentNullException.ThrowIfNull(recipe);

        var document = string.IsNullOrWhiteSpace(existingXmp)
            ? CreateEmptyDocument()
            : ParseExistingDocument(existingXmp);
        return CreateSidecarDocument(sourcePhotoPath, recipe, document);
    }

    private static XmpSidecarExportResult CreateSidecarDocument(
        string sourcePhotoPath,
        EditRecipe recipe,
        XDocument document)
    {
        var normalizedRecipe = recipe.Normalize();
        var warnings = new List<string>();
        var values = CreateRecipeProperties(normalizedRecipe, warnings);
        values["HasSettings"] = HasAdobeSettings(normalizedRecipe) ? "True" : "False";
        values["RawFileName"] = Path.GetFileName(sourcePhotoPath);

        var description = GetOrCreateDescription(document);

        foreach (var element in document.Descendants()
                     .Where(element => element.Name.Namespace == Crs && RecipePropertiesToReplace.Contains(element.Name.LocalName))
                     .ToArray())
        {
            element.Remove();
        }

        foreach (var attribute in (document.Root?.DescendantsAndSelf() ?? [])
                     .SelectMany(element => element.Attributes())
                     .Where(attribute => attribute.Name.Namespace == Crs && RecipePropertiesToReplace.Contains(attribute.Name.LocalName))
                     .ToArray())
        {
            attribute.Remove();
        }

        foreach (var (name, value) in values)
        {
            description.Add(new XElement(Crs + name, value));
        }

        if (normalizedRecipe.LocalMasks!.Count > 0)
        {
            warnings.Add("OpenLume local masks are not represented in Camera Raw XMP and were not exported.");
        }

        if (normalizedRecipe.Optics!.ChromaticAberration > 0)
        {
            warnings.Add("OpenLume chromatic-aberration strength was exported as Adobe's AutoLateralCA on/off setting.");
        }

        return new XmpSidecarExportResult(Serialize(document), warnings);
    }

    public static async Task<XmpSidecarExportResult> WriteSidecarAsync(
        string sourcePhotoPath,
        string destinationPath,
        EditRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePhotoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(recipe);
        cancellationToken.ThrowIfCancellationRequested();

        var fullSourcePath = Path.GetFullPath(sourcePhotoPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (PathsEqual(fullSourcePath, fullDestinationPath))
        {
            throw new IOException("An XMP sidecar cannot replace the original photo.");
        }

        byte[]? originalBytes = null;
        string? originalHash = null;
        if (File.Exists(fullDestinationPath))
        {
            var info = new FileInfo(fullDestinationPath);
            if (info.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The existing XMP sidecar is larger than the 1 MB safety limit.");
            }

            originalBytes = await File.ReadAllBytesAsync(fullDestinationPath, cancellationToken).ConfigureAwait(false);
            originalHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        }

        var existingDocument = originalBytes is { Length: > 0 }
            ? ParseExistingDocument(originalBytes)
            : CreateEmptyDocument();
        var result = CreateSidecarDocument(fullSourcePath, recipe, existingDocument);
        var bytes = Encoding.UTF8.GetBytes(result.Xmp);
        if (bytes.LongLength > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The exported XMP sidecar exceeds the 1 MB safety limit.");
        }

        var directory = Path.GetDirectoryName(fullDestinationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullDestinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
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
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (originalHash is null)
            {
                File.Move(temporaryPath, fullDestinationPath, overwrite: false);
            }
            else
            {
                if (!File.Exists(fullDestinationPath))
                {
                    throw new IOException("The existing XMP sidecar changed while it was being updated.");
                }

                var currentHash = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(fullDestinationPath, cancellationToken).ConfigureAwait(false)));
                if (!string.Equals(originalHash, currentHash, StringComparison.Ordinal))
                {
                    throw new IOException("The existing XMP sidecar changed while it was being updated.");
                }

                File.Move(temporaryPath, fullDestinationPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return result;
    }

    private static Dictionary<string, string> CreateRecipeProperties(EditRecipe recipe, List<string> warnings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Exposure2012"] = Real(recipe.ExposureEv),
            ["Contrast2012"] = Integer(recipe.Contrast, warnings),
            ["Saturation"] = Integer(recipe.Saturation, warnings),
            ["Temperature"] = Integer(Math.Max(2000, 5500 + (recipe.Temperature * 50)), warnings),
            ["Tint"] = Integer(recipe.Tint, warnings),
            ["Highlights2012"] = Integer(recipe.Highlights, warnings),
            ["Shadows2012"] = Integer(recipe.Shadows, warnings),
            ["Whites2012"] = Integer(recipe.Whites, warnings),
            ["Blacks2012"] = Integer(recipe.Blacks, warnings),
            ["Vibrance"] = Integer(recipe.Vibrance, warnings),
            ["PostCropVignetteAmount"] = Integer(recipe.Vignette, warnings),
            ["Texture"] = Integer(recipe.Texture, warnings),
            ["Clarity2012"] = Integer(recipe.Clarity, warnings),
            ["Dehaze"] = Integer(recipe.Dehaze, warnings),
            ["Sharpness"] = Integer(recipe.Sharpening, warnings),
            ["SharpenRadius"] = Real(recipe.SharpeningRadius),
            ["SharpenEdgeMasking"] = Integer(recipe.SharpeningMasking, warnings),
            ["LuminanceSmoothing"] = Integer(recipe.NoiseReduction, warnings),
            ["ColorNoiseReduction"] = Integer(recipe.ColorNoiseReduction, warnings),
            ["GrainAmount"] = Integer(recipe.Grain, warnings),
            ["CropAngle"] = Real(recipe.RotationDegrees),
            ["HasCrop"] = (recipe.Crop!.HasCrop || Math.Abs(recipe.RotationDegrees) > .000001) ? "True" : "False",
            ["CropLeft"] = Real(recipe.Crop.X),
            ["CropTop"] = Real(recipe.Crop.Y),
            ["CropRight"] = Real(recipe.Crop.X + recipe.Crop.Width),
            ["CropBottom"] = Real(recipe.Crop.Y + recipe.Crop.Height),
            ["Orientation"] = Orientation(recipe.Crop),
            ["LensManualDistortionAmount"] = Integer(recipe.Optics!.Distortion, warnings),
            ["AutoLateralCA"] = recipe.Optics.ChromaticAberration > 0 ? "True" : "False",
            ["VignetteAmount"] = Integer(recipe.Optics.LensVignette, warnings),
            ["VignetteMidpoint"] = Integer(recipe.Optics.VignetteMidpoint, warnings),
            ["ParametricHighlights"] = Integer(recipe.ToneCurve!.Highlights, warnings),
            ["ParametricLights"] = Integer(recipe.ToneCurve.Lights, warnings),
            ["ParametricDarks"] = Integer(recipe.ToneCurve.Darks, warnings),
            ["ParametricShadows"] = Integer(recipe.ToneCurve.Shadows, warnings),
            ["ParametricShadowSplit"] = Integer(recipe.ToneCurve.ShadowSplit, warnings),
            ["ParametricMidtoneSplit"] = Integer(recipe.ToneCurve.MidtoneSplit, warnings),
            ["ParametricHighlightSplit"] = Integer(recipe.ToneCurve.HighlightSplit, warnings)
        };

        if (recipe.Temperature < -70)
        {
            warnings.Add("Temperature below Adobe's 2000 K minimum was clamped to 2000 K.");
        }

        var mixer = recipe.ColorMixer!.Normalize();
        foreach (var channel in MixerChannels)
        {
            var adjustment = GetMixerChannel(mixer, channel);
            values[$"HueAdjustment{channel}"] = Integer(adjustment.Hue, warnings);
            values[$"SaturationAdjustment{channel}"] = Integer(adjustment.Saturation, warnings);
            values[$"LuminanceAdjustment{channel}"] = Integer(adjustment.Luminance, warnings);
        }

        return values;
    }

    private static HslChannelAdjustment GetMixerChannel(HslColorMixer mixer, string channel) => channel switch
    {
        "Red" => mixer.Red!,
        "Orange" => mixer.Orange!,
        "Yellow" => mixer.Yellow!,
        "Green" => mixer.Green!,
        "Aqua" => mixer.Aqua!,
        "Blue" => mixer.Blue!,
        "Purple" => mixer.Purple!,
        "Magenta" => mixer.Magenta!,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private static string Orientation(CropGeometry crop)
    {
        var turns = crop.QuarterTurns;
        var horizontal = crop.FlipHorizontal;
        var vertical = crop.FlipVertical;
        return (turns, horizontal, vertical) switch
        {
            (0, false, false) => "1",
            (0, true, false) => "2",
            (0, true, true) => "3",
            (0, false, true) => "4",
            (1, true, false) => "5",
            (1, false, false) => "6",
            (1, false, true) => "7",
            (1, true, true) => "8",
            (2, false, false) => "3",
            (2, true, false) => "4",
            (2, false, true) => "2",
            (2, true, true) => "1",
            (3, false, false) => "8",
            (3, true, false) => "7",
            (3, false, true) => "5",
            (3, true, true) => "6",
            _ => "1"
        };
    }

    private static bool HasAdobeSettings(EditRecipe recipe)
    {
        var defaultRecipe = EditRecipe.Default;
        var normalized = recipe.Normalize();
        return normalized.ExposureEv != 0 || normalized.Contrast != 0 || normalized.Saturation != 0 ||
            normalized.Temperature != 0 || normalized.Tint != 0 || normalized.RotationDegrees != 0 ||
            normalized.Highlights != 0 || normalized.Shadows != 0 || normalized.Whites != 0 || normalized.Blacks != 0 ||
            normalized.Vibrance != 0 || normalized.Vignette != 0 || normalized.Texture != 0 || normalized.Clarity != 0 ||
            normalized.Dehaze != 0 || normalized.Sharpening != 0 || normalized.NoiseReduction != 0 ||
            normalized.ColorNoiseReduction != 0 || normalized.Grain != 0 || normalized.SharpeningMasking != 0 ||
            normalized.SharpeningRadius != defaultRecipe.SharpeningRadius || normalized.Crop!.HasCrop ||
            normalized.Crop.HasOrientation || !normalized.Optics!.IsNeutral || !normalized.ToneCurve!.IsIdentity ||
            HasColorMixerSettings(normalized.ColorMixer!) || normalized.LocalMasks!.Count > 0;
    }

    private static bool HasColorMixerSettings(HslColorMixer mixer) =>
        MixerChannels.Any(channel =>
        {
            var adjustment = GetMixerChannel(mixer, channel);
            return adjustment.Hue != 0 || adjustment.Saturation != 0 || adjustment.Luminance != 0;
        });

    private static string Integer(double value, List<string> warnings)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > .000001 && !warnings.Contains("Some slider values were rounded to integers for Adobe Camera Raw compatibility."))
        {
            warnings.Add("Some slider values were rounded to integers for Adobe Camera Raw compatibility.");
        }

        return rounded.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string Real(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static XDocument CreateEmptyDocument() => new(
        new XDeclaration("1.0", "UTF-8", null),
        new XElement(XmpMeta + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", XmpMetaNamespace),
            new XAttribute(XmpMeta + "xmptk", "OpenLume"),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", RdfNamespace),
                new XElement(Rdf + "Description",
                    new XAttribute(Rdf + "about", string.Empty),
                    new XAttribute(XNamespace.Xmlns + "crs", CameraRawNamespace)))));

    private static XElement GetOrCreateDescription(XDocument document)
    {
        var rdf = document.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            var root = document.Root ?? throw new InvalidDataException("The XMP document has no root element.");
            rdf = new XElement(Rdf + "RDF");
            root.Add(rdf);
        }

        var description = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (description is null)
        {
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
            rdf.Add(description);
        }

        return description;
    }

    private static XDocument ParseExistingDocument(string xmp)
    {
        if (Encoding.UTF8.GetByteCount(xmp) > MaximumDocumentBytes)
        {
            throw new InvalidDataException("The existing XMP sidecar is larger than the 1 MB safety limit.");
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDocumentBytes,
                IgnoreComments = false
            };
            using var reader = XmlReader.Create(new StringReader(xmp), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The existing XMP sidecar is invalid and was left unchanged.", exception);
        }
    }

    private static XDocument ParseExistingDocument(byte[] xmp)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDocumentBytes,
                IgnoreComments = false
            };
            using var stream = new MemoryStream(xmp, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The existing XMP sidecar is invalid and was left unchanged.", exception);
        }
    }

    private static string Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var xmlWriter = XmlWriter.Create(stream, new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = true,
            Encoding = new UTF8Encoding(false)
        }))
        {
            document.Save(xmlWriter);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HashSet<string> CreateRecipePropertySet()
    {
        var values = new HashSet<string>(StringComparer.Ordinal)
        {
            "Exposure", "Exposure2012", "Contrast", "Contrast2012", "Saturation", "Temperature", "Tint",
            "Highlights2012", "Shadows2012", "Whites2012", "Blacks2012", "Vibrance", "PostCropVignetteAmount",
            "Texture", "Clarity", "Clarity2012", "Dehaze", "Sharpness", "SharpenRadius", "SharpenEdgeMasking",
            "LuminanceSmoothing", "ColorNoiseReduction", "GrainAmount", "CropAngle", "HasCrop", "CropLeft",
            "CropTop", "CropRight", "CropBottom", "Orientation", "LensManualDistortionAmount", "AutoLateralCA",
            "DefringePurpleAmount", "DefringeGreenAmount", "VignetteAmount", "VignetteMidpoint", "ParametricHighlights",
            "ParametricLights", "ParametricDarks", "ParametricShadows", "ParametricShadowSplit", "ParametricMidtoneSplit",
            "ParametricHighlightSplit", "HasSettings", "RawFileName"
        };

        foreach (var channel in MixerChannels)
            foreach (var component in MixerComponents)
                values.Add($"{component}Adjustment{channel}");

        return values;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record XmpSidecarExportResult(string Xmp, IReadOnlyList<string> Warnings);
