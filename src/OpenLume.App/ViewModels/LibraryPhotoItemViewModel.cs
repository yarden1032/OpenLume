using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLume.Core.Domain;
using OpenLume.Imaging;

namespace OpenLume.App.ViewModels;

public sealed class LibraryPhotoItemViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _thumbnailUnavailable;
    private bool _disposed;

    public LibraryPhotoItemViewModel(PhotoAsset asset)
    {
        Asset = asset;
    }

    public PhotoAsset Asset { get; private set; }

    public Guid Id => Asset.Id;

    public string FileName => Asset.FileName;

    public string Extension => Asset.Extension;

    public int Rating => Asset.Rating;

    public PickState PickState => Asset.PickState;

    public bool IsMissing => Asset.IsMissing;

    public string Dimensions => Asset.PixelWidth > 0 && Asset.PixelHeight > 0
        ? $"{Asset.PixelWidth}×{Asset.PixelHeight}"
        : "Indexing…";

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            var previous = _thumbnail;
            if (SetProperty(ref _thumbnail, value))
            {
                previous?.Dispose();
            }
        }
    }

    public bool ThumbnailUnavailable
    {
        get => _thumbnailUnavailable;
        private set => SetProperty(ref _thumbnailUnavailable, value);
    }

    public async Task LoadThumbnailAsync(ThumbnailCache cache, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsMissing)
        {
            ThumbnailUnavailable = true;
            return;
        }

        try
        {
            var image = await cache.GetOrCreateAsync(
                Asset.OriginalPath, Asset.Edit, 320, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(new MemoryStream(image.Data, writable: false));
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            Thumbnail = bitmap;
            ThumbnailUnavailable = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ThumbnailUnavailable = true;
        }
    }

    public void Update(PhotoAsset asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Asset = asset;
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Thumbnail = null;
    }
}
