namespace OpenLume.Core.Domain;

public enum LocalMaskKind { Radial, Linear }

/// <summary>Geometry is normalized to the full developed source, before straighten/crop/orientation.</summary>
public sealed record LocalMask(
    Guid Id, string Name = "Mask", LocalMaskKind Kind = LocalMaskKind.Radial,
    double CenterX = .5, double CenterY = .5, double RadiusX = .25, double RadiusY = .25,
    double AngleDegrees = 90, double Feather = .5, bool Inverted = false, bool Enabled = true,
    double Density = 1, double ExposureEv = 0, double Contrast = 0, double Saturation = 0,
    double Temperature = 0, double Tint = 0)
{
    private static double Bound(double value, double min, double max, double fallback = 0) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public LocalMask Normalize() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Mask" : Name.Trim()[..Math.Min(Name.Trim().Length, 64)],
        Kind = Enum.IsDefined(Kind) ? Kind : LocalMaskKind.Radial,
        CenterX = Bound(CenterX, 0, 1, .5),
        CenterY = Bound(CenterY, 0, 1, .5),
        RadiusX = Bound(RadiusX, .01, 1, .25),
        RadiusY = Bound(RadiusY, .01, 1, .25),
        AngleDegrees = Bound(AngleDegrees, -180, 180, 90),
        Feather = Bound(Feather, 0, 1, .5),
        Density = Bound(Density, 0, 1, 1),
        ExposureEv = Bound(ExposureEv, -3, 3),
        Contrast = Bound(Contrast, -100, 100),
        Saturation = Bound(Saturation, -100, 100),
        Temperature = Bound(Temperature, -100, 100),
        Tint = Bound(Tint, -100, 100)
    };

    public bool HasAdjustments => Enabled && Density > 0 &&
        (ExposureEv != 0 || Contrast != 0 || Saturation != 0 || Temperature != 0 || Tint != 0);

    /// <summary>Call on normalized masks. Linear gradients use RadiusX as half their transition width.</summary>
    public double Weight(double x, double y) => Kind == LocalMaskKind.Radial
        ? Weight(x, y, 0, 0)
        : Weight(x, y, Math.Cos(AngleDegrees * Math.PI / 180), Math.Sin(AngleDegrees * Math.PI / 180));

    /// <summary>Cached linear axis variant for pixel pipelines.</summary>
    public double Weight(double x, double y, double axisX, double axisY)
    {
        if (!Enabled) return 0;
        double weight;
        if (Kind == LocalMaskKind.Radial)
        {
            var dx = (x - CenterX) / RadiusX;
            var dy = (y - CenterY) / RadiusY;
            var radius = Math.Sqrt(dx * dx + dy * dy);
            weight = Feather <= .000001 ? (radius <= 1 ? 1 : 0) : Math.Clamp((1 - radius) / Feather, 0, 1);
        }
        else
        {
            var distance = (x - CenterX) * axisX + (y - CenterY) * axisY;
            weight = Math.Clamp(.5 - distance / (2 * RadiusX), 0, 1);
        }
        weight = weight * weight * (3 - 2 * weight);
        return (Inverted ? 1 - weight : weight) * Density;
    }
}
