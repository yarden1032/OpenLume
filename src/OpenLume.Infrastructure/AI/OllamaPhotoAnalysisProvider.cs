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
                    content = "Analyze this photograph for a nondestructive editor. Return strict JSON with summary (string), technicalScore and aestheticScore (0..1), suggestedPick (boolean), tags (up to 8 strings), and suggestedEdit containing exposureEv (-2..2), contrast (-50..50), saturation (-40..40), temperature (-30..30), tint (-30..30). Do not include markdown.",
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

        return new PhotoAnalysis(
            GetRequiredString(root, "summary", 600),
            GetBoundedDouble(root, "technicalScore", 0, 1),
            GetBoundedDouble(root, "aestheticScore", 0, 1),
            root.TryGetProperty("suggestedPick", out var pick) && pick.ValueKind == JsonValueKind.True,
            ReadTags(root),
            new EditRecipe(
                ExposureEv: GetBoundedDouble(edit, "exposureEv", -2, 2),
                Contrast: GetBoundedDouble(edit, "contrast", -50, 50),
                Saturation: GetBoundedDouble(edit, "saturation", -40, 40),
                Temperature: GetBoundedDouble(edit, "temperature", -30, 30),
                Tint: GetBoundedDouble(edit, "tint", -30, 30)).Normalize());
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
}
