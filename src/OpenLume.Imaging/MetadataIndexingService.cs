using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using Sdcb.LibRaw;
using SkiaSharp;

namespace OpenLume.Imaging;

public sealed record MetadataIndexProgress(int Processed, int Succeeded, int Failed);

public sealed class MetadataIndexingService
{
    private readonly IPhotoCatalog _catalog;
    private readonly int _batchSize;
    private readonly int _concurrency;

    public MetadataIndexingService(IPhotoCatalog catalog, int batchSize = 64, int concurrency = 4)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        _batchSize = batchSize;
        _concurrency = concurrency;
    }

    public async Task IndexPendingAsync(IProgress<MetadataIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _catalog.GetPendingMetadataAsync(_batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) return;
            using var gate = new SemaphoreSlim(_concurrency);
            var tasks = batch.Select(async photo =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var metadata = await MetadataExtractor.ReadAsync(photo.OriginalPath, cancellationToken).ConfigureAwait(false);
                        await _catalog.UpdateMetadataAsync(photo.Id, metadata, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref succeeded);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await _catalog.MarkMetadataFailedAsync(photo.Id, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        var count = Interlocked.Increment(ref processed);
                        progress?.Report(new MetadataIndexProgress(
                            count,
                            Volatile.Read(ref succeeded),
                            Volatile.Read(ref failed)));
                    }
                }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}

public static class MetadataExtractor
{
    public static Task<PhotoMetadata> ReadAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (SupportedPhotoFormats.RawExtensions.Contains(Path.GetExtension(path)))
        {
            using var context = RawContext.OpenFile(path);
            token.ThrowIfCancellationRequested();
            return new PhotoMetadata(context.Width, context.Height, null, stamp);
        }

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unable to read image metadata.");
        return new PhotoMetadata(codec.Info.Width, codec.Info.Height, null, stamp);
    }, token);
}
