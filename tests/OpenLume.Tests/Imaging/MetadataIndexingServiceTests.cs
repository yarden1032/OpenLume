using OpenLume.Core.Domain;
using OpenLume.Imaging;
using OpenLume.Infrastructure.Catalog;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class MetadataIndexingServiceTests
{
    [Fact]
    public async Task IndexesValidRasterAndDoesNotRetryFailedFileForever()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var valid = Path.Combine(root, "valid.png");
            WritePng(valid, 40, 30);
            await File.WriteAllTextAsync(Path.Combine(root, "broken.jpg"), "not an image");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var progress = new List<MetadataIndexProgress>();
            var service = new MetadataIndexingService(catalog, batchSize: 1, concurrency: 2);

            await service.IndexPendingAsync(new InlineProgress<MetadataIndexProgress>(progress.Add));

            var photos = await catalog.GetPhotosAsync();
            var indexed = Assert.Single(photos, photo => photo.FileName == "valid.png");
            var failed = Assert.Single(photos, photo => photo.FileName == "broken.jpg");
            Assert.Equal((40, 30, MetadataIndexState.Complete),
                (indexed.PixelWidth, indexed.PixelHeight, indexed.MetadataState));
            Assert.Equal(MetadataIndexState.Failed, failed.MetadataState);
            Assert.Empty(await catalog.GetPendingMetadataAsync(10));
            Assert.Equal(2, progress[^1].Processed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationLeavesPhotoPendingForResume()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WritePng(Path.Combine(root, "valid.png"), 20, 10);
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new MetadataIndexingService(catalog);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.IndexPendingAsync(cancellationToken: cancellation.Token));

            Assert.Single(await catalog.GetPendingMetadataAsync(10));
            await service.IndexPendingAsync();
            Assert.Empty(await catalog.GetPendingMetadataAsync(10));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedSourceIsQueuedAgain()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "valid.png");
            WritePng(path, 20, 10);
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var service = new MetadataIndexingService(catalog);
            await service.IndexPendingAsync();

            WritePng(path, 60, 50);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert.Single(await catalog.GetPendingMetadataAsync(10));
            Assert.Equal(MetadataIndexState.Pending, (await catalog.GetPhotoAsync(
                (await catalog.GetPhotosAsync()).Single().Id))!.MetadataState);
            await service.IndexPendingAsync();

            var updated = Assert.Single(await catalog.GetPhotosAsync());
            Assert.Equal((60, 50), (updated.PixelWidth, updated.PixelHeight));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openlume-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
