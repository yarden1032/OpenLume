using OpenLume.Core.Domain;

namespace OpenLume.Tests.Imaging;

public sealed class ParametricToneCurveTests
{
    [Fact]
    public void IdentityCurvePreservesEveryInput()
    {
        for (var index = 0; index <= 1_000; index++)
        {
            var input = index / 1_000.0;
            Assert.Equal(input, ParametricToneCurve.Identity.Evaluate(input), 12);
        }
    }

    [Fact]
    public void ExtremeCurveRemainsMonotonicAndLocksEndpoints()
    {
        var curve = new ParametricToneCurve(
            Highlights: -100,
            Lights: 100,
            Darks: -100,
            Shadows: 100,
            ShadowSplit: 85,
            MidtoneSplit: 5,
            HighlightSplit: 10).Normalize();
        var table = curve.CreateLookupTable(8_192);

        Assert.Equal(0, table[0]);
        Assert.Equal(1, table[^1]);
        for (var index = 1; index < table.Length; index++)
        {
            Assert.True(table[index] >= table[index - 1] - 1e-10,
                $"Curve inverted between entries {index - 1} and {index}.");
        }

        Assert.True(curve.ShadowSplit + 5 <= curve.MidtoneSplit);
        Assert.True(curve.MidtoneSplit + 5 <= curve.HighlightSplit);
    }

    [Fact]
    public void HighlightAdjustmentPrimarilyTargetsBrightTones()
    {
        var curve = new ParametricToneCurve(Highlights: 80);

        var shadowChange = Math.Abs(curve.Evaluate(.12) - .12);
        var highlightChange = curve.Evaluate(.88) - .88;

        Assert.True(highlightChange > .04, $"Expected lifted highlights, observed {highlightChange}.");
        Assert.True(shadowChange < .015, $"Expected stable shadows, observed {shadowChange}.");
    }
}
