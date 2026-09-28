using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenLume.Imaging;

namespace OpenLume.App.Controls;

public sealed class HistogramControl : Control
{
    public static readonly StyledProperty<ImageHistogram?> HistogramProperty =
        AvaloniaProperty.Register<HistogramControl, ImageHistogram?>(nameof(Histogram));

    static HistogramControl() => AffectsRender<HistogramControl>(HistogramProperty);

    public ImageHistogram? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var histogram = Histogram;
        if (histogram is null || Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        DrawChannel(context, histogram.Luminance, histogram.Peak, Color.FromArgb(64, 220, 226, 235));
        DrawChannel(context, histogram.Red, histogram.Peak, Color.FromArgb(88, 255, 91, 101));
        DrawChannel(context, histogram.Green, histogram.Peak, Color.FromArgb(78, 77, 214, 142));
        DrawChannel(context, histogram.Blue, histogram.Peak, Color.FromArgb(88, 84, 143, 255));
    }

    private void DrawChannel(DrawingContext context, IReadOnlyList<int> values, int peak, Color color)
    {
        if (values.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(0, Bounds.Height), true);
            for (var index = 0; index < values.Count; index++)
            {
                var x = index * Bounds.Width / Math.Max(1, values.Count - 1);
                var normalized = Math.Clamp(values[index] / (double)peak, 0, 1);
                path.LineTo(new Point(x, Bounds.Height - (normalized * Bounds.Height)));
            }

            path.LineTo(new Point(Bounds.Width, Bounds.Height));
            path.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }
}
