using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Imaging;

namespace OpenLume.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private const int PageSize = 200;
    private readonly IPhotoCatalog _catalog;
    private readonly IImageRenderer _renderer;
    private readonly IPhotoAnalysisProvider _analysisProvider;
    private readonly IPresetImporter _presetImporter;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly MetadataIndexingService _metadataIndexer;
    private LibraryPhotoItemViewModel? _selectedItem;
    private PhotoAsset? _selectedPhoto;
    private Bitmap? _preview;
    private Bitmap? _secondaryPreview;
    private string _status = "Starting OpenLume…";
    private string _searchText = string.Empty;
    private string _newCollectionName = string.Empty;
    private double _exposure;
    private int _rating;
    private int _minimumRating;
    private int _pageIndex;
    private int _totalCount;
    private int _missingCount;
    private int _indexedCount;
    private bool _isBusy;
    private bool _isIndexing;
    private bool _picksOnly;
    private bool _missingOnly;
    private bool _syncingSelection;
    private string _presentation = "Library";
    private CatalogFolder? _selectedFolder;
    private PhotoSet? _selectedCollection;
    private PhotoStackGroup? _selectedStack;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _queryCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _indexCancellation;
    private Task _filterTask = Task.CompletedTask;
    private Task _thumbnailTask = Task.CompletedTask;
    private Task _indexTask = Task.CompletedTask;
    private bool _disposed;

    public MainWindowViewModel(
        IPhotoCatalog catalog,
        IImageRenderer renderer,
        IPhotoAnalysisProvider analysisProvider,
        IPresetImporter presetImporter,
        ThumbnailCache thumbnailCache,
        MetadataIndexingService metadataIndexer)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _analysisProvider = analysisProvider ?? throw new ArgumentNullException(nameof(analysisProvider));
        _presetImporter = presetImporter ?? throw new ArgumentNullException(nameof(presetImporter));
        _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        _metadataIndexer = metadataIndexer ?? throw new ArgumentNullException(nameof(metadataIndexer));

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, () => SelectedPhoto is not null && !IsBusy);
        PickCommand = new AsyncRelayCommand(() => SetPickStateAsync(PickState.Pick), () => SelectedPhoto is not null);
        RejectCommand = new AsyncRelayCommand(() => SetPickStateAsync(PickState.Reject), () => SelectedPhoto is not null);
        ResetEditCommand = new AsyncRelayCommand(ResetEditAsync, () => SelectedPhoto is not null);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => IsBusy || IsIndexing);
        PreviousPageCommand = new AsyncRelayCommand(
            () => ChangePageAsync(-1), () => PageIndex > 0 && !IsBusy);
        NextPageCommand = new AsyncRelayCommand(
            () => ChangePageAsync(1), () => (PageIndex + 1) * PageSize < TotalCount && !IsBusy);
        CompareCommand = new AsyncRelayCommand(
            ShowCompareAsync, () => SelectedItems.Count == 2 && !IsBusy);
        SurveyCommand = new RelayCommand(
            ShowSurvey, () => SelectedItems.Count > 1);
        ReturnToLibraryCommand = new RelayCommand(() => Presentation = "Library");
        CreateCollectionCommand = new AsyncRelayCommand(CreateCollectionAsync, () => !string.IsNullOrWhiteSpace(NewCollectionName));
        AddToCollectionCommand = new AsyncRelayCommand(
            AddSelectionToCollectionAsync,
            () => SelectedCollection is not null && SelectedItems.Count > 0);
        CreateStackCommand = new AsyncRelayCommand(
            CreateStackFromSelectionAsync,
            () => SelectedItems.Count > 1);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
    }

    public ObservableCollection<LibraryPhotoItemViewModel> LibraryItems { get; } = new();
    public ObservableCollection<LibraryPhotoItemViewModel> SelectedItems { get; } = new();
    public ObservableCollection<CatalogFolder> Folders { get; } = new();
    public ObservableCollection<PhotoSet> Collections { get; } = new();
    public ObservableCollection<PhotoStackGroup> Stacks { get; } = new();

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand PickCommand { get; }
    public IAsyncRelayCommand RejectCommand { get; }
    public IAsyncRelayCommand ResetEditCommand { get; }
    public IRelayCommand CancelOperationCommand { get; }
    public IAsyncRelayCommand PreviousPageCommand { get; }
    public IAsyncRelayCommand NextPageCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IRelayCommand SurveyCommand { get; }
    public IRelayCommand ReturnToLibraryCommand { get; }
    public IAsyncRelayCommand CreateCollectionCommand { get; }
    public IAsyncRelayCommand AddToCollectionCommand { get; }
    public IAsyncRelayCommand CreateStackCommand { get; }
    public IAsyncRelayCommand ClearFiltersCommand { get; }

    public LibraryPhotoItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            SelectedPhoto = value?.Asset;
        }
    }

    public PhotoAsset? SelectedPhoto
    {
        get => _selectedPhoto;
        private set
        {
            if (!SetProperty(ref _selectedPhoto, value))
            {
                return;
            }

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
        private set => ReplaceBitmap(ref _preview, value, nameof(Preview));
    }

    public Bitmap? SecondaryPreview
    {
        get => _secondaryPreview;
        private set => ReplaceBitmap(ref _secondaryPreview, value, nameof(SecondaryPreview));
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _filterTask = DebouncedFilterAsync();
            }
        }
    }

    public string NewCollectionName
    {
        get => _newCollectionName;
        set
        {
            if (SetProperty(ref _newCollectionName, value))
            {
                CreateCollectionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int MinimumRating
    {
        get => _minimumRating;
        set
        {
            if (SetProperty(ref _minimumRating, Math.Clamp(value, 0, 5)))
            {
                _filterTask = ResetAndReloadAsync();
            }
        }
    }

    public bool PicksOnly
    {
        get => _picksOnly;
        set
        {
            if (SetProperty(ref _picksOnly, value))
            {
                _filterTask = ResetAndReloadAsync();
            }
        }
    }

    public bool MissingOnly
    {
        get => _missingOnly;
        set
        {
            if (SetProperty(ref _missingOnly, value))
            {
                _filterTask = RefreshMissingAndReloadAsync(value);
            }
        }
    }

    public CatalogFolder? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value))
            {
                return;
            }

            if (value is not null)
            {
                _selectedCollection = null;
                _selectedStack = null;
                OnPropertyChanged(nameof(SelectedCollection));
                OnPropertyChanged(nameof(SelectedStack));
            }

            _filterTask = ResetAndReloadAsync();
        }
    }

    public PhotoSet? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (!SetProperty(ref _selectedCollection, value))
            {
                return;
            }

            if (value is not null)
            {
                _selectedFolder = null;
                _selectedStack = null;
                OnPropertyChanged(nameof(SelectedFolder));
                OnPropertyChanged(nameof(SelectedStack));
            }

            AddToCollectionCommand.NotifyCanExecuteChanged();
            _filterTask = ResetAndReloadAsync();
        }
    }

    public PhotoStackGroup? SelectedStack
    {
        get => _selectedStack;
        set
        {
            if (!SetProperty(ref _selectedStack, value))
            {
                return;
            }

            if (value is not null)
            {
                _selectedFolder = null;
                _selectedCollection = null;
                OnPropertyChanged(nameof(SelectedFolder));
                OnPropertyChanged(nameof(SelectedCollection));
            }

            _filterTask = ResetAndReloadAsync();
        }
    }

    public double Exposure
    {
        get => _exposure;
        set
        {
            if (!SetProperty(ref _exposure, Math.Clamp(value, -5, 5)) || _syncingSelection)
            {
                return;
            }

            _ = ApplyExposureAsync();
        }
    }

    public int Rating
    {
        get => _rating;
        set
        {
            if (!SetProperty(ref _rating, Math.Clamp(value, 0, 5)) || _syncingSelection)
            {
                return;
            }

            _ = ApplyRatingAsync();
        }
    }

    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                NotifyCommands();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                NotifyCommands();
            }
        }
    }

    public int MissingCount
    {
        get => _missingCount;
        private set
        {
            if (SetProperty(ref _missingCount, value))
            {
                OnPropertyChanged(nameof(HasMissingFiles));
            }
        }
    }

    public bool HasMissingFiles => MissingCount > 0;

    public int IndexedCount
    {
        get => _indexedCount;
        private set => SetProperty(ref _indexedCount, value);
    }

    public string PageSummary => TotalCount == 0
        ? "0 photos"
        : $"{(PageIndex * PageSize) + 1}–{Math.Min((PageIndex + 1) * PageSize, TotalCount)} of {TotalCount:N0}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                NotifyCommands();
            }
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (SetProperty(ref _isIndexing, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                NotifyCommands();
            }
        }
    }

    public bool IsWorking => IsBusy || IsIndexing;

    public string Presentation
    {
        get => _presentation;
        private set
        {
            if (SetProperty(ref _presentation, value))
            {
                OnPropertyChanged(nameof(IsLibraryPresentation));
                OnPropertyChanged(nameof(IsComparePresentation));
                OnPropertyChanged(nameof(IsSurveyPresentation));
            }
        }
    }

    public bool IsLibraryPresentation => Presentation == "Library";

    public bool IsComparePresentation => Presentation == "Compare";

    public bool IsSurveyPresentation => Presentation == "Survey";

    public async Task InitializeAsync()
    {
        await _catalog.InitializeAsync();
        await RefreshOrganizationAsync();
        var missing = await _catalog.FindMissingAsync();
        MissingCount = missing.Count;
        await ReloadAsync();
        StartMetadataIndexing();
        var ollamaAvailable = await _analysisProvider.IsAvailableAsync();
        Status = ollamaAvailable
            ? $"Ready · {_analysisProvider.Name} available"
            : $"Ready · {_analysisProvider.Name} model not installed";
    }

    public async Task ImportFolderAsync(string folder)
    {
        BeginOperation($"Importing {folder}…");
        try
        {
            var result = await _catalog.ImportFolderAsync(
                folder, includeSubfolders: true, _operationCancellation!.Token);
            await RefreshOrganizationAsync(_operationCancellation.Token);
            await ReloadAsync(cancellationToken: _operationCancellation.Token);
            StartMetadataIndexing();
            Status = $"Imported {result.Imported}; already present {result.AlreadyPresent}; failed {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            Status = "Import cancelled; completed files remain safely cataloged.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Import failed: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task RelinkSelectedAsync(string replacementPath)
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        try
        {
            if (!await _catalog.RelinkAsync(photo.Id, replacementPath))
            {
                Status = "Relink failed: that file is already referenced by another catalog photo.";
                return;
            }

            await _catalog.FindMissingAsync();
            await RefreshOrganizationAsync();
            await ReloadAsync(photo.Id);
            StartMetadataIndexing();
            Status = $"Relinked {Path.GetFileName(replacementPath)} without changing edits or ratings.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = $"Relink failed: {exception.Message}";
        }
    }

    public async Task ExportSelectedAsync(string destinationPath)
    {
        if (SelectedPhoto is null)
        {
            return;
        }

        BeginOperation("Exporting JPEG…");
        try
        {
            await _renderer.ExportJpegAsync(
                SelectedPhoto.OriginalPath,
                destinationPath,
                SelectedPhoto.Edit,
                92,
                _operationCancellation!.Token);
            Status = $"Exported {Path.GetFileName(destinationPath)}";
        }
        catch (OperationCanceledException)
        {
            Status = "Export cancelled.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Export failed: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ImportPresetAsync(string presetPath)
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        try
        {
            var file = new FileInfo(presetPath);
            if (file.Length > 1_000_000)
            {
                throw new InvalidDataException("Preset is larger than the 1 MB safety limit.");
            }

            var result = _presetImporter.Import(await File.ReadAllTextAsync(presetPath));
            if (!result.Success)
            {
                Status = result.Warnings.Count > 0 ? result.Warnings[0] : "The XMP preset could not be imported.";
                return;
            }

            await _catalog.UpdateEditAsync(photo.Id, result.Recipe);
            ReplacePhoto(photo with { Edit = result.Recipe });
            _syncingSelection = true;
            Exposure = result.Recipe.ExposureEv;
            _syncingSelection = false;
            await RenderSelectedAsync();
            var compatibility = result.UnsupportedParameters.Count == 0
                ? "all recognized settings applied"
                : $"{result.UnsupportedParameters.Count} unsupported setting(s) reported";
            Status = $"Preset {result.Name ?? Path.GetFileNameWithoutExtension(presetPath)}: {compatibility}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Preset import failed: {exception.Message}";
        }
    }

    public void SetSelectedItems(IEnumerable<LibraryPhotoItemViewModel> items)
    {
        SelectedItems.Clear();
        foreach (var item in items.Take(20))
        {
            SelectedItems.Add(item);
        }

        CompareCommand.NotifyCanExecuteChanged();
        SurveyCommand.NotifyCanExecuteChanged();
        AddToCollectionCommand.NotifyCanExecuteChanged();
        CreateStackCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAndDispose(ref _previewCancellation);
        CancelAndDispose(ref _operationCancellation);
        CancelAndDispose(ref _filterCancellation);
        CancelAndDispose(ref _queryCancellation);
        CancelAndDispose(ref _thumbnailCancellation);
        CancelAndDispose(ref _indexCancellation);
        await AwaitBackgroundTaskAsync(_filterTask);
        await AwaitBackgroundTaskAsync(_thumbnailTask);
        await AwaitBackgroundTaskAsync(_indexTask);
        DisposeLibraryItems();
        Preview = null;
        SecondaryPreview = null;
        _thumbnailCache.Dispose();
        if (_analysisProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        await _catalog.DisposeAsync();
    }

    private CatalogFilter BuildFilter(int offset) => new(
        MinimumRating: MinimumRating == 0 ? null : MinimumRating,
        PickState: PicksOnly ? PickState.Pick : null,
        Missing: MissingOnly ? true : null,
        SearchText: SearchText,
        CollectionId: SelectedCollection?.Id,
        StackId: SelectedStack?.Id,
        Offset: offset,
        Limit: PageSize,
        Folder: SelectedFolder?.Path);

    private async Task ReloadAsync(Guid? selectId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelAndDispose(ref _queryCancellation);
        _queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _queryCancellation.Token;
        selectId ??= SelectedPhoto?.Id;
        var filter = BuildFilter(PageIndex * PageSize);
        var total = await _catalog.GetPhotoCountAsync(filter, token);
        var assets = await _catalog.QueryAsync(filter, token);
        token.ThrowIfCancellationRequested();

        TotalCount = total;
        DisposeLibraryItems();
        foreach (var asset in assets)
        {
            LibraryItems.Add(new LibraryPhotoItemViewModel(asset));
        }

        SelectedItem = LibraryItems.FirstOrDefault(item => item.Id == selectId) ?? LibraryItems.FirstOrDefault();
        SetSelectedItems(SelectedItem is null ? [] : [SelectedItem]);
        Presentation = "Library";
        SecondaryPreview = null;
        StartThumbnailLoading();
    }

    private async Task RefreshOrganizationAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _catalog.GetFoldersAsync(cancellationToken);
        var collections = await _catalog.GetCollectionsAsync(cancellationToken);
        var stacks = await _catalog.GetStacksAsync(cancellationToken);
        Folders.Clear();
        Collections.Clear();
        Stacks.Clear();
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        foreach (var collection in collections)
        {
            Collections.Add(collection);
        }

        foreach (var stack in stacks)
        {
            Stacks.Add(stack);
        }
    }

    private void StartThumbnailLoading()
    {
        CancelAndDispose(ref _thumbnailCancellation);
        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;
        var items = LibraryItems.ToArray();
        _thumbnailTask = LoadThumbnailsAsync(items, token);
    }

    private async Task LoadThumbnailsAsync(
        IReadOnlyCollection<LibraryPhotoItemViewModel> items,
        CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(4, 4);
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    await item.LoadThumbnailAsync(_thumbnailCache, cancellationToken);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StartMetadataIndexing()
    {
        CancelAndDispose(ref _indexCancellation);
        _indexCancellation = new CancellationTokenSource();
        var token = _indexCancellation.Token;
        _indexTask = RunMetadataIndexingAsync(token);
    }

    private async Task RunMetadataIndexingAsync(CancellationToken cancellationToken)
    {
        IsIndexing = true;
        IndexedCount = 0;
        try
        {
            var progress = new Progress<MetadataIndexProgress>(value =>
            {
                IndexedCount = value.Processed;
                Status = $"Indexing metadata… {value.Processed} processed, {value.Failed} failed";
            });
            await _metadataIndexer.IndexPendingAsync(progress, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Status = $"Metadata index current · {IndexedCount} processed";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Metadata indexing paused; it will resume next time.";
        }
        catch (OperationCanceledException)
        {
            // A newer library query replaced the refresh after indexing; metadata is already durable.
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private async Task RenderSelectedAsync()
    {
        CancelAndDispose(ref _previewCancellation);
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;
        var photo = SelectedPhoto;
        if (photo is null)
        {
            Preview = null;
            return;
        }

        if (photo.IsMissing)
        {
            Preview = null;
            Status = $"{photo.FileName} is missing. Use Relink to locate the original.";
            return;
        }

        try
        {
            var rendered = await _renderer.RenderPreviewAsync(photo.OriginalPath, photo.Edit, 1800, token);
            token.ThrowIfCancellationRequested();
            Preview = CreateBitmap(rendered.Data);
            Status = $"{photo.FileName} · {rendered.Width}×{rendered.Height}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Preview unavailable: {exception.Message}";
            Preview = null;
        }
    }

    private async Task ShowCompareAsync()
    {
        if (SelectedItems.Count != 2)
        {
            return;
        }

        BeginOperation("Building compare view…");
        try
        {
            var token = _operationCancellation!.Token;
            var first = SelectedItems[0].Asset;
            var second = SelectedItems[1].Asset;
            if (first.IsMissing || second.IsMissing)
            {
                SecondaryPreview = null;
                Presentation = "Library";
                Status = "Compare requires both originals to be available. Relink missing files first.";
                return;
            }
            var renders = await Task.WhenAll(
                _renderer.RenderPreviewAsync(first.OriginalPath, first.Edit, 1600, token),
                _renderer.RenderPreviewAsync(second.OriginalPath, second.Edit, 1600, token));
            Preview = CreateBitmap(renders[0].Data);
            SecondaryPreview = CreateBitmap(renders[1].Data);
            Presentation = "Compare";
            Status = $"Comparing {first.FileName} and {second.FileName}";
        }
        catch (OperationCanceledException)
        {
            Status = "Compare cancelled.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SecondaryPreview = null;
            Presentation = "Library";
            Status = $"Compare unavailable: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void ShowSurvey()
    {
        if (SelectedItems.Count > 1)
        {
            Presentation = "Survey";
            Status = $"Surveying {SelectedItems.Count} photos";
        }
    }

    private async Task ApplyExposureAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        var edit = (photo.Edit with { ExposureEv = Exposure }).Normalize();
        await _catalog.UpdateEditAsync(photo.Id, edit);
        ReplacePhoto(photo with { Edit = edit });
        await RenderSelectedAsync();
    }

    private async Task ApplyRatingAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        await _catalog.UpdateRatingAsync(photo.Id, Rating);
        ReplacePhoto(photo with { Rating = Rating });
    }

    private async Task SetPickStateAsync(PickState state)
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        await _catalog.UpdatePickStateAsync(photo.Id, state);
        ReplacePhoto(photo with { PickState = state });
        Status = state == PickState.Pick ? "Marked as pick" : "Marked as reject; the original is untouched";
    }

    private async Task ResetEditAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

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
        if (photo is null)
        {
            return;
        }

        BeginOperation($"Analyzing with {_analysisProvider.Name}…");
        try
        {
            var token = _operationCancellation!.Token;
            var preview = await _renderer.RenderPreviewAsync(photo.OriginalPath, photo.Edit, 1280, token);
            var analysis = await _analysisProvider.AnalyzeAsync(photo, preview.Data, token);
            await _catalog.UpdateAnalysisAsync(photo.Id, analysis, token);
            ReplacePhoto(photo with { AiSummary = analysis.Summary });
            Status = $"AI: {analysis.Summary}";
        }
        catch (OperationCanceledException)
        {
            Status = "AI analysis cancelled.";
        }
        catch (Exception exception)
        {
            Status = $"Local AI unavailable: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task CreateCollectionAsync()
    {
        try
        {
            var id = await _catalog.CreateCollectionAsync(NewCollectionName);
            NewCollectionName = string.Empty;
            await RefreshOrganizationAsync();
            SelectedCollection = Collections.First(collection => collection.Id == id);
            Status = "Collection created.";
        }
        catch (Exception exception)
        {
            Status = $"Could not create collection: {exception.Message}";
        }
    }

    private async Task AddSelectionToCollectionAsync()
    {
        var collection = SelectedCollection;
        if (collection is null)
        {
            return;
        }

        foreach (var item in SelectedItems)
        {
            await _catalog.SetCollectionMembershipAsync(collection.Id, item.Id, included: true);
        }

        await RefreshOrganizationAsync();
        Status = $"Added {SelectedItems.Count} photo(s) to {collection.Name}.";
    }

    private async Task CreateStackFromSelectionAsync()
    {
        if (SelectedItems.Count < 2)
        {
            return;
        }

        var id = await _catalog.CreateStackAsync($"Stack {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        await _catalog.SetStackMembershipAsync(id, SelectedItems.Select(item => item.Id).ToArray());
        await RefreshOrganizationAsync();
        Status = $"Created a {SelectedItems.Count}-photo stack.";
    }

    private async Task ClearFiltersAsync()
    {
        _searchText = string.Empty;
        _minimumRating = 0;
        _picksOnly = false;
        _missingOnly = false;
        _selectedFolder = null;
        _selectedCollection = null;
        _selectedStack = null;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(MinimumRating));
        OnPropertyChanged(nameof(PicksOnly));
        OnPropertyChanged(nameof(MissingOnly));
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(SelectedStack));
        PageIndex = 0;
        await ReloadAsync();
    }

    private async Task DebouncedFilterAsync()
    {
        CancelAndDispose(ref _filterCancellation);
        _filterCancellation = new CancellationTokenSource();
        var token = _filterCancellation.Token;
        try
        {
            await Task.Delay(250, token);
            PageIndex = 0;
            await ReloadAsync(cancellationToken: token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ResetAndReloadAsync()
    {
        PageIndex = 0;
        try
        {
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Status = $"Library filter failed: {exception.Message}";
        }
    }

    private async Task RefreshMissingAndReloadAsync(bool refreshMissing)
    {
        try
        {
            if (refreshMissing)
            {
                MissingCount = (await _catalog.FindMissingAsync()).Count;
            }

            await ResetAndReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ChangePageAsync(int delta)
    {
        try
        {
            PageIndex = Math.Max(0, PageIndex + delta);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReplacePhoto(PhotoAsset updated)
    {
        var item = LibraryItems.FirstOrDefault(candidate => candidate.Id == updated.Id);
        item?.Update(updated);
        _selectedPhoto = updated;
        OnPropertyChanged(nameof(SelectedPhoto));
    }

    private void BeginOperation(string status)
    {
        CancelAndDispose(ref _operationCancellation);
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        Status = status;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        IsBusy = false;
    }

    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        _indexCancellation?.Cancel();
        Status = "Cancelling…";
    }

    private void NotifyCommands()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        PickCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        ResetEditCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
    }

    private void DisposeLibraryItems()
    {
        foreach (var item in LibraryItems)
        {
            item.Dispose();
        }

        LibraryItems.Clear();
        SelectedItems.Clear();
    }

    private void ReplaceBitmap(ref Bitmap? field, Bitmap? value, string propertyName)
    {
        var previous = field;
        if (SetProperty(ref field, value, propertyName))
        {
            previous?.Dispose();
        }
    }

    private static Bitmap CreateBitmap(byte[] data) =>
        new(new MemoryStream(data, writable: false));

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private static async Task AwaitBackgroundTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
