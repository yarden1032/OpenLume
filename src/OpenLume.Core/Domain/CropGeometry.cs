namespace OpenLume.Core.Domain;

public sealed record CropGeometry(
    double X = 0,
    double Y = 0,
    double Width = 1,
    double Height = 1,
    int QuarterTurns = 0,
    bool FlipHorizontal = false,
    bool FlipVertical = false)
{
    public static CropGeometry FullFrame { get; } = new();

    public bool HasCrop => X > .000001 || Y > .000001 || Width < .999999 || Height < .999999;
    public bool HasOrientation => QuarterTurns != 0 || FlipHorizontal || FlipVertical;

    public CropGeometry Normalize()
    {
        var width = ClampFinite(Width, .01, 1, 1);
        var height = ClampFinite(Height, .01, 1, 1);
        return this with
        {
            X = ClampFinite(X, 0, 1 - width, 0),
            Y = ClampFinite(Y, 0, 1 - height, 0),
            Width = width,
            Height = height,
            QuarterTurns = ((QuarterTurns % 4) + 4) % 4
        };
    }

    public CropGeometry WithoutCrop() => Normalize() with { X = 0, Y = 0, Width = 1, Height = 1 };

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);
}
