using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OpenLume.App.Controls;
using OpenLume.App.ViewModels;
using OpenLume.Core.Domain;

namespace OpenLume.Tests.App;

[Collection("Renderer budget")]
public sealed class LocalMaskOverlayTests
{
    [AvaloniaFact]
    public void PointerDragMovesAndResizesMaskInLetterboxedImageSpace()
    {
        var vm = new LocalMaskViewModel(new LocalMask(Guid.NewGuid()), () => { });
        var overlay = new LocalMaskOverlayControl { ImageAspectRatio = 2 };
        overlay.Bind(LocalMaskOverlayControl.MaskProperty, new Binding(nameof(vm.Recipe)) { Source = vm });
        overlay.Bind(LocalMaskOverlayControl.CenterXProperty, new Binding(nameof(vm.CenterX)) { Source = vm, Mode = BindingMode.TwoWay });
        overlay.Bind(LocalMaskOverlayControl.CenterYProperty, new Binding(nameof(vm.CenterY)) { Source = vm, Mode = BindingMode.TwoWay });
        overlay.Bind(LocalMaskOverlayControl.RadiusXProperty, new Binding(nameof(vm.RadiusX)) { Source = vm, Mode = BindingMode.TwoWay });
        overlay.Bind(LocalMaskOverlayControl.RadiusYProperty, new Binding(nameof(vm.RadiusY)) { Source = vm, Mode = BindingMode.TwoWay });
        var window = new Window { Width = 400, Height = 300, Content = overlay };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs();
            var width = overlay.Bounds.Width;
            var height = width / 2;
            var top = (overlay.Bounds.Height - height) / 2;
            Point At(double x, double y) => overlay.TranslatePoint(new Point(x * width, top + y * height), window)!.Value;
            Assert.True(width > 0);
            Assert.Same(overlay, window.InputHitTest(At(.5, .5)));
            window.MouseDown(At(.5, .5), MouseButton.Left);
            window.MouseMove(At(.6, .6));
            window.MouseUp(At(.6, .6), MouseButton.Left);
            Assert.Equal(.6, vm.CenterX, 5);
            Assert.Equal(.6, vm.CenterY, 5);
            window.MouseDown(At(.85, .85), MouseButton.Left);
            window.MouseMove(At(.8, .8));
            window.MouseUp(At(.8, .8), MouseButton.Left);
            Assert.Equal(.2, vm.RadiusX, 5);
            Assert.Equal(.2, vm.RadiusY, 5);
        }
        finally { window.Close(); }
    }
}
