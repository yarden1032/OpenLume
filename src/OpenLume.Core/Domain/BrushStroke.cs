namespace OpenLume.Core.Domain;

public readonly record struct MaskPoint(double X, double Y)
{
    public MaskPoint Normalize() => new(double.IsFinite(X) ? Math.Clamp(X, 0, 1) : .5,
        double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : .5);
}

/// <summary>Flow is the opacity of one continuous pass; overlapping passes accumulate.</summary>
public sealed record BrushStroke(ImmutableValues<MaskPoint> Points, double RadiusX = .03, double RadiusY = .03,
    double Feather = .6, double Flow = .5, bool Erase = false)
{
    public BrushStroke Normalize() => this with
    {
        Points = new ImmutableValues<MaskPoint>((Points ?? new ImmutableValues<MaskPoint>([])).Take(2048).Select(point => point.Normalize())),
        RadiusX = double.IsFinite(RadiusX) ? Math.Clamp(RadiusX, .001, 1) : .03,
        RadiusY = double.IsFinite(RadiusY) ? Math.Clamp(RadiusY, .001, 1) : .03,
        Feather = double.IsFinite(Feather) ? Math.Clamp(Feather, 0, 1) : .6,
        Flow = double.IsFinite(Flow) ? Math.Clamp(Flow, 0, 1) : .5
    };

    public double Weight(double x, double y)
    {
        var weight = 0d;
        for (var index = 0; index < Points.Count; index++)
        {
            var a = Points[Math.Max(0, index - 1)];
            var b = Points[index];
            var dx = (b.X - a.X) / RadiusX;
            var dy = (b.Y - a.Y) / RadiusY;
            var px = (x - a.X) / RadiusX;
            var py = (y - a.Y) / RadiusY;
            var length = dx * dx + dy * dy;
            var t = length <= .000000001 ? 0 : Math.Clamp((px * dx + py * dy) / length, 0, 1);
            var distance = Math.Sqrt(Math.Pow(px - t * dx, 2) + Math.Pow(py - t * dy, 2));
            var alpha = Feather <= .000001 ? (distance <= 1 ? 1 : 0) : Math.Clamp((1 - distance) / Feather, 0, 1);
            weight = Math.Max(weight, alpha * alpha * (3 - 2 * alpha));
        }
        return weight * Flow;
    }
}
