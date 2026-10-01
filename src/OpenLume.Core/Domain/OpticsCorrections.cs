namespace OpenLume.Core.Domain;

public sealed record OpticsCorrections(
    double Distortion = 0,
    double ChromaticAberration = 0,
    double LensVignette = 0,
    double VignetteMidpoint = 50)
{
    public static OpticsCorrections Neutral { get; } = new();

    public bool IsNeutral =>
        Math.Abs(Distortion) < .000001 &&
        Math.Abs(ChromaticAberration) < .000001 &&
        Math.Abs(LensVignette) < .000001;

    public OpticsCorrections Normalize() => this with
    {
        Distortion = ClampFinite(Distortion, -100, 100, 0),
        ChromaticAberration = ClampFinite(ChromaticAberration, 0, 100, 0),
        LensVignette = ClampFinite(LensVignette, -100, 100, 0),
        VignetteMidpoint = ClampFinite(VignetteMidpoint, 0, 100, 50)
    };

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
