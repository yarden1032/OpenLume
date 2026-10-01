using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using OpenLume.App.ViewModels;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using OpenLume.Infrastructure.Catalog;
using OpenLume.Infrastructure.Presets;
using SkiaSharp;

namespace OpenLume.Tests.App;

public sealed class CropWorkflowTests
{
    [AvaloniaFact]
    public async Task CancelDiscardsPendingGeometryWhileKeepingOtherDevelopEdits()
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        vm.StartCropCommand.Execute(null);
        vm.RotationDegrees = 13;
        vm.RotateCropRightCommand.Execute(null);
        vm.CropWidth = .6;
        vm.Exposure = 1;
        await AwaitTaskAsync(vm, "_editTask");

        var saved = (await context.Catalog.GetPhotoAsync(context.PhotoId))!.Edit;
        Assert.Equal(1, saved.ExposureEv);
        Assert.Equal(2, saved.RotationDegrees);
        Assert.Equal(context.Initial.Crop, saved.Crop);
        Assert.True(vm.IsCropMode);
        Assert.Equal(13, vm.RotationDegrees);
        Assert.Equal(1, vm.CropQuarterTurns);

        vm.CancelCropCommand.Execute(null);
        await AwaitTaskAsync(vm, "_previewTask");
        Assert.False(vm.IsCropMode);
        Assert.Equal(2, vm.RotationDegrees);
        Assert.Equal(context.Initial.Crop!.Width, vm.CropWidth);
        Assert.Equal(1, vm.Exposure);
        Assert.Equal(3, (await context.Catalog.GetEditHistoryAsync(context.PhotoId)).Revisions.Count);
    }

    [AvaloniaFact]
    public async Task LocalMasksPersistReorderRemoveAndUndoWithoutChangingCrop()
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        var originalCrop = vm.SelectedPhoto!.Edit.Crop;
        vm.AddRadialMaskCommand.Execute(null);
        vm.SelectedLocalMask!.Name = "Face light";
        vm.SelectedLocalMask.ExposureEv = 1;
        await AwaitTaskAsync(vm, "_editTask");
        var saved = (await context.Catalog.GetPhotoAsync(context.PhotoId))!.Edit;
        Assert.Single(saved.LocalMasks!);
        Assert.Equal("Face light", saved.LocalMasks![0].Name);
        Assert.Equal(1, saved.LocalMasks[0].ExposureEv);
        Assert.Equal(originalCrop, saved.Crop);
        Assert.True(vm.IsLocalMaskMode);
        vm.AddLinearMaskCommand.Execute(null);
        vm.MoveLocalMaskUpCommand.Execute(null);
        await AwaitTaskAsync(vm, "_editTask");
        Assert.Equal(LocalMaskKind.Linear, (await context.Catalog.GetPhotoAsync(context.PhotoId))!.Edit.LocalMasks![0].Kind);
        vm.RemoveLocalMaskCommand.Execute(null);
        await AwaitTaskAsync(vm, "_editTask");
        Assert.Single((await context.Catalog.GetPhotoAsync(context.PhotoId))!.Edit.LocalMasks!);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.LocalMasks.Count);
        Assert.Equal(LocalMaskKind.Linear, vm.LocalMasks[0].Recipe.Kind);
    }

    [AvaloniaFact]
    public async Task GlobalPresetPreservesPendingLocalMasksAndHasUndoableHistory()
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        vm.AddRadialMaskCommand.Execute(null);
        vm.SelectedLocalMask!.ExposureEv = 1;
        var maskId = vm.SelectedLocalMask.Recipe.Id;
        var preset = Path.Combine(context.Root, "global.xmp");
        await File.WriteAllTextAsync(preset,
            "<x xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/' crs:Exposure2012='0.8'/>", TestContext.Current.CancellationToken);
        await vm.ImportPresetAsync(preset);
        var saved = (await context.Catalog.GetPhotoAsync(context.PhotoId, TestContext.Current.CancellationToken))!.Edit;
        Assert.Equal(.8, saved.ExposureEv);
        Assert.Equal(maskId, Assert.Single(saved.LocalMasks!).Id);
        Assert.Equal(1, saved.LocalMasks![0].ExposureEv);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(context.Initial.ExposureEv, vm.Exposure);
        Assert.Equal(maskId, Assert.Single(vm.LocalMasks).Recipe.Id);
    }

    [AvaloniaFact]
    public async Task ApplyCommitsPendingGeometryOnceAndUndoRedoPersistIt()
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        vm.StartCropCommand.Execute(null);
        vm.RotationDegrees = 13;
        vm.RotateCropRightCommand.Execute(null);
        vm.FlipCropHorizontalCommand.Execute(null);
        vm.CropWidth = .6;
        vm.Exposure = 1;
        await vm.ApplyCropCommand.ExecuteAsync(null);

        var saved = (await context.Catalog.GetPhotoAsync(context.PhotoId))!.Edit;
        Assert.False(vm.IsCropMode);
        Assert.Equal(13, saved.RotationDegrees);
        Assert.Equal(1, saved.ExposureEv);
        Assert.Equal(.6, saved.Crop!.Width);
        Assert.Equal(1, saved.Crop.QuarterTurns);
        Assert.True(saved.Crop.FlipHorizontal);
        Assert.Equal(context.Initial.Optics, saved.Optics);
        Assert.Equal(4, (await context.Catalog.GetEditHistoryAsync(context.PhotoId)).Revisions.Count);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.RotationDegrees);
        Assert.Equal(1, vm.Exposure);
        Assert.Equal(context.Initial.Crop, vm.SelectedPhoto!.Edit.Crop);
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Equal(saved, vm.SelectedPhoto!.Edit);
        await using var reopened = new SqlitePhotoCatalog(context.DatabasePath);
        Assert.Equal(saved, (await reopened.GetPhotoAsync(context.PhotoId))!.Edit);
    }

    [AvaloniaTheory]
    [InlineData("1:1", 1)]
    [InlineData("4:5", .8)]
    [InlineData("16:9", 16d / 9)]
    public async Task AspectRemainsCorrectAfterRotateStraightenAndImmediateApply(string preset, double aspect)
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        vm.StartCropCommand.Execute(null);
        await AwaitTaskAsync(vm, "_previewTask");
        vm.SelectedCropAspect = preset;
        vm.RotateCropRightCommand.Execute(null);
        await AwaitTaskAsync(vm, "_previewTask");
        Assert.Equal(aspect, vm.CropWidth / vm.CropHeight * vm.CropPreviewAspectRatio, 6);
        vm.RotationDegrees = 23;
        await vm.ApplyCropCommand.ExecuteAsync(null);
        Assert.Equal(preset, vm.SelectedCropAspect);

        var recipe = vm.SelectedPhoto!.Edit;
        var fullFrame = await context.Renderer.RenderPreviewAsync(vm.SelectedPhoto.OriginalPath,
            recipe with { Crop = recipe.Crop!.WithoutCrop() }, 1800);
        Assert.Equal(aspect, recipe.Crop!.Width / recipe.Crop.Height * fullFrame.Width / fullFrame.Height, 6);
    }

    [AvaloniaFact]
    public async Task FreeCropIsUnchangedByPreviewGeometryChanges()
    {
        await using var context = await Context.CreateAsync();
        var vm = context.ViewModel;
        vm.StartCropCommand.Execute(null);
        vm.CropWidth = .6;
        vm.RotateCropRightCommand.Execute(null);
        vm.RotationDegrees = 23;
        await AwaitTaskAsync(vm, "_previewTask");
        Assert.Equal("Free", vm.SelectedCropAspect);
        Assert.Equal(.6, vm.CropWidth);
        Assert.Equal(context.Initial.Crop!.Height, vm.CropHeight);
    }

    private static Task AwaitTaskAsync(MainWindowViewModel vm, string field) =>
        ((Task)typeof(MainWindowViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!).WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class Context : IAsyncDisposable
    {
        public required string Root { get; init; }
        public string DatabasePath => Path.Combine(Root, "catalog.db");
        public required SqlitePhotoCatalog Catalog { get; init; }
        public required SkiaImageRenderer Renderer { get; init; }
        public required MainWindowViewModel ViewModel { get; init; }
        public required Guid PhotoId { get; init; }
        public required EditRecipe Initial { get; init; }

        public static async Task<Context> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "openlume-crop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var bitmap = new SKBitmap(120, 80);
            bitmap.Erase(SKColors.Gray);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(root, "photo.png"), data.ToArray());
            var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, false);
            var photo = (await catalog.GetPhotosAsync()).Single();
            var initial = new EditRecipe(RotationDegrees: 2, Crop: new CropGeometry(.1, .1, .8, .7),
                Optics: new OpticsCorrections(LensVignette: 12)).Normalize();
            await catalog.UpdateEditAsync(photo.Id, initial);
            photo = (await catalog.GetPhotoAsync(photo.Id))!;
            var renderer = new SkiaImageRenderer();
            var vm = new MainWindowViewModel(catalog, renderer, new UnusedAnalysisProvider(),
                new XmpPresetImporter(), new ThumbnailCache(Path.Combine(root, "cache"), 1_000_000, renderer),
                new MetadataIndexingService(catalog));
            vm.SelectedItem = new LibraryPhotoItemViewModel(photo);
            await AwaitTaskAsync(vm, "_previewTask");
            return new Context
            {
                Root = root,
                Catalog = catalog,
                Renderer = renderer,
                ViewModel = vm,
                PhotoId = photo.Id,
                Initial = initial
            };
        }

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class UnusedAnalysisProvider : IPhotoAnalysisProvider
    {
        public string Name => "Unused";
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<PhotoAnalysis> AnalyzeAsync(PhotoAsset photo, byte[] previewJpeg,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
