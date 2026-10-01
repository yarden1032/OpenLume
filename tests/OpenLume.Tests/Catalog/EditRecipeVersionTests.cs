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

    [Fact]
    public void VersionTwoJsonAddsNeutralPresenceAndDetailDefaults()
    {
        const string json = """{"version":2,"exposureEv":0.5,"vignette":-12}""";

        var recipe = JsonSerializer.Deserialize<EditRecipe>(json, JsonOptions)!.Normalize();

        Assert.Equal(EditRecipe.CurrentVersion, recipe.Version);
        Assert.Equal(.5, recipe.ExposureEv);
        Assert.Equal(-12, recipe.Vignette);
        Assert.Equal(0, recipe.Texture);
        Assert.Equal(0, recipe.Clarity);
        Assert.Equal(0, recipe.Dehaze);
        Assert.Equal(0, recipe.Sharpening);
        Assert.Equal(0, recipe.NoiseReduction);
        Assert.Equal(0, recipe.Grain);
    }

    [Fact]
    public void PresenceAndDetailControlsAreBounded()
    {
        var normalized = new EditRecipe(
            Texture: -200,
            Clarity: 200,
            Dehaze: 250,
            Sharpening: -20,
            NoiseReduction: 140,
            Grain: 500).Normalize();

        Assert.Equal(-100, normalized.Texture);
        Assert.Equal(100, normalized.Clarity);
        Assert.Equal(100, normalized.Dehaze);
        Assert.Equal(0, normalized.Sharpening);
        Assert.Equal(100, normalized.NoiseReduction);
        Assert.Equal(100, normalized.Grain);
    }

    [Fact]
    public void VersionThreeJsonAddsANeutralColorMixer()
    {
        const string json = """{"version":3,"exposureEv":0.25,"clarity":12}""";

        var recipe = JsonSerializer.Deserialize<EditRecipe>(json, JsonOptions)!.Normalize();

        Assert.Equal(EditRecipe.CurrentVersion, recipe.Version);
        Assert.NotNull(recipe.ColorMixer);
        Assert.Equal(0, recipe.ColorMixer!.Red!.Hue);
        Assert.Equal(0, recipe.ColorMixer.Blue!.Saturation);
        Assert.Equal(0, recipe.ColorMixer.Magenta!.Luminance);
    }

    [Fact]
    public void ColorMixerChannelsAreBounded()
    {
        var recipe = new EditRecipe(ColorMixer: new HslColorMixer(
            Red: new HslChannelAdjustment(-500, 150, 101),
            Blue: new HslChannelAdjustment(500, -140, -101))).Normalize();

        Assert.Equal(-100, recipe.ColorMixer!.Red!.Hue);
        Assert.Equal(100, recipe.ColorMixer.Red.Saturation);
        Assert.Equal(100, recipe.ColorMixer.Red.Luminance);
        Assert.Equal(100, recipe.ColorMixer.Blue!.Hue);
        Assert.Equal(-100, recipe.ColorMixer.Blue.Saturation);
        Assert.Equal(-100, recipe.ColorMixer.Blue.Luminance);
    }

    [Fact]
    public void VersionFourJsonAddsAnIdentityToneCurve()
    {
        const string json = """{"version":4,"exposureEv":0.4,"colorMixer":{}}""";

        var recipe = JsonSerializer.Deserialize<EditRecipe>(json, JsonOptions)!.Normalize();

        Assert.Equal(EditRecipe.CurrentVersion, recipe.Version);
        Assert.NotNull(recipe.ToneCurve);
        Assert.True(recipe.ToneCurve!.IsIdentity);
        Assert.Equal(25, recipe.ToneCurve.ShadowSplit);
        Assert.Equal(50, recipe.ToneCurve.MidtoneSplit);
        Assert.Equal(75, recipe.ToneCurve.HighlightSplit);
    }

    [Fact]
    public void ToneCurveValuesAndSplitsAreNormalized()
    {
        var curve = new EditRecipe(ToneCurve: new ParametricToneCurve(
            Highlights: 500,
            Lights: -500,
            Darks: 120,
            Shadows: -140,
            ShadowSplit: 90,
            MidtoneSplit: 10,
            HighlightSplit: 20)).Normalize().ToneCurve!;

        Assert.Equal(100, curve.Highlights);
        Assert.Equal(-100, curve.Lights);
        Assert.Equal(100, curve.Darks);
        Assert.Equal(-100, curve.Shadows);
        Assert.True(curve.ShadowSplit + 5 <= curve.MidtoneSplit);
        Assert.True(curve.MidtoneSplit + 5 <= curve.HighlightSplit);

        var nonFinite = new ParametricToneCurve(
            Highlights: double.NaN,
            Lights: double.PositiveInfinity,
            ShadowSplit: double.NaN).Normalize();
        Assert.Equal(0, nonFinite.Highlights);
        Assert.Equal(0, nonFinite.Lights);
        Assert.Equal(25, nonFinite.ShadowSplit);
    }
}
