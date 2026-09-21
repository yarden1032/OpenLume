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
            {"message":{"content":"{\"summary\":\"Strong portrait\",\"technicalScore\":2,\"aestheticScore\":0.8,\"suggestedPick\":true,\"tags\":[\"portrait\",\"portrait\"],\"suggestedEdit\":{\"exposureEv\":9,\"contrast\":10,\"saturation\":5,\"temperature\":2,\"tint\":-2}}"}}
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
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
