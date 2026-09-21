using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using SkiaSharp;

namespace OpenLume.Imaging;

public sealed class ThumbnailCache : IDisposable
{
    private sealed record CacheEntry(
        string Key,
        string Source,
        long Length,
        long LastWriteUtcTicks,
        int Width,
        int Height,
        long Bytes,
        long LastAccessUtcTicks);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly long _maxBytes;
    private readonly IImageRenderer _renderer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ThumbnailCache(string directory, long maxBytes, IImageRenderer? renderer = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Cache directory is required.", nameof(directory));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _directory = Path.GetFullPath(directory);
        _indexPath = Path.Combine(_directory, "index.json");
        _maxBytes = maxBytes;
        _renderer = renderer ?? new SkiaImageRenderer();
    }

    public async Task<RenderedImage> GetOrCreateAsync(
        string sourcePath,
        EditRecipe edit,
        int maxDimension,
        CancellationToken cancellationToken = default) =>
        await GetOrCreateCoreAsync(sourcePath, edit, maxDimension, retryOnSourceChange: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<RenderedImage> GetOrCreateCoreAsync(
        string sourcePath,
        EditRecipe edit,
        int maxDimension,
        bool retryOnSourceChange,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("Source image was not found.", fullPath);
        }

        var normalizedEdit = edit.Normalize();
        var key = CreateKey(fullPath, source, normalizedEdit, maxDimension);
        var cached = await TryReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var rendered = await _renderer
            .RenderPreviewAsync(fullPath, normalizedEdit, maxDimension, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.Refresh();
        if (!source.Exists)
        {
            throw new FileNotFoundException("Source image disappeared while its thumbnail was rendered.", fullPath);
        }

        var currentKey = CreateKey(fullPath, source, normalizedEdit, maxDimension);
        if (currentKey != key)
        {
            return retryOnSourceChange
                ? await GetOrCreateCoreAsync(
                    fullPath, normalizedEdit, maxDimension, retryOnSourceChange: false, cancellationToken)
                    .ConfigureAwait(false)
                : rendered;
        }

        return await StoreOrReadWinnerAsync(
            key, fullPath, source, rendered, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<RenderedImage?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAndRepairIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var match = entries.FirstOrDefault(entry => entry.Key == key);
            if (match is null)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(CachePath(key), cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != match.Bytes || !IsValidJpeg(bytes, match.Width, match.Height))
            {
                TryDelete(CachePath(key));
                entries.Remove(match);
                await SaveIndexUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
                return null;
            }

            entries[entries.IndexOf(match)] = match with { LastAccessUtcTicks = DateTime.UtcNow.Ticks };
            await SaveIndexUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
            return new RenderedImage(bytes, "image/jpeg", match.Width, match.Height);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RenderedImage> StoreOrReadWinnerAsync(
        string key,
        string fullPath,
        FileInfo source,
        RenderedImage rendered,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAndRepairIndexUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var winner = entries.FirstOrDefault(entry => entry.Key == key);
            if (winner is not null)
            {
                var bytes = await File.ReadAllBytesAsync(CachePath(key), cancellationToken).ConfigureAwait(false);
                entries[entries.IndexOf(winner)] = winner with { LastAccessUtcTicks = DateTime.UtcNow.Ticks };
                await SaveIndexUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
                return new RenderedImage(bytes, "image/jpeg", winner.Width, winner.Height);
            }

            Directory.CreateDirectory(_directory);
            var cachePath = CachePath(key);
            await WriteAtomicallyAsync(cachePath, rendered.Data, cancellationToken).ConfigureAwait(false);

            foreach (var stale in entries
                         .Where(entry => PathsEqual(entry.Source, fullPath) && entry.Key != key)
                         .ToArray())
            {
                TryDelete(CachePath(stale.Key));
                entries.Remove(stale);
            }

            entries.Add(new CacheEntry(
                key,
                fullPath,
                source.Length,
                source.LastWriteTimeUtc.Ticks,
                rendered.Width,
                rendered.Height,
                new FileInfo(cachePath).Length,
                DateTime.UtcNow.Ticks));
            EvictUnsafe(entries);
            await SaveIndexUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
            return rendered;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<CacheEntry>> ReadAndRepairIndexUnsafeAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CacheEntry>();
        if (File.Exists(_indexPath))
        {
            try
            {
                await using var stream = new FileStream(
                    _indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
                entries = await JsonSerializer.DeserializeAsync<List<CacheEntry>>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
            }
            catch (JsonException)
            {
                TryDelete(_indexPath);
            }
            catch (IOException)
            {
                TryDelete(_indexPath);
            }
        }

        entries.RemoveAll(entry => !File.Exists(CachePath(entry.Key)));
        if (Directory.Exists(_directory))
        {
            var known = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var candidate in Directory.EnumerateFiles(_directory, "*.jpg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(candidate);
                if (name.Length == 64 && !known.Contains(name))
                {
                    TryDelete(candidate);
                }
            }
        }

        return entries;
    }

    private void EvictUnsafe(List<CacheEntry> entries)
    {
        var total = entries.Sum(entry => entry.Bytes);
        foreach (var entry in entries.OrderBy(entry => entry.LastAccessUtcTicks).ToArray())
        {
            if (total <= _maxBytes)
            {
                break;
            }

            TryDelete(CachePath(entry.Key));
            entries.Remove(entry);
            total -= entry.Bytes;
        }
    }

    private async Task SaveIndexUnsafeAsync(
        IReadOnlyCollection<CacheEntry> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, entries, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string CachePath(string key) => Path.Combine(_directory, key + ".jpg");

    private static string CreateKey(
        string path,
        FileInfo source,
        EditRecipe edit,
        int maxDimension)
    {
        var text = string.Join('\0',
            path,
            source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            maxDimension.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(edit, JsonOptions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsValidJpeg(byte[] data, int expectedWidth, int expectedHeight)
    {
        try
        {
            using var encoded = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(encoded);
            return codec is not null &&
                   codec.Info.Width == expectedWidth &&
                   codec.Info.Height == expectedHeight;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
