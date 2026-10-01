using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenLume.Core.Domain;

namespace OpenLume.App.Controls;

public sealed class ToneCurveControl : Control
{
    public static readonly StyledProperty<ParametricToneCurve?> CurveProperty =
        AvaloniaProperty.Register<ToneCurveControl, ParametricToneCurve?>(nameof(Curve));

    static ToneCurveControl() => AffectsRender<ToneCurveControl>(CurveProperty);

    public ParametricToneCurve? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#0D1015")), null, bounds, 5, 5);
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#28303A")), 1);
        for (var index = 1; index < 4; index++)
        {
            var x = Bounds.Width * index / 4;
            var y = Bounds.Height * index / 4;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawLine(gridPen, new Point(0, y), new Point(Bounds.Width, y));
        }

        var curve = (Curve ?? ParametricToneCurve.Identity).Normalize();
        var splitPen = new Pen(new SolidColorBrush(Color.Parse("#3A4656")), 1, dashStyle: DashStyle.Dash);
        foreach (var split in new[] { curve.ShadowSplit, curve.MidtoneSplit, curve.HighlightSplit })
        {
            var x = Bounds.Width * split / 100;
            context.DrawLine(splitPen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        var geometry = new StreamGeometry();
        var curvePoints = curve.CreateLookupTable(129);
        using (var path = geometry.Open())
        {
            for (var index = 0; index <= 128; index++)
            {
                var input = index / 128.0;
                var point = new Point(input * Bounds.Width, (1 - curvePoints[index]) * Bounds.Height);
                if (index == 0)
                {
                    path.BeginFigure(point, false);
                }
                else
                {
                    path.LineTo(point);
                }
            }
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#DDE6F4")), 2), geometry);
    }
}
