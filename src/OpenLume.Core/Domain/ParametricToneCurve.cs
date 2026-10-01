namespace OpenLume.Core.Domain;

public sealed record ParametricToneCurve(
    double Highlights = 0,
    double Lights = 0,
    double Darks = 0,
    double Shadows = 0,
    double ShadowSplit = 25,
    double MidtoneSplit = 50,
    double HighlightSplit = 75)
{
    private const double MinimumSplitGap = 5;

    public static ParametricToneCurve Identity { get; } = new();

    public bool IsIdentity =>
        Math.Abs(Highlights) <= .001 &&
        Math.Abs(Lights) <= .001 &&
        Math.Abs(Darks) <= .001 &&
        Math.Abs(Shadows) <= .001;

    public ParametricToneCurve Normalize()
    {
        var shadowSplit = ClampFinite(ShadowSplit, 5, 85, 25);
        var midtoneSplit = ClampFinite(MidtoneSplit, shadowSplit + MinimumSplitGap, 90, 50);
        var highlightSplit = ClampFinite(HighlightSplit, midtoneSplit + MinimumSplitGap, 95, 75);
        return this with
        {
            Highlights = ClampFinite(Highlights, -100, 100, 0),
            Lights = ClampFinite(Lights, -100, 100, 0),
            Darks = ClampFinite(Darks, -100, 100, 0),
            Shadows = ClampFinite(Shadows, -100, 100, 0),
            ShadowSplit = shadowSplit,
            MidtoneSplit = midtoneSplit,
            HighlightSplit = highlightSplit
        };
    }

    public double Evaluate(double input)
    {
        var normalized = Normalize();
        if (normalized.IsIdentity)
        {
            return Math.Clamp(input, 0, 1);
        }

        var (inputs, outputs) = normalized.CreateAnchors();
        var slopes = CreateMonotoneSlopes(inputs, outputs);
        return EvaluateFromAnchors(Math.Clamp(input, 0, 1), inputs, outputs, slopes);
    }

    public double[] CreateLookupTable(int entries = 4_096)
    {
        if (entries < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(entries), "A tone-curve lookup table needs at least two entries.");
        }

        var normalized = Normalize();
        var table = new double[entries];
        if (normalized.IsIdentity)
        {
            for (var index = 0; index < entries; index++)
            {
                table[index] = index / (double)(entries - 1);
            }

            return table;
        }

        var (inputs, outputs) = normalized.CreateAnchors();
        var slopes = CreateMonotoneSlopes(inputs, outputs);
        for (var index = 0; index < entries; index++)
        {
            table[index] = EvaluateFromAnchors(index / (double)(entries - 1), inputs, outputs, slopes);
        }

        return table;
    }

    private (double[] Inputs, double[] Outputs) CreateAnchors()
    {
        var shadowBoundary = ShadowSplit / 100.0;
        var midtoneBoundary = MidtoneSplit / 100.0;
        var highlightBoundary = HighlightSplit / 100.0;
        var inputs = new[]
        {
            0.0,
            shadowBoundary / 2,
            (shadowBoundary + midtoneBoundary) / 2,
            (midtoneBoundary + highlightBoundary) / 2,
            (highlightBoundary + 1) / 2,
            1.0
        };
        var adjustments = new[] { 0.0, Shadows, Darks, Lights, Highlights, 0.0 };
        var outputs = new double[inputs.Length];
        outputs[0] = 0;
        const double minimumOutputGap = .0005;
        for (var index = 1; index < inputs.Length - 1; index++)
        {
            var available = 1 - ((inputs.Length - 1 - index) * minimumOutputGap);
            var envelope = Math.Sin(Math.PI * inputs[index]);
            var desired = inputs[index] + (adjustments[index] / 100.0 * .22 * envelope);
            outputs[index] = Math.Clamp(desired, outputs[index - 1] + minimumOutputGap, available);
        }

        outputs[^1] = 1;
        return (inputs, outputs);
    }

    private static double EvaluateFromAnchors(
        double input,
        double[] inputs,
        double[] outputs,
        double[] slopes)
    {
        if (input <= 0 || input >= 1)
        {
            return input;
        }

        var segment = 0;
        while (segment < inputs.Length - 2 && input > inputs[segment + 1])
        {
            segment++;
        }

        var width = inputs[segment + 1] - inputs[segment];
        var position = Math.Clamp((input - inputs[segment]) / width, 0, 1);
        var positionSquared = position * position;
        var positionCubed = positionSquared * position;
        var startBasis = (2 * positionCubed) - (3 * positionSquared) + 1;
        var startSlopeBasis = positionCubed - (2 * positionSquared) + position;
        var endBasis = (-2 * positionCubed) + (3 * positionSquared);
        var endSlopeBasis = positionCubed - positionSquared;
        return Math.Clamp(
            (startBasis * outputs[segment]) +
            (startSlopeBasis * width * slopes[segment]) +
            (endBasis * outputs[segment + 1]) +
            (endSlopeBasis * width * slopes[segment + 1]),
            0,
            1);
    }

    private static double[] CreateMonotoneSlopes(double[] inputs, double[] outputs)
    {
        var secants = new double[inputs.Length - 1];
        for (var index = 0; index < secants.Length; index++)
        {
            secants[index] = (outputs[index + 1] - outputs[index]) / (inputs[index + 1] - inputs[index]);
        }

        var slopes = new double[inputs.Length];
        slopes[0] = secants[0];
        slopes[^1] = secants[^1];
        for (var index = 1; index < slopes.Length - 1; index++)
        {
            if (secants[index - 1] <= 0 || secants[index] <= 0)
            {
                slopes[index] = 0;
                continue;
            }

            var previousWidth = inputs[index] - inputs[index - 1];
            var nextWidth = inputs[index + 1] - inputs[index];
            var previousWeight = (2 * nextWidth) + previousWidth;
            var nextWeight = nextWidth + (2 * previousWidth);
            slopes[index] = (previousWeight + nextWeight) /
                ((previousWeight / secants[index - 1]) + (nextWeight / secants[index]));
        }

        return slopes;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);
}
