using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.Infrastructure.Presets;

public sealed class XmpPresetImporter : IPresetImporter
{
    private const string CameraRawNamespace = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private const long MaximumDocumentCharacters = 1_000_000;
    private static readonly HashSet<string> MetadataFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "PresetName", "presName", "UUID", "Version", "ProcessVersion", "HasSettings",
        "SupportsAmount", "SupportsColor", "SupportsMonochrome", "SupportsHighDynamicRange", "SupportsNormalDynamicRange"
    };
    private static readonly Dictionary<string, string> SupportedFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Exposure"] = nameof(EditRecipe.ExposureEv),
            ["Exposure2012"] = nameof(EditRecipe.ExposureEv),
            ["Contrast"] = nameof(EditRecipe.Contrast),
            ["Contrast2012"] = nameof(EditRecipe.Contrast),
            ["Saturation"] = nameof(EditRecipe.Saturation),
            ["Temperature"] = nameof(EditRecipe.Temperature),
            ["Tint"] = nameof(EditRecipe.Tint),
            ["Highlights2012"] = nameof(EditRecipe.Highlights),
            ["Shadows2012"] = nameof(EditRecipe.Shadows),
            ["Whites2012"] = nameof(EditRecipe.Whites),
            ["Blacks2012"] = nameof(EditRecipe.Blacks),
            ["Vibrance"] = nameof(EditRecipe.Vibrance),
            ["PostCropVignetteAmount"] = nameof(EditRecipe.Vignette),
            ["Texture"] = nameof(EditRecipe.Texture),
            ["Clarity"] = nameof(EditRecipe.Clarity),
            ["Clarity2012"] = nameof(EditRecipe.Clarity),
            ["Dehaze"] = nameof(EditRecipe.Dehaze),
            ["Sharpness"] = nameof(EditRecipe.Sharpening),
            ["LuminanceSmoothing"] = nameof(EditRecipe.NoiseReduction),
            ["GrainAmount"] = nameof(EditRecipe.Grain)
        };
    private static readonly string[] MixerChannels =
        ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];
    private static readonly string[] MixerComponents = ["Hue", "Saturation", "Luminance"];

    public PresetImportResult Import(string xmp)
    {
        ArgumentNullException.ThrowIfNull(xmp);
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDocumentCharacters,
                IgnoreComments = true
            };
            using var reader = XmlReader.Create(new StringReader(xmp), settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string? name = null;

            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes()
                             .Where(attribute => attribute.Name.NamespaceName == CameraRawNamespace))
                {
                    if (IsName(attribute.Name.LocalName)) name ??= attribute.Value.Trim();
                    ParseValue(attribute.Name.LocalName, attribute.Value, values, unsupported, warnings);
                }

                if (element.Name.NamespaceName == CameraRawNamespace)
                {
                    if (IsName(element.Name.LocalName)) name ??= element.Value.Trim();
                    else if (!element.HasElements)
                        ParseValue(element.Name.LocalName, element.Value, values, unsupported, warnings);
                    else if (!MetadataFields.Contains(element.Name.LocalName))
                        unsupported.Add(element.Name.LocalName);
                }
            }

            var recipe = new EditRecipe(
                ExposureEv: values.GetValueOrDefault(nameof(EditRecipe.ExposureEv)),
                Contrast: values.GetValueOrDefault(nameof(EditRecipe.Contrast)),
                Saturation: values.GetValueOrDefault(nameof(EditRecipe.Saturation)),
                Temperature: values.GetValueOrDefault(nameof(EditRecipe.Temperature)),
                Tint: values.GetValueOrDefault(nameof(EditRecipe.Tint)),
                Highlights: values.GetValueOrDefault(nameof(EditRecipe.Highlights)),
                Shadows: values.GetValueOrDefault(nameof(EditRecipe.Shadows)),
                Whites: values.GetValueOrDefault(nameof(EditRecipe.Whites)),
                Blacks: values.GetValueOrDefault(nameof(EditRecipe.Blacks)),
                Vibrance: values.GetValueOrDefault(nameof(EditRecipe.Vibrance)),
                Vignette: values.GetValueOrDefault(nameof(EditRecipe.Vignette)),
                Texture: values.GetValueOrDefault(nameof(EditRecipe.Texture)),
                Clarity: values.GetValueOrDefault(nameof(EditRecipe.Clarity)),
                Dehaze: values.GetValueOrDefault(nameof(EditRecipe.Dehaze)),
                Sharpening: values.GetValueOrDefault(nameof(EditRecipe.Sharpening)),
                NoiseReduction: values.GetValueOrDefault(nameof(EditRecipe.NoiseReduction)),
                Grain: values.GetValueOrDefault(nameof(EditRecipe.Grain)),
                ColorMixer: new HslColorMixer(
                    ReadMixerChannel(values, "Red"),
                    ReadMixerChannel(values, "Orange"),
                    ReadMixerChannel(values, "Yellow"),
                    ReadMixerChannel(values, "Green"),
                    ReadMixerChannel(values, "Aqua"),
                    ReadMixerChannel(values, "Blue"),
                    ReadMixerChannel(values, "Purple"),
                    ReadMixerChannel(values, "Magenta"))).Normalize();
            return new PresetImportResult(
                string.IsNullOrWhiteSpace(name) ? null : name,
                recipe,
                unsupported.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                warnings,
                true);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            warnings.Add($"Invalid XMP: {exception.Message}");
            return new PresetImportResult(null, EditRecipe.Default, unsupported.ToArray(), warnings, false);
        }
    }

    private static bool IsName(string field) =>
        field.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("PresetName", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("presName", StringComparison.OrdinalIgnoreCase);

    private static void ParseValue(
        string field,
        string rawValue,
        Dictionary<string, double> values,
        HashSet<string> unsupported,
        List<string> warnings)
    {
        if (MetadataFields.Contains(field)) return;
        if (TryGetMixerKey(field, out var mixerKey))
        {
            if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var mixerValue))
            {
                warnings.Add($"Invalid value for {field}: {rawValue}");
                return;
            }

            values[mixerKey] = mixerValue;
            return;
        }

        if (!SupportedFields.TryGetValue(field, out var recipeField))
        {
            unsupported.Add(field);
            return;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            warnings.Add($"Invalid value for {field}: {rawValue}");
            return;
        }

        if (field.Equals("Temperature", StringComparison.OrdinalIgnoreCase) && value > 100)
        {
            value = (value - 5500) / 50;
            warnings.Add("Adobe's absolute color temperature was converted to OpenLume's relative temperature scale.");
        }

        values[recipeField] = value;
    }

    private static bool TryGetMixerKey(string field, out string key)
    {
        foreach (var component in MixerComponents)
        {
            foreach (var channel in MixerChannels)
            {
                if (field.Equals($"{component}Adjustment{channel}", StringComparison.OrdinalIgnoreCase))
                {
                    key = $"ColorMixer.{channel}.{component}";
                    return true;
                }
            }
        }

        key = string.Empty;
        return false;
    }

    private static HslChannelAdjustment ReadMixerChannel(
        IReadOnlyDictionary<string, double> values,
        string channel) => new(
            values.GetValueOrDefault($"ColorMixer.{channel}.Hue"),
            values.GetValueOrDefault($"ColorMixer.{channel}.Saturation"),
            values.GetValueOrDefault($"ColorMixer.{channel}.Luminance"));
}
