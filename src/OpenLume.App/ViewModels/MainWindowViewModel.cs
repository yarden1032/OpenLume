using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPhotoCatalog _catalog;
    private readonly IImageRenderer _renderer;
    private readonly IPhotoAnalysisProvider _analysisProvider;
    private PhotoAsset? _selectedPhoto;
    private Bitmap? _preview;
    private string _status = "Starting OpenLume…";
    private double _exposure;
    private int _rating;
    private bool _isBusy;
    private CancellationTokenSource? _previewCancellation;
    private bool _syncingSelection;

    public MainWindowViewModel(
        IPhotoCatalog catalog,
        IImageRenderer renderer,
        IPhotoAnalysisProvider analysisProvider)
    {
        _catalog = catalog;
        _renderer = renderer;
        _analysisProvider = analysisProvider;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, () => SelectedPhoto is not null && !IsBusy);
        PickCommand = new AsyncRelayCommand(() => SetPickStateAsync(PickState.Pick), () => SelectedPhoto is not null);
        RejectCommand = new AsyncRelayCommand(() => SetPickStateAsync(PickState.Reject), () => SelectedPhoto is not null);
        ResetEditCommand = new AsyncRelayCommand(ResetEditAsync, () => SelectedPhoto is not null);
    }

    public ObservableCollection<PhotoAsset> Photos { get; } = new();
    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand PickCommand { get; }
    public IAsyncRelayCommand RejectCommand { get; }
    public IAsyncRelayCommand ResetEditCommand { get; }

    public PhotoAsset? SelectedPhoto
    {
        get => _selectedPhoto;
        set
        {
            if (!SetProperty(ref _selectedPhoto, value)) return;
            _syncingSelection = true;
            Exposure = value?.Edit.ExposureEv ?? 0;
            Rating = value?.Rating ?? 0;
            _syncingSelection = false;
            NotifyCommands();
            _ = RenderSelectedAsync();
        }
    }

    public Bitmap? Preview
    {
        get => _preview;
        private set
        {
            var previous = _preview;
            SetProperty(ref _preview, value);
            previous?.Dispose();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double Exposure
    {
        get => _exposure;
        set
        {
            if (!SetProperty(ref _exposure, Math.Clamp(value, -5, 5)) || _syncingSelection) return;
            _ = ApplyExposureAsync();
        }
    }

    public int Rating
    {
        get => _rating;
        set
        {
            if (!SetProperty(ref _rating, Math.Clamp(value, 0, 5)) || _syncingSelection) return;
            _ = ApplyRatingAsync();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) NotifyCommands();
        }
    }

    public async Task InitializeAsync()
    {
        await _catalog.InitializeAsync();
        await ReloadAsync();
        var ollamaAvailable = await _analysisProvider.IsAvailableAsync();
        Status = ollamaAvailable
            ? $"Ready · {_analysisProvider.Name} available"
            : $"Ready · {_analysisProvider.Name} model not installed";
    }

    public async Task ImportFolderAsync(string folder)
    {
        IsBusy = true;
        Status = $"Importing {folder}…";
        try
        {
            var result = await _catalog.ImportFolderAsync(folder, includeSubfolders: true);
            await ReloadAsync();
            Status = $"Imported {result.Imported}; already present {result.AlreadyPresent}; failed {result.Failed}";
        }
        catch (Exception exception)
        {
            Status = $"Import failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportSelectedAsync(string destinationPath)
    {
        if (SelectedPhoto is null) return;
        IsBusy = true;
        Status = "Exporting JPEG…";
        try
        {
            await _renderer.ExportJpegAsync(SelectedPhoto.OriginalPath, destinationPath, SelectedPhoto.Edit, 92);
            Status = $"Exported {Path.GetFileName(destinationPath)}";
        }
        catch (Exception exception)
        {
            Status = $"Export failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync(Guid? selectId = null)
    {
        selectId ??= SelectedPhoto?.Id;
        var assets = await _catalog.GetPhotosAsync();
        Photos.Clear();
        foreach (var asset in assets) Photos.Add(asset);
        SelectedPhoto = Photos.FirstOrDefault(photo => photo.Id == selectId) ?? Photos.FirstOrDefault();
    }

    private async Task RenderSelectedAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;
        var photo = SelectedPhoto;
        if (photo is null)
        {
            Preview = null;
            return;
        }

        try
        {
            var rendered = await _renderer.RenderPreviewAsync(photo.OriginalPath, photo.Edit, 1800, token);
            token.ThrowIfCancellationRequested();
            Preview = new Bitmap(new MemoryStream(rendered.Data, writable: false));
            Status = $"{photo.FileName} · {rendered.Width}×{rendered.Height}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Preview unavailable: {exception.Message}";
            Preview = null;
        }
    }

    private async Task ApplyExposureAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null) return;
        var edit = photo.Edit with { ExposureEv = Exposure };
        await _catalog.UpdateEditAsync(photo.Id, edit);
        ReplacePhoto(photo with { Edit = edit.Normalize() });
        await RenderSelectedAsync();
    }

    private async Task ApplyRatingAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null) return;
        await _catalog.UpdateRatingAsync(photo.Id, Rating);
        ReplacePhoto(photo with { Rating = Rating });
    }

    private async Task SetPickStateAsync(PickState state)
    {
        var photo = SelectedPhoto;
        if (photo is null) return;
        await _catalog.UpdatePickStateAsync(photo.Id, state);
        ReplacePhoto(photo with { PickState = state });
        Status = state == PickState.Pick ? "Marked as pick" : "Marked as reject (original is untouched)";
    }

    private async Task ResetEditAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null) return;
        await _catalog.UpdateEditAsync(photo.Id, EditRecipe.Default);
        ReplacePhoto(photo with { Edit = EditRecipe.Default });
        _syncingSelection = true;
        Exposure = 0;
        _syncingSelection = false;
        await RenderSelectedAsync();
    }

    private async Task AnalyzeSelectedAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null) return;
        IsBusy = true;
        Status = $"Analyzing with {_analysisProvider.Name}…";
        try
        {
            var preview = await _renderer.RenderPreviewAsync(photo.OriginalPath, photo.Edit, 1280);
            var analysis = await _analysisProvider.AnalyzeAsync(photo, preview.Data);
            await _catalog.UpdateAnalysisAsync(photo.Id, analysis);
            ReplacePhoto(photo with { AiSummary = analysis.Summary });
            Status = $"AI: {analysis.Summary}";
        }
        catch (Exception exception)
        {
            Status = $"Local AI unavailable: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplacePhoto(PhotoAsset updated)
    {
        var index = Photos.IndexOf(Photos.First(photo => photo.Id == updated.Id));
        Photos[index] = updated;
        _selectedPhoto = updated;
        OnPropertyChanged(nameof(SelectedPhoto));
    }

    private void NotifyCommands()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        PickCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        ResetEditCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        Preview = null;
        if (_analysisProvider is IDisposable disposable) disposable.Dispose();
        await _catalog.DisposeAsync();
    }
}

