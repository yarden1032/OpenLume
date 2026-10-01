using System.Net;
using System.Text;
using OpenLume.Core.Domain;
using OpenLume.Infrastructure.AI;

namespace OpenLume.Tests.AI;

public sealed class OllamaPhotoAnalysisProviderTests
{
    [Fact]
    public async Task DetectsConfiguredLocalModel()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
            Json(HttpStatusCode.OK, """{"models":[{"name":"vision:latest"}]}""")));
        using var provider = new OllamaPhotoAnalysisProvider(
            new OllamaOptions(new Uri("http://localhost:11434"), "vision", TimeSpan.FromSeconds(5)),
            httpClient);

        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task ValidatesAndBoundsAnalysisResponse()
    {
        const string modelResponse = """
            {"message":{"content":"{\"summary\":\"Strong portrait\",\"technicalScore\":2,\"aestheticScore\":0.8,\"suggestedPick\":true,\"tags\":[\"portrait\",\"portrait\"],\"intent\":\"Protect the face and soften the background\",\"editConfidence\":0.92,\"warnings\":[\"Check skin tones\"],\"decisions\":[{\"parameter\":\"Highlights\",\"reason\":\"Recover facial detail\"}],\"suggestedEdit\":{\"exposureEv\":9,\"contrast\":10,\"highlights\":-35,\"shadows\":22,\"whites\":8,\"blacks\":-7,\"saturation\":5,\"vibrance\":14,\"temperature\":2,\"tint\":-2,\"vignette\":-10,\"texture\":12,\"clarity\":8,\"dehaze\":5,\"sharpening\":35,\"noiseReduction\":20,\"grain\":6,\"rotationDegrees\":1.5,\"colorMixer\":{\"orange\":{\"hue\":-8,\"saturation\":12,\"luminance\":150},\"blue\":{\"saturation\":-18}},\"toneCurve\":{\"highlights\":18,\"lights\":9,\"darks\":-11,\"shadows\":-24}}}"}}
            """;
        using var httpClient = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, modelResponse)));
        using var provider = new OllamaPhotoAnalysisProvider(
            new OllamaOptions(new Uri("http://localhost:11434"), "vision", TimeSpan.FromSeconds(5)),
            httpClient);
        var photo = new PhotoAsset(
            Guid.NewGuid(), "photo.jpg", "photo.jpg", ".jpg", 10, DateTimeOffset.UtcNow,
            null, 100, 100, 0, PickState.Unflagged, EditRecipe.Default);

        var result = await provider.AnalyzeAsync(photo, [1, 2, 3]);

        Assert.Equal("Strong portrait", result.Summary);
        Assert.Equal(1, result.TechnicalScore);
        Assert.Equal(2, result.SuggestedEdit.ExposureEv);
        Assert.Single(result.Tags);
        Assert.True(result.SuggestedPick);
        Assert.NotNull(result.DevelopSuggestion);
        Assert.Equal("Protect the face and soften the background", result.DevelopSuggestion!.Intent);
        Assert.Equal(0.92, result.DevelopSuggestion.Confidence);
        Assert.Equal(-35, result.DevelopSuggestion.Recipe.Highlights);
        Assert.Equal(22, result.DevelopSuggestion.Recipe.Shadows);
        Assert.Equal(1.5, result.DevelopSuggestion.Recipe.RotationDegrees);
        Assert.Equal(12, result.DevelopSuggestion.Recipe.Texture);
        Assert.Equal(35, result.DevelopSuggestion.Recipe.Sharpening);
        Assert.Equal(20, result.DevelopSuggestion.Recipe.NoiseReduction);
        Assert.Equal(-8, result.DevelopSuggestion.Recipe.ColorMixer!.Orange!.Hue);
        Assert.Equal(100, result.DevelopSuggestion.Recipe.ColorMixer.Orange.Luminance);
        Assert.Equal(-18, result.DevelopSuggestion.Recipe.ColorMixer.Blue!.Saturation);
        Assert.Contains(nameof(EditRecipe.ColorMixer), result.DevelopSuggestion.ControlledParameters!);
        Assert.Equal(18, result.DevelopSuggestion.Recipe.ToneCurve!.Highlights);
        Assert.Equal(-24, result.DevelopSuggestion.Recipe.ToneCurve.Shadows);
        Assert.Contains(DevelopSuggestion.ToneCurveHighlightsParameter, result.DevelopSuggestion.ControlledParameters!);
        Assert.Contains(DevelopSuggestion.ToneCurveShadowsParameter, result.DevelopSuggestion.ControlledParameters!);
        Assert.Contains(nameof(EditRecipe.Grain), result.DevelopSuggestion.ControlledParameters!);
        Assert.Single(result.DevelopSuggestion.Decisions);
        Assert.Single(result.DevelopSuggestion.Warnings);
    }

    [Fact]
    public async Task RejectsMalformedModelJsonWithoutInventingAnEdit()
    {
        const string modelResponse = """{"message":{"content":"not-json"}}""";
        using var httpClient = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, modelResponse)));
        using var provider = new OllamaPhotoAnalysisProvider(
            new OllamaOptions(new Uri("http://localhost:11434"), "vision", TimeSpan.FromSeconds(5)),
            httpClient);
        var photo = new PhotoAsset(
            Guid.NewGuid(), "photo.jpg", "photo.jpg", ".jpg", 10, DateTimeOffset.UtcNow,
            null, 100, 100, 0, PickState.Unflagged, EditRecipe.Default);

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => provider.AnalyzeAsync(photo, [1, 2, 3]));
    }

    [Fact]
    public async Task HonorsCancellationBeforeSendingPhotoData()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("The cancelled request must not reach the response factory.")));
        using var provider = new OllamaPhotoAnalysisProvider(
            new OllamaOptions(new Uri("http://localhost:11434"), "vision", TimeSpan.FromSeconds(5)),
            httpClient);
        var photo = new PhotoAsset(
            Guid.NewGuid(), "photo.jpg", "photo.jpg", ".jpg", 10, DateTimeOffset.UtcNow,
            null, 100, 100, 0, PickState.Unflagged, EditRecipe.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.AnalyzeAsync(photo, [1, 2, 3], cancellation.Token));
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                : Task.FromResult(responseFactory(request));
    }
}
