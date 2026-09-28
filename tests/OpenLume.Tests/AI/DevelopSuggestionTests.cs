using OpenLume.Core.Domain;

namespace OpenLume.Tests.AI;

public sealed class DevelopSuggestionTests
{
    [Fact]
    public void MergeOntoChangesOnlySupportedDevelopParametersAndNormalizesThem()
    {
        var current = new EditRecipe(
            Version: EditRecipe.CurrentVersion,
            ExposureEv: -1,
            Contrast: 4,
            RotationDegrees: 3,
            Vignette: -5);
        var suggestion = new DevelopSuggestion(
            Guid.NewGuid(),
            "Brighten conservatively",
            2,
            new EditRecipe(ExposureEv: 20, Contrast: 18, Highlights: -30, RotationDegrees: 60),
            [],
            [],
            DevelopSuggestionStatus.Pending,
            DateTimeOffset.UtcNow);

        var merged = suggestion.MergeOnto(current);

        Assert.Equal(EditRecipe.CurrentVersion, merged.Version);
        Assert.Equal(5, merged.ExposureEv);
        Assert.Equal(18, merged.Contrast);
        Assert.Equal(-30, merged.Highlights);
        Assert.Equal(45, merged.RotationDegrees);
    }

    [Fact]
    public void NormalizeBoundsUntrustedModelMetadata()
    {
        var suggestion = new DevelopSuggestion(
            Guid.Empty,
            new string('x', 500),
            -4,
            EditRecipe.Default,
            Enumerable.Range(0, 30).Select(index => new EditParameterDecision($"P{index}", new string('r', 400))).ToArray(),
            Enumerable.Range(0, 20).Select(index => $"Warning {index}").ToArray(),
            DevelopSuggestionStatus.Pending,
            default).Normalize();

        Assert.NotEqual(Guid.Empty, suggestion.Id);
        Assert.Equal(240, suggestion.Intent.Length);
        Assert.Equal(0, suggestion.Confidence);
        Assert.Equal(16, suggestion.Decisions.Count);
        Assert.All(suggestion.Decisions, decision => Assert.True(decision.Reason.Length <= 240));
        Assert.Equal(8, suggestion.Warnings.Count);
        Assert.NotEqual(default, suggestion.GeneratedAt);
    }

    [Fact]
    public void PartialProposalPreservesParametersTheModelDidNotControl()
    {
        var current = new EditRecipe(ExposureEv: -1, Contrast: 30, Temperature: 18, Vignette: -25);
        var suggestion = new DevelopSuggestion(
            Guid.NewGuid(), "Lift exposure only", .7, new EditRecipe(ExposureEv: .8), [], [],
            DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow,
            [nameof(EditRecipe.ExposureEv)]);

        var merged = suggestion.MergeOnto(current);

        Assert.Equal(.8, merged.ExposureEv);
        Assert.Equal(30, merged.Contrast);
        Assert.Equal(18, merged.Temperature);
        Assert.Equal(-25, merged.Vignette);
    }
}
