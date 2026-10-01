using System.Reflection;
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
    public async Task OverlayCoalescesRequestsIntoOneBackgroundWorkerAndOnlyPublishesLatestMask()
    {
        using var overlay = new LocalMaskOverlayControl { ImageAspectRatio = 2 };
        var window = new Window { Width = 400, Height = 300, Content = overlay };
        var first = new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush,
            BrushStrokes: new([new BrushStroke(new([new MaskPoint(.2, .5), new MaskPoint(.8, .5)]))])).Normalize();
        var latest = first with { Id = Guid.NewGuid(), Density = .2 };
        static object? Field(LocalMaskOverlayControl control, string name) =>
            typeof(LocalMaskOverlayControl).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control);
        static void Flush()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            window.Show();
            Flush();
            Task worker;
            // Hold the request monitor so the background worker cannot consume the first request yet.
            lock (Field(overlay, "_requestLock")!)
            {
                overlay.Mask = first;
                Flush();
                worker = (Task)Field(overlay, "_overlayTask")!;
                Assert.False(worker.IsCompleted);
                overlay.Mask = latest;
                Flush();
                Assert.Same(worker, Field(overlay, "_overlayTask"));
                Assert.Null(Field(overlay, "_brushBitmap"));
            }
            await worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(latest, Field(overlay, "_renderedMask"));
            Assert.NotNull(Field(overlay, "_brushBitmap"));
            lock (Field(overlay, "_requestLock")!)
            {
                overlay.Mask = first;
                Flush();
                worker = (Task)Field(overlay, "_overlayTask")!;
                overlay.Dispose();
            }
            await worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(Field(overlay, "_renderedMask"));
            Assert.Null(Field(overlay, "_brushBitmap"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BrushPointerStrokeCommitsOnceAndEscapeAndSelectionChangeCancel()
    {
        var commits = 0;
        var vm = new LocalMaskViewModel(new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush), () => commits++);
        using var overlay = new LocalMaskOverlayControl { ImageAspectRatio = 2, Editor = vm };
        overlay.Bind(LocalMaskOverlayControl.MaskProperty, new Binding(nameof(vm.Recipe)) { Source = vm });
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
            Assert.Same(overlay, window.InputHitTest(At(.2, .5)));
            window.MouseDown(At(.2, .5), MouseButton.Left);
            window.MouseMove(At(.8, .5));
            Assert.True(vm.IsPainting);
            Assert.Equal(0, commits);
            window.MouseUp(At(.8, .5), MouseButton.Left);
            Assert.Equal(1, commits);
            Assert.False(vm.IsPainting);
            Assert.Equal(.5, vm.Recipe.Weight(.5, .5), 6);
            var applied = vm.Recipe;
            window.MouseDown(At(.5, .2), MouseButton.Left);
            window.MouseMove(At(.5, .8));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(At(.5, .8), MouseButton.Left);
            Assert.Equal(applied, vm.Recipe);
            Assert.Equal(1, commits);
            window.MouseDown(At(.5, .2), MouseButton.Left);
            var other = new LocalMaskViewModel(new LocalMask(Guid.NewGuid(), Kind: LocalMaskKind.Brush), () => commits++);
            overlay.Editor = other;
            window.MouseUp(At(.5, .8), MouseButton.Left);
            Assert.Equal(applied, vm.Recipe);
            Assert.Empty(other.Recipe.BrushStrokes!);
            Assert.Equal(1, commits);
        }
        finally { window.Close(); }
    }

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
