using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OpenLume.App.ViewModels;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.App.Controls;

public sealed class LocalMaskOverlayControl : Control, IDisposable
{
    public static readonly StyledProperty<LocalMaskViewModel?> EditorProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, LocalMaskViewModel?>(nameof(Editor));
    public LocalMaskViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    private LocalMaskViewModel? _strokeOwner;
    private LocalMask? _cachedMask;
    private double _cachedAspect;
    private Avalonia.Media.Imaging.Bitmap? _brushBitmap;
    private LocalMask? _renderedMask;
    private readonly object _requestLock = new();
    private OverlayRequest? _pendingRequest;
    private CancellationTokenSource? _activeCancellation;
    private bool _workerRunning;
    private int _generation;
    private Task _overlayTask = Task.CompletedTask;
    private sealed record OverlayRequest(LocalMask Mask, int Width, int Height, int Generation);
    private Point? _cursor;
    public LocalMaskOverlayControl() { Focusable = true; }
    public static readonly StyledProperty<LocalMask?> MaskProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, LocalMask?>(nameof(Mask));
    public static readonly StyledProperty<double> ImageAspectRatioProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, double>(nameof(ImageAspectRatio), 1);
    public static readonly StyledProperty<double> CenterXProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, double>(nameof(CenterX), .5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> CenterYProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, double>(nameof(CenterY), .5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> RadiusXProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, double>(nameof(RadiusX), .25, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> RadiusYProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, double>(nameof(RadiusY), .25, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowOverlayProperty = AvaloniaProperty.Register<LocalMaskOverlayControl, bool>(nameof(ShowOverlay), true);
    public LocalMask? Mask { get => GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
    public double ImageAspectRatio { get => GetValue(ImageAspectRatioProperty); set => SetValue(ImageAspectRatioProperty, value); }
    public double CenterX { get => GetValue(CenterXProperty); set => SetValue(CenterXProperty, value); }
    public double CenterY { get => GetValue(CenterYProperty); set => SetValue(CenterYProperty, value); }
    public double RadiusX { get => GetValue(RadiusXProperty); set => SetValue(RadiusXProperty, value); }
    public double RadiusY { get => GetValue(RadiusYProperty); set => SetValue(RadiusYProperty, value); }
    public bool ShowOverlay { get => GetValue(ShowOverlayProperty); set => SetValue(ShowOverlayProperty, value); }
    private bool _dragging, _resize;
    private Point _start, _center;

    static LocalMaskOverlayControl() => AffectsRender<LocalMaskOverlayControl>(MaskProperty, ImageAspectRatioProperty, ShowOverlayProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty && !ReferenceEquals(_strokeOwner, Editor))
        {
            _strokeOwner?.CancelStroke();
            _strokeOwner = null;
            _dragging = false;
        }
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            _strokeOwner?.CancelStroke();
            _strokeOwner = null;
            _dragging = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        lock (_requestLock)
        {
            _generation++;
            _pendingRequest = null;
            _activeCancellation?.Cancel();
        }
        _strokeOwner?.CancelStroke();
        _strokeOwner = null;
        _brushBitmap?.Dispose();
        _brushBitmap = null;
        _cachedMask = null;
        _renderedMask = null;
        _dragging = false;
        GC.SuppressFinalize(this);
    }

    private void RenderBrush(DrawingContext context, LocalMask mask, Rect image)
    {
        context.DrawRectangle(Brushes.Transparent, null, image);
        if (ShowOverlay && mask.Enabled)
        {
            var aspect = image.Width / image.Height;
            if (!ReferenceEquals(_cachedMask, mask) || _cachedAspect != aspect)
            {
                if (_renderedMask?.Id != mask.Id)
                {
                    _brushBitmap?.Dispose();
                    _brushBitmap = null;
                    _renderedMask = null;
                }
                _cachedMask = mask;
                _cachedAspect = aspect;
                var width = Math.Max(1, (int)(192 * Math.Min(1, aspect)));
                var height = Math.Max(1, (int)(192 / Math.Max(1, aspect)));
                QueueOverlay(mask, width, height);
            }
            if (_brushBitmap is not null) context.DrawImage(_brushBitmap, image);
        }
        if (_cursor is { } cursor && Editor is { } editor)
            context.DrawEllipse(null, new Pen(editor.BrushErase ? Brushes.OrangeRed : Brushes.White, 1.5),
                cursor, editor.BrushSize * image.Width, editor.BrushSize * image.Width);
    }

    private void QueueOverlay(LocalMask mask, int width, int height)
    {
        lock (_requestLock)
        {
            _pendingRequest = new(mask, width, height, ++_generation);
            _activeCancellation?.Cancel();
            if (_workerRunning) return;
            _workerRunning = true;
            _overlayTask = Task.Run(ProcessOverlayRequestsAsync);
        }
    }

    private async Task ProcessOverlayRequestsAsync()
    {
        while (true)
        {
            OverlayRequest request;
            using var cancellation = new CancellationTokenSource();
            lock (_requestLock)
            {
                if (_pendingRequest is null) { _workerRunning = false; return; }
                request = _pendingRequest;
                _pendingRequest = null;
                _activeCancellation = cancellation;
            }
            try
            {
                var bytes = EncodeOverlay(request, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    lock (_requestLock)
                    {
                        if (_generation != request.Generation || !ReferenceEquals(Mask, request.Mask)) return;
                    }
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    _brushBitmap?.Dispose();
                    _brushBitmap = bitmap;
                    _renderedMask = request.Mask;
                    InvalidateVisual();
                }, DispatcherPriority.Background, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Brush overlay rendering failed: {0}", error.Message);
            }
            finally
            {
                lock (_requestLock)
                    if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null;
            }
        }
    }

    private static byte[] EncodeOverlay(OverlayRequest request, CancellationToken cancellationToken)
    {
        var mask = request.Mask;
        var coverage = new float[request.Width * request.Height];
        new BrushRasterizer(mask, request.Width, request.Height).FillTile(0, 0, request.Width, request.Height,
            coverage, new float[coverage.Length], cancellationToken);
        using var bitmap = new SKBitmap(request.Width, request.Height);
        var pixels = new SKColor[coverage.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            var weight = (mask.Inverted ? 1 - coverage[index] : coverage[index]) * mask.Density;
            pixels[index] = new SKColor(255, 70, 100, (byte)(weight * 85));
        }
        cancellationToken.ThrowIfCancellationRequested();
        bitmap.Pixels = pixels;
        using var encodedImage = SKImage.FromBitmap(bitmap);
        using var data = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private Rect ImageRect()
    {
        var aspect = double.IsFinite(ImageAspectRatio) && ImageAspectRatio > .01 ? ImageAspectRatio : 1;
        var width = Math.Min(Bounds.Width, Bounds.Height * aspect);
        var height = width / aspect;
        return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    private static Point Normalized(Point point, Rect image) => new((point.X - image.X) / image.Width, (point.Y - image.Y) / image.Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Mask is not { } mask) return;
        var image = ImageRect();
        if (image.Width <= 1 || image.Height <= 1) return;
        using var clip = context.PushClip(image);
        if (mask.Kind == LocalMaskKind.Brush) { RenderBrush(context, mask, image); return; }
        if (ShowOverlay)
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    var alpha = (byte)(mask.Weight((x + .5) / 32, (y + .5) / 32) * 85);
                    if (alpha == 0) continue;
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 70, 100)), null,
                        new Rect(image.X + x * image.Width / 32, image.Y + y * image.Height / 32, image.Width / 32 + .5, image.Height / 32 + .5));
                }
        var center = new Point(image.X + mask.CenterX * image.Width, image.Y + mask.CenterY * image.Height);
        var pen = new Pen(Brushes.White, 1.5);
        if (mask.Kind == LocalMaskKind.Radial)
            context.DrawEllipse(null, pen, new Rect(center.X - mask.RadiusX * image.Width, center.Y - mask.RadiusY * image.Height,
                mask.RadiusX * image.Width * 2, mask.RadiusY * image.Height * 2));
        else
        {
            var angle = mask.AngleDegrees * Math.PI / 180;
            for (var edge = -1; edge <= 1; edge++)
            {
                var x = mask.CenterX + edge * mask.RadiusX * Math.Cos(angle);
                var y = mask.CenterY + edge * mask.RadiusX * Math.Sin(angle);
                context.DrawLine(pen, new Point(image.X + (x - Math.Sin(angle) * 2) * image.Width, image.Y + (y + Math.Cos(angle) * 2) * image.Height),
                    new Point(image.X + (x + Math.Sin(angle) * 2) * image.Width, image.Y + (y - Math.Cos(angle) * 2) * image.Height));
            }
        }
        context.DrawEllipse(Brushes.White, null, center, 5, 5);
        if (mask.Kind == LocalMaskKind.Radial)
            context.DrawRectangle(Brushes.White, null, new Rect(center.X + mask.RadiusX * image.Width - 4, center.Y + mask.RadiusY * image.Height - 4, 8, 8));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var image = ImageRect();
        var point = e.GetPosition(this);
        if (Mask is not { } mask || !image.Contains(point) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (mask.Kind == LocalMaskKind.Brush)
        {
            var normalized = Normalized(point, image);
            if (Editor?.BeginStroke(new(normalized.X, normalized.Y), image.Width / image.Height) != true) return;
            _strokeOwner = Editor;
            _dragging = true;
            _cursor = point;
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        var handle = new Point(image.X + (mask.CenterX + mask.RadiusX) * image.Width, image.Y + (mask.CenterY + mask.RadiusY) * image.Height);
        _resize = mask.Kind == LocalMaskKind.Radial && Math.Abs(point.X - handle.X) < 14 && Math.Abs(point.Y - handle.Y) < 14;
        _start = Normalized(point, image);
        _center = new Point(mask.CenterX, mask.CenterY);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Mask?.Kind == LocalMaskKind.Brush)
        {
            _cursor = e.GetPosition(this);
            if (_dragging && _strokeOwner is { } owner)
            {
                var position = Normalized(_cursor.Value, ImageRect());
                owner.AppendStroke(new(position.X, position.Y));
                e.Handled = true;
            }
            InvalidateVisual();
            return;
        }
        if (!_dragging) return;
        var point = Normalized(e.GetPosition(this), ImageRect());
        if (_resize)
        {
            SetCurrentValue(RadiusXProperty, Math.Clamp(Math.Abs(point.X - _center.X), .01, 1));
            SetCurrentValue(RadiusYProperty, Math.Clamp(Math.Abs(point.Y - _center.Y), .01, 1));
        }
        else
        {
            SetCurrentValue(CenterXProperty, Math.Clamp(_center.X + point.X - _start.X, 0, 1));
            SetCurrentValue(CenterYProperty, Math.Clamp(_center.Y + point.Y - _start.Y, 0, 1));
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_strokeOwner is { } owner)
        {
            var point = Normalized(e.GetPosition(this), ImageRect());
            owner.AppendStroke(new(point.X, point.Y));
            owner.FinishStroke();
            _strokeOwner = null;
        }
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _strokeOwner?.FinishStroke();
        _strokeOwner = null;
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || _strokeOwner is null) return;
        _strokeOwner.CancelStroke();
        _strokeOwner = null;
        _dragging = false;
        e.Handled = true;
        InvalidateVisual();
    }
}
