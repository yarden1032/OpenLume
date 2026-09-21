using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;
using SkiaSharp;

namespace OpenLume.Tests.Imaging;

public sealed class ThumbnailCacheTests
{
    [Fact]
    public async Task CacheHitSurvivesRestartWithoutRenderingAgain()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(source, "source");
            var encodedThumbnail = CreateJpeg();
            var renderer = new CountingRenderer(encodedThumbnail);
            var cacheDirectory = Path.Combine(root, "cache");

            using (var cache = new ThumbnailCache(cacheDirectory, 1_000, renderer))
            {
                await cache.GetOrCreateAsync(source, EditRecipe.Default, 256);
            }

            using (var cache = new ThumbnailCache(cacheDirectory, 1_000, renderer))
            {
                var cached = await cache.GetOrCreateAsync(source, EditRecipe.Default, 256);
                Assert.Equal(encodedThumbnail.Length, cached.Data.Length);
            }

            Assert.Equal(1, renderer.RenderCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SourceAndEditChangesInvalidateStaleThumbnail()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(source, "one");
            var renderer = new CountingRenderer(CreateJpeg());
            var cacheDirectory = Path.Combine(root, "cache");
            using var cache = new ThumbnailCache(cacheDirectory, 1_000, renderer);

            await cache.GetOrCreateAsync(source, EditRecipe.Default, 256);
            await cache.GetOrCreateAsync(source, EditRecipe.Default with { ExposureEv = 1 }, 256);
            await File.AppendAllTextAsync(source, "two");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(1));
            await cache.GetOrCreateAsync(source, EditRecipe.Default with { ExposureEv = 1 }, 256);

            Assert.Equal(3, renderer.RenderCount);
            Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.jpg"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeastRecentlyUsedEntryIsEvictedWhenBudgetIsExceeded()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var first = Path.Combine(root, "first.jpg");
            var second = Path.Combine(root, "second.jpg");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            var renderer = new CountingRenderer(CreateJpeg());
            using var cache = new ThumbnailCache(Path.Combine(root, "cache"), 100, renderer);

            await cache.GetOrCreateAsync(first, EditRecipe.Default, 256);
            await cache.GetOrCreateAsync(second, EditRecipe.Default, 256);
            await cache.GetOrCreateAsync(first, EditRecipe.Default, 256);

            Assert.Equal(3, renderer.RenderCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDoesNotCreateCacheFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(source, "source");
            var renderer = new CountingRenderer([1, 2, 3]);
            var cacheDirectory = Path.Combine(root, "cache");
            using var cache = new ThumbnailCache(cacheDirectory, 100, renderer);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cache.GetOrCreateAsync(source, EditRecipe.Default, 256, cancellation.Token));
            Assert.Equal(0, renderer.RenderCount);
            Assert.False(Directory.Exists(cacheDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptCachedPayloadIsDiscardedAndRegenerated()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(source, "source");
            var renderer = new CountingRenderer(CreateJpeg());
            var cacheDirectory = Path.Combine(root, "cache");
            using var cache = new ThumbnailCache(cacheDirectory, 10_000, renderer);
            await cache.GetOrCreateAsync(source, EditRecipe.Default, 256);
            var cachedPath = Assert.Single(Directory.EnumerateFiles(cacheDirectory, "*.jpg"));
            await File.WriteAllBytesAsync(cachedPath, [1, 2, 3, 4]);

            var repaired = await cache.GetOrCreateAsync(source, EditRecipe.Default, 256);

            Assert.Equal(2, renderer.RenderCount);
            Assert.True(repaired.Data.Length > 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openlume-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateJpeg()
    {
        using var bitmap = new SKBitmap(32, 24);
        bitmap.Erase(SKColors.DarkSlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private sealed class CountingRenderer(byte[] data) : IImageRenderer
    {
        public int RenderCount { get; private set; }

        public Task<RenderedImage> RenderPreviewAsync(
            string sourcePath,
            EditRecipe edit,
            int maxDimension,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderCount++;
            return Task.FromResult(new RenderedImage(data, "image/jpeg", 32, 24));
        }

        public Task ExportJpegAsync(
            string sourcePath,
            string destinationPath,
            EditRecipe edit,
            int quality,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
