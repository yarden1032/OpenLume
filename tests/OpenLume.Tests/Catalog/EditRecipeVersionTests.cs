using System.Text.Json;
using OpenLume.Core.Domain;

namespace OpenLume.Tests.Catalog;

public sealed class EditRecipeVersionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void VersionOneJsonUpgradesWithoutLosingExistingAdjustments()
    {
        const string json = """
            {"version":1,"exposureEv":1.25,"contrast":-12,"saturation":8,"temperature":4,"tint":-3,"rotationDegrees":2}
            """;

        var recipe = JsonSerializer.Deserialize<EditRecipe>(json, JsonOptions)!
            .Normalize();

        Assert.Equal(EditRecipe.CurrentVersion, recipe.Version);
        Assert.Equal(1.25, recipe.ExposureEv);
        Assert.Equal(-12, recipe.Contrast);
        Assert.Equal(8, recipe.Saturation);
        Assert.Equal(4, recipe.Temperature);
        Assert.Equal(-3, recipe.Tint);
        Assert.Equal(2, recipe.RotationDegrees);
        Assert.Equal(0, recipe.Highlights);
        Assert.Equal(0, recipe.Vignette);
    }

    [Fact]
    public void NewGlobalControlsAreBounded()
    {
        var normalized = new EditRecipe(
            Highlights: 101,
            Shadows: -101,
            Whites: 500,
            Blacks: -500,
            Vibrance: 120,
            Vignette: -120).Normalize();

        Assert.Equal(100, normalized.Highlights);
        Assert.Equal(-100, normalized.Shadows);
        Assert.Equal(100, normalized.Whites);
        Assert.Equal(-100, normalized.Blacks);
        Assert.Equal(100, normalized.Vibrance);
        Assert.Equal(-100, normalized.Vignette);
    }
}
