using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenLume.App.Controls;

public sealed class CropOverlayControl : Control
{
    public static readonly StyledProperty<double> CropXProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(CropX), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> CropYProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(CropY), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> CropWidthProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(CropWidth), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> CropHeightProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(CropHeight), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ImageAspectRatioProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(ImageAspectRatio), 1);
    public static readonly StyledProperty<double> LockedNormalizedAspectRatioProperty =
        AvaloniaProperty.Register<CropOverlayControl, double>(nameof(LockedNormalizedAspectRatio), 0);

    private DragOperation _operation;
    private Point _dragStart;
    private Rect _startCrop;

    static CropOverlayControl() => AffectsRender<CropOverlayControl>(
        CropXProperty,
        CropYProperty,
        CropWidthProperty,
        CropHeightProperty,
        ImageAspectRatioProperty,
        LockedNormalizedAspectRatioProperty);

    public double CropX { get => GetValue(CropXProperty); set => SetValue(CropXProperty, value); }
    public double CropY { get => GetValue(CropYProperty); set => SetValue(CropYProperty, value); }
    public double CropWidth { get => GetValue(CropWidthProperty); set => SetValue(CropWidthProperty, value); }
    public double CropHeight { get => GetValue(CropHeightProperty); set => SetValue(CropHeightProperty, value); }
    public double ImageAspectRatio { get => GetValue(ImageAspectRatioProperty); set => SetValue(ImageAspectRatioProperty, value); }
    public double LockedNormalizedAspectRatio { get => GetValue(LockedNormalizedAspectRatioProperty); set => SetValue(LockedNormalizedAspectRatioProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var image = GetImageRect();
        if (image.Width <= 1 || image.Height <= 1)
        {
            return;
        }

        var crop = GetCropRect(image);
        var shade = new SolidColorBrush(Color.FromArgb(175, 0, 0, 0));
        context.DrawRectangle(shade, null, new Rect(image.X, image.Y, image.Width, crop.Y - image.Y));
        context.DrawRectangle(shade, null, new Rect(image.X, crop.Bottom, image.Width, image.Bottom - crop.Bottom));
        context.DrawRectangle(shade, null, new Rect(image.X, crop.Y, crop.X - image.X, crop.Height));
        context.DrawRectangle(shade, null, new Rect(crop.Right, crop.Y, image.Right - crop.Right, crop.Height));

        var borderPen = new Pen(Brushes.White, 1.5);
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1);
        context.DrawRectangle(null, borderPen, crop);
        for (var index = 1; index < 3; index++)
        {
            var x = crop.X + (crop.Width * index / 3);
            var y = crop.Y + (crop.Height * index / 3);
            context.DrawLine(guidePen, new Point(x, crop.Y), new Point(x, crop.Bottom));
            context.DrawLine(guidePen, new Point(crop.X, y), new Point(crop.Right, y));
        }

        foreach (var point in GetCorners(crop))
        {
            context.DrawRectangle(Brushes.White, null, new Rect(point.X - 4, point.Y - 4, 8, 8), 2, 2);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var image = GetImageRect();
        var crop = GetCropRect(image);
        var point = e.GetPosition(this);
        _operation = HitTest(point, crop);
        if (_operation == DragOperation.None)
        {
            return;
        }

        _dragStart = ToNormalized(point, image);
        _startCrop = new Rect(CropX, CropY, CropWidth, CropHeight);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_operation == DragOperation.None || e.Pointer.Captured != this)
        {
            return;
        }

        var image = GetImageRect();
        var point = ToNormalized(e.GetPosition(this), image);
        if (_operation == DragOperation.Move)
        {
            var x = Math.Clamp(_startCrop.X + point.X - _dragStart.X, 0, 1 - _startCrop.Width);
            var y = Math.Clamp(_startCrop.Y + point.Y - _dragStart.Y, 0, 1 - _startCrop.Height);
            UpdateCrop(x, y, _startCrop.Width, _startCrop.Height);
        }
        else
        {
            ResizeFromCorner(point);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _operation = DragOperation.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ResizeFromCorner(Point point)
    {
        var left = _operation is DragOperation.TopLeft or DragOperation.BottomLeft;
        var top = _operation is DragOperation.TopLeft or DragOperation.TopRight;
        var anchorX = left ? _startCrop.Right : _startCrop.Left;
        var anchorY = top ? _startCrop.Bottom : _startCrop.Top;
        var width = Math.Max(.04, Math.Abs(point.X - anchorX));
        var height = Math.Max(.04, Math.Abs(point.Y - anchorY));
        var lockedAspect = LockedNormalizedAspectRatio;
        if (double.IsFinite(lockedAspect) && lockedAspect > .01)
        {
            if (width / height > lockedAspect)
            {
                height = width / lockedAspect;
            }
            else
            {
                width = height * lockedAspect;
            }
        }

        var maximumWidth = left ? anchorX : 1 - anchorX;
        var maximumHeight = top ? anchorY : 1 - anchorY;
        var scale = Math.Min(1, Math.Min(maximumWidth / width, maximumHeight / height));
        width *= scale;
        height *= scale;
        var x = left ? anchorX - width : anchorX;
        var y = top ? anchorY - height : anchorY;
        UpdateCrop(x, y, width, height);
    }

    private void UpdateCrop(double x, double y, double width, double height)
    {
        SetCurrentValue(CropXProperty, Math.Clamp(x, 0, 1));
        SetCurrentValue(CropYProperty, Math.Clamp(y, 0, 1));
        SetCurrentValue(CropWidthProperty, Math.Clamp(width, .01, 1 - CropX));
        SetCurrentValue(CropHeightProperty, Math.Clamp(height, .01, 1 - CropY));
        InvalidateVisual();
    }

    private Rect GetImageRect()
    {
        var aspect = double.IsFinite(ImageAspectRatio) && ImageAspectRatio > .01 ? ImageAspectRatio : 1;
        var availableAspect = Bounds.Width / Math.Max(1, Bounds.Height);
        if (availableAspect > aspect)
        {
            var width = Bounds.Height * aspect;
            return new Rect((Bounds.Width - width) / 2, 0, width, Bounds.Height);
        }

        var height = Bounds.Width / aspect;
        return new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height);
    }

    private Rect GetCropRect(Rect image) => new(
        image.X + (CropX * image.Width),
        image.Y + (CropY * image.Height),
        CropWidth * image.Width,
        CropHeight * image.Height);

    private static Point ToNormalized(Point point, Rect image) => new(
        Math.Clamp((point.X - image.X) / image.Width, 0, 1),
        Math.Clamp((point.Y - image.Y) / image.Height, 0, 1));

    private static DragOperation HitTest(Point point, Rect crop)
    {
        var corners = GetCorners(crop);
        for (var index = 0; index < corners.Length; index++)
        {
            if (Math.Abs(point.X - corners[index].X) <= 14 && Math.Abs(point.Y - corners[index].Y) <= 14)
            {
                return (DragOperation)(index + 2);
            }
        }

        return crop.Contains(point) ? DragOperation.Move : DragOperation.None;
    }

    private static Point[] GetCorners(Rect crop) =>
        [crop.TopLeft, crop.TopRight, crop.BottomLeft, crop.BottomRight];

    private enum DragOperation
    {
        None = 0,
        Move = 1,
        TopLeft = 2,
        TopRight = 3,
        BottomLeft = 4,
        BottomRight = 5
    }
}
