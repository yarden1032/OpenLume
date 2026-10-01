using OpenLume.Core.Domain;

namespace OpenLume.Imaging;

/// <summary>Rasterizes source-anchored strokes into caller-owned tiles, never full-photo mask buffers.</summary>
public sealed class BrushRasterizer
{
    private readonly Stroke[] _strokes;
    private readonly int _width, _height;

    public BrushRasterizer(LocalMask mask, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
        _strokes = mask.Normalize().BrushStrokes!.Select(stroke => new Stroke(stroke, width, height)).ToArray();
    }

    public void FillTile(int x, int y, int width, int height, float[] destination, float[] scratch,
        CancellationToken cancellationToken = default)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > _width || (long)y + height > _height)
            throw new ArgumentOutOfRangeException(nameof(width));
        var count = checked(width * height);
        if (destination.Length < count || scratch.Length < count || ReferenceEquals(destination, scratch))
            throw new ArgumentException("Two distinct tile buffers of sufficient size are required.");
        Array.Clear(destination, 0, count);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var stroke in _strokes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stroke.Right <= x || stroke.Bottom <= y || stroke.Left >= x + width || stroke.Top >= y + height) continue;
            Array.Clear(scratch, 0, count);
            var touched = false;
            foreach (var segment in stroke.Segments)
            {
                var left = Math.Max(x, segment.Left);
                var top = Math.Max(y, segment.Top);
                var right = Math.Min(x + width, segment.Right);
                var bottom = Math.Min(y + height, segment.Bottom);
                if (right <= left || bottom <= top) continue;
                touched = true;
                for (var row = top; row < bottom; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var py = (row + .5 - segment.Y) * stroke.InverseRadiusY;
                    for (var column = left; column < right; column++)
                    {
                        var px = (column + .5 - segment.X) * stroke.InverseRadiusX;
                        var t = segment.LengthSquared <= 1e-9 ? 0 :
                            Math.Clamp((px * segment.Dx + py * segment.Dy) / segment.LengthSquared, 0, 1);
                        var dx = px - t * segment.Dx;
                        var dy = py - t * segment.Dy;
                        var distance = Math.Sqrt(dx * dx + dy * dy);
                        var alpha = stroke.Feather <= 1e-6 ? (distance <= 1 ? 1 : 0) :
                            Math.Clamp((1 - distance) / stroke.Feather, 0, 1);
                        var index = (row - y) * width + column - x;
                        scratch[index] = Math.Max(scratch[index], (float)(alpha * alpha * (3 - 2 * alpha)));
                    }
                }
            }
            if (!touched) continue;
            for (var index = 0; index < count; index++)
            {
                var alpha = scratch[index] * stroke.Flow;
                destination[index] = stroke.Erase ? destination[index] * (1 - alpha) :
                    destination[index] + (1 - destination[index]) * alpha;
            }
        }
    }

    private sealed class Stroke
    {
        public readonly Segment[] Segments;
        public readonly double InverseRadiusX, InverseRadiusY, Feather;
        public readonly float Flow;
        public readonly bool Erase;
        public readonly int Left, Top, Right, Bottom;
        public Stroke(BrushStroke stroke, int width, int height)
        {
            var rx = stroke.RadiusX * width;
            var ry = stroke.RadiusY * height;
            InverseRadiusX = 1 / rx;
            InverseRadiusY = 1 / ry;
            Feather = stroke.Feather;
            Flow = (float)stroke.Flow;
            Erase = stroke.Erase;
            Segments = new Segment[stroke.Points.Count];
            for (var index = 0; index < Segments.Length; index++)
            {
                var a = stroke.Points[Math.Max(0, index - 1)];
                var b = stroke.Points[index];
                var dx = (b.X - a.X) / stroke.RadiusX;
                var dy = (b.Y - a.Y) / stroke.RadiusY;
                Segments[index] = new(a.X * width, a.Y * height, dx, dy, dx * dx + dy * dy,
                    Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) * width - rx)),
                    Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) * height - ry)),
                    Math.Min(width, (int)Math.Ceiling(Math.Max(a.X, b.X) * width + rx)),
                    Math.Min(height, (int)Math.Ceiling(Math.Max(a.Y, b.Y) * height + ry)));
            }
            Left = Segments.Length == 0 ? width : Segments.Min(segment => segment.Left);
            Top = Segments.Length == 0 ? height : Segments.Min(segment => segment.Top);
            Right = Segments.Length == 0 ? 0 : Segments.Max(segment => segment.Right);
            Bottom = Segments.Length == 0 ? 0 : Segments.Max(segment => segment.Bottom);
        }
    }

    private readonly record struct Segment(double X, double Y, double Dx, double Dy, double LengthSquared,
        int Left, int Top, int Right, int Bottom);
}
