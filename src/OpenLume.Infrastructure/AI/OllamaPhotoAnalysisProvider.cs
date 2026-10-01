using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.Infrastructure.AI;

public sealed class OllamaPhotoAnalysisProvider : IPhotoAnalysisProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly bool _ownsClient;

    public OllamaPhotoAnalysisProvider(OllamaOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? OllamaOptions.LocalDefault;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = _options.Endpoint;
        _httpClient.Timeout = _options.Timeout;
    }

    public string Name => $"Ollama ({_options.Model})";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return document.RootElement.GetProperty("models").EnumerateArray().Any(model =>
                model.TryGetProperty("name", out var name) &&
                name.GetString()?.StartsWith(_options.Model, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<PhotoAnalysis> AnalyzeAsync(
        PhotoAsset photo,
        byte[] previewJpeg,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(previewJpeg);

        var currentRecipe = JsonSerializer.Serialize(photo.Edit.Normalize());
        var prompt = $"You are the Develop Director for a nondestructive photo editor. Analyze the supplied preview and propose absolute target parameter values only. Never regenerate, replace, inpaint, or synthesize pixels. The current recipe is {currentRecipe}. Return strict JSON without markdown: summary (string), technicalScore and aestheticScore (0..1), suggestedPick (boolean), tags (up to 8 strings), intent (short string), editConfidence (0..1), warnings (up to 8 strings), decisions (array of objects with parameter and reason), and suggestedEdit containing only parameters you intentionally control from exposureEv (-2..2), contrast, highlights, shadows, whites, blacks, temperature, tint, vibrance, saturation, vignette, texture, clarity, dehaze (-100..100), sharpening, noiseReduction, grain (0..100), rotationDegrees (-45..45), an optional colorMixer object, and an optional toneCurve object. colorMixer may contain red, orange, yellow, green, aqua, blue, purple, and magenta objects, each with hue, saturation, and luminance values (-100..100). toneCurve may contain highlights, lights, darks, and shadows values (-100..100); do not propose split points. Omitted parameters remain unchanged. Prefer restrained photographic corrections and explain material changes.";

        var request = new
        {
            model = _options.Model,
            stream = false,
            format = "json",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    images = new[] { Convert.ToBase64String(previewJpeg) }
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("api/chat", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var envelope = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var content = envelope.RootElement.GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("Ollama returned an empty analysis.");
        using var result = JsonDocument.Parse(content);
        var root = result.RootElement;
        var edit = root.GetProperty("suggestedEdit");

        var recipe = new EditRecipe(
            ExposureEv: GetBoundedDouble(edit, "exposureEv", -2, 2),
            Contrast: GetBoundedDouble(edit, "contrast", -100, 100),
            Saturation: GetBoundedDouble(edit, "saturation", -100, 100),
            Temperature: GetBoundedDouble(edit, "temperature", -100, 100),
            Tint: GetBoundedDouble(edit, "tint", -100, 100),
            RotationDegrees: GetBoundedDouble(edit, "rotationDegrees", -45, 45),
            Highlights: GetBoundedDouble(edit, "highlights", -100, 100),
            Shadows: GetBoundedDouble(edit, "shadows", -100, 100),
            Whites: GetBoundedDouble(edit, "whites", -100, 100),
            Blacks: GetBoundedDouble(edit, "blacks", -100, 100),
            Vibrance: GetBoundedDouble(edit, "vibrance", -100, 100),
            Vignette: GetBoundedDouble(edit, "vignette", -100, 100),
            Texture: GetBoundedDouble(edit, "texture", -100, 100),
            Clarity: GetBoundedDouble(edit, "clarity", -100, 100),
            Dehaze: GetBoundedDouble(edit, "dehaze", -100, 100),
            Sharpening: GetBoundedDouble(edit, "sharpening", 0, 100),
            NoiseReduction: GetBoundedDouble(edit, "noiseReduction", 0, 100),
            Grain: GetBoundedDouble(edit, "grain", 0, 100),
            ColorMixer: ReadColorMixer(edit),
            ToneCurve: ReadToneCurve(edit)).Normalize();
        var suggestion = new DevelopSuggestion(
            Guid.NewGuid(),
            GetOptionalString(root, "intent", "Balanced automatic development", 240),
            GetBoundedDouble(root, "editConfidence", 0, 1),
            recipe,
            ReadDecisions(root),
            ReadStringArray(root, "warnings", 8, 240),
            DevelopSuggestionStatus.Pending,
            DateTimeOffset.UtcNow,
            ReadControlledParameters(edit)).Normalize();

        return new PhotoAnalysis(
            GetRequiredString(root, "summary", 600),
            GetBoundedDouble(root, "technicalScore", 0, 1),
            GetBoundedDouble(root, "aestheticScore", 0, 1),
            root.TryGetProperty("suggestedPick", out var pick) && pick.ValueKind == JsonValueKind.True,
            ReadTags(root),
            recipe,
            suggestion);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string GetRequiredString(JsonElement element, string name, int maximumLength)
    {
        var value = element.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Ollama response property '{name}' was empty.");
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string GetOptionalString(
        JsonElement element,
        string name,
        string fallback,
        int maximumLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        var value = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static double GetBoundedDouble(JsonElement element, string name, double minimum, double maximum)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        double value;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return Math.Clamp(value, minimum, maximum);
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return Math.Clamp(value, minimum, maximum);
        }

        return 0;
    }

    private static string[] ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return tags.EnumerateArray()
            .Where(tag => tag.ValueKind == JsonValueKind.String)
            .Select(tag => tag.GetString()?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!.Length <= 40 ? tag : tag[..40])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string[] ReadStringArray(
        JsonElement root,
        string name,
        int maximumItems,
        int maximumLength)
    {
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Length <= maximumLength ? value : value[..maximumLength])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumItems)
            .ToArray();
    }

    private static EditParameterDecision[] ReadDecisions(JsonElement root)
    {
        if (!root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return decisions.EnumerateArray()
            .Where(decision => decision.ValueKind == JsonValueKind.Object)
            .Select(decision => new EditParameterDecision(
                GetOptionalString(decision, "parameter", string.Empty, 40),
                GetOptionalString(decision, "reason", string.Empty, 240)))
            .Where(decision => !string.IsNullOrWhiteSpace(decision.Parameter) &&
                               !string.IsNullOrWhiteSpace(decision.Reason))
            .Take(16)
            .ToArray();
    }

    private static string[] ReadControlledParameters(JsonElement edit)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["exposureEv"] = nameof(EditRecipe.ExposureEv),
            ["contrast"] = nameof(EditRecipe.Contrast),
            ["saturation"] = nameof(EditRecipe.Saturation),
            ["temperature"] = nameof(EditRecipe.Temperature),
            ["tint"] = nameof(EditRecipe.Tint),
            ["rotationDegrees"] = nameof(EditRecipe.RotationDegrees),
            ["highlights"] = nameof(EditRecipe.Highlights),
            ["shadows"] = nameof(EditRecipe.Shadows),
            ["whites"] = nameof(EditRecipe.Whites),
            ["blacks"] = nameof(EditRecipe.Blacks),
            ["vibrance"] = nameof(EditRecipe.Vibrance),
            ["vignette"] = nameof(EditRecipe.Vignette),
            ["texture"] = nameof(EditRecipe.Texture),
            ["clarity"] = nameof(EditRecipe.Clarity),
            ["dehaze"] = nameof(EditRecipe.Dehaze),
            ["sharpening"] = nameof(EditRecipe.Sharpening),
            ["noiseReduction"] = nameof(EditRecipe.NoiseReduction),
            ["grain"] = nameof(EditRecipe.Grain),
            ["colorMixer"] = nameof(EditRecipe.ColorMixer)
        };

        var controlled = edit.EnumerateObject()
            .Where(property => mappings.ContainsKey(property.Name))
            .Select(property => mappings[property.Name])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (edit.TryGetProperty("toneCurve", out var toneCurve) && toneCurve.ValueKind == JsonValueKind.Object)
        {
            var curveMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["highlights"] = DevelopSuggestion.ToneCurveHighlightsParameter,
                ["lights"] = DevelopSuggestion.ToneCurveLightsParameter,
                ["darks"] = DevelopSuggestion.ToneCurveDarksParameter,
                ["shadows"] = DevelopSuggestion.ToneCurveShadowsParameter
            };
            controlled.AddRange(toneCurve.EnumerateObject()
                .Where(property => curveMappings.ContainsKey(property.Name))
                .Select(property => curveMappings[property.Name]));
        }

        return controlled.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static HslColorMixer ReadColorMixer(JsonElement edit)
    {
        if (!edit.TryGetProperty("colorMixer", out var mixer) || mixer.ValueKind != JsonValueKind.Object)
        {
            return HslColorMixer.Neutral;
        }

        return new HslColorMixer(
            ReadMixerChannel(mixer, "red"),
            ReadMixerChannel(mixer, "orange"),
            ReadMixerChannel(mixer, "yellow"),
            ReadMixerChannel(mixer, "green"),
            ReadMixerChannel(mixer, "aqua"),
            ReadMixerChannel(mixer, "blue"),
            ReadMixerChannel(mixer, "purple"),
            ReadMixerChannel(mixer, "magenta")).Normalize();
    }

    private static ParametricToneCurve ReadToneCurve(JsonElement edit)
    {
        if (!edit.TryGetProperty("toneCurve", out var curve) || curve.ValueKind != JsonValueKind.Object)
        {
            return ParametricToneCurve.Identity;
        }

        return new ParametricToneCurve(
            Highlights: GetBoundedDouble(curve, "highlights", -100, 100),
            Lights: GetBoundedDouble(curve, "lights", -100, 100),
            Darks: GetBoundedDouble(curve, "darks", -100, 100),
            Shadows: GetBoundedDouble(curve, "shadows", -100, 100)).Normalize();
    }

    private static HslChannelAdjustment ReadMixerChannel(JsonElement mixer, string name)
    {
        if (!mixer.TryGetProperty(name, out var channel) || channel.ValueKind != JsonValueKind.Object)
        {
            return new HslChannelAdjustment();
        }

        return new HslChannelAdjustment(
            GetBoundedDouble(channel, "hue", -100, 100),
            GetBoundedDouble(channel, "saturation", -100, 100),
            GetBoundedDouble(channel, "luminance", -100, 100));
    }
}
