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
    private const int PageSize = 80;
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
    private ImageHistogram? _histogram;
    private string _status = "Starting OpenLume…";
    private string _searchText = string.Empty;
    private string _newCollectionName = string.Empty;
    private double _exposure;
    private double _contrast;
    private double _saturation;
    private double _temperature;
    private double _tint;
    private double _rotationDegrees;
    private double _highlights;
    private double _shadows;
    private double _whites;
    private double _blacks;
    private double _vibrance;
    private double _vignette;
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
    private CancellationTokenSource? _editCancellation;
    private Task _filterTask = Task.CompletedTask;
    private Task _thumbnailTask = Task.CompletedTask;
    private Task _indexTask = Task.CompletedTask;
    private Task _editTask = Task.CompletedTask;
    private Task _previewTask = Task.CompletedTask;
    private bool _disposed;
    private bool _showBefore;
    private bool _isPreviewingAiSuggestion;
    private EditHistory? _editHistory;
    private string _newSnapshotName = string.Empty;

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
        PreviewAiSuggestionCommand = new RelayCommand(
            ToggleAiSuggestionPreview,
            () => HasPendingAiSuggestion && !IsBusy);
        ApplyAiSuggestionCommand = new AsyncRelayCommand(
            ApplyAiSuggestionAsync,
            () => HasPendingAiSuggestion && !IsBusy);
        RejectAiSuggestionCommand = new AsyncRelayCommand(
            RejectAiSuggestionAsync,
            () => HasPendingAiSuggestion && !IsBusy);
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
        ShowDevelopCommand = new RelayCommand(
            () => Presentation = "Develop",
            () => SelectedPhoto is not null);
        CreateCollectionCommand = new AsyncRelayCommand(CreateCollectionAsync, () => !string.IsNullOrWhiteSpace(NewCollectionName));
        AddToCollectionCommand = new AsyncRelayCommand(
            AddSelectionToCollectionAsync,
            () => SelectedCollection is not null && SelectedItems.Count > 0);
        CreateStackCommand = new AsyncRelayCommand(
            CreateStackFromSelectionAsync,
            () => SelectedItems.Count > 1);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => EditHistory?.CanUndo == true && !IsBusy);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => EditHistory?.CanRedo == true && !IsBusy);
        CreateSnapshotCommand = new AsyncRelayCommand(
            CreateSnapshotAsync,
            () => SelectedPhoto is not null && !string.IsNullOrWhiteSpace(NewSnapshotName) && !IsBusy);
        RestoreSnapshotCommand = new AsyncRelayCommand<EditSnapshot>(
            RestoreSnapshotAsync,
            snapshot => snapshot is not null && !IsBusy);
    }

    public ObservableCollection<LibraryPhotoItemViewModel> LibraryItems { get; } = new();
    public ObservableCollection<LibraryPhotoItemViewModel> SelectedItems { get; } = new();
    public ObservableCollection<CatalogFolder> Folders { get; } = new();
    public ObservableCollection<PhotoSet> Collections { get; } = new();
    public ObservableCollection<PhotoStackGroup> Stacks { get; } = new();
    public ObservableCollection<EditRevision> EditRevisions { get; } = new();
    public ObservableCollection<EditSnapshot> EditSnapshots { get; } = new();

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IRelayCommand PreviewAiSuggestionCommand { get; }
    public IAsyncRelayCommand ApplyAiSuggestionCommand { get; }
    public IAsyncRelayCommand RejectAiSuggestionCommand { get; }
    public IAsyncRelayCommand PickCommand { get; }
    public IAsyncRelayCommand RejectCommand { get; }
    public IAsyncRelayCommand ResetEditCommand { get; }
    public IRelayCommand CancelOperationCommand { get; }
    public IAsyncRelayCommand PreviousPageCommand { get; }
    public IAsyncRelayCommand NextPageCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IRelayCommand SurveyCommand { get; }
    public IRelayCommand ReturnToLibraryCommand { get; }
    public IRelayCommand ShowDevelopCommand { get; }
    public IAsyncRelayCommand CreateCollectionCommand { get; }
    public IAsyncRelayCommand AddToCollectionCommand { get; }
    public IAsyncRelayCommand CreateStackCommand { get; }
    public IAsyncRelayCommand ClearFiltersCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand RedoCommand { get; }
    public IAsyncRelayCommand CreateSnapshotCommand { get; }
    public IAsyncRelayCommand<EditSnapshot> RestoreSnapshotCommand { get; }
    public string NewSnapshotName { get => _newSnapshotName; set { if (SetProperty(ref _newSnapshotName, value)) CreateSnapshotCommand.NotifyCanExecuteChanged(); } }
    public EditHistory? EditHistory { get => _editHistory; private set { if (SetProperty(ref _editHistory, value)) { UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); } } }
    public bool ShowBefore
    {
        get => _showBefore;
        set
        {
            if (SetProperty(ref _showBefore, value))
            {
                _previewTask = RenderSelectedAsync();
            }
        }
    }

    public DevelopSuggestion? AiSuggestion => SelectedPhoto?.AiSuggestion;

    public bool HasPendingAiSuggestion => AiSuggestion?.Status == DevelopSuggestionStatus.Pending;

    public string AiSuggestionConfidence => AiSuggestion is null
        ? string.Empty
        : $"{AiSuggestion.Confidence:P0} confidence";

    public string AiPreviewLabel => IsPreviewingAiSuggestion ? "Show active edit" : "Preview proposal";

    public IReadOnlyList<EditParameterDecision> AiSuggestionDecisions => AiSuggestion?.Decisions ?? [];

    public IReadOnlyList<DevelopParameterChangeViewModel> AiParameterChanges => BuildAiParameterChanges();

    public IReadOnlyList<string> AiSuggestionWarnings => AiSuggestion?.Warnings ?? [];

    public bool IsPreviewingAiSuggestion
    {
        get => _isPreviewingAiSuggestion;
        private set
        {
            if (SetProperty(ref _isPreviewingAiSuggestion, value))
            {
                OnPropertyChanged(nameof(AiPreviewLabel));
                _previewTask = RenderSelectedAsync();
            }
        }
    }

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

            CancelAndDispose(ref _editCancellation);
            _isPreviewingAiSuggestion = false;
            OnPropertyChanged(nameof(IsPreviewingAiSuggestion));
            OnPropertyChanged(nameof(AiPreviewLabel));
            SyncEditorFromRecipe(value?.Edit ?? EditRecipe.Default);
            _syncingSelection = true;
            Rating = value?.Rating ?? 0;
            _syncingSelection = false;
            NotifyCommands();
            NotifyAiSuggestionChanged();
            _ = RefreshEditHistoryAsync(value?.Id);
            _previewTask = RenderSelectedAsync();
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

    public ImageHistogram? Histogram
    {
        get => _histogram;
        private set => SetProperty(ref _histogram, value);
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

            ScheduleEditUpdate();
        }
    }

    public double Contrast
    {
        get => _contrast;
        set
        {
            if (SetProperty(ref _contrast, Math.Clamp(value, -100, 100)) && !_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    public double Saturation
    {
        get => _saturation;
        set
        {
            if (SetProperty(ref _saturation, Math.Clamp(value, -100, 100)) && !_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    public double Temperature
    {
        get => _temperature;
        set
        {
            if (SetProperty(ref _temperature, Math.Clamp(value, -100, 100)) && !_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    public double Tint
    {
        get => _tint;
        set
        {
            if (SetProperty(ref _tint, Math.Clamp(value, -100, 100)) && !_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            if (SetProperty(ref _rotationDegrees, Math.Clamp(value, -45, 45)) && !_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    public double Highlights
    {
        get => _highlights;
        set => SetDevelopValue(ref _highlights, value);
    }

    public double Shadows
    {
        get => _shadows;
        set => SetDevelopValue(ref _shadows, value);
    }

    public double Whites
    {
        get => _whites;
        set => SetDevelopValue(ref _whites, value);
    }

    public double Blacks
    {
        get => _blacks;
        set => SetDevelopValue(ref _blacks, value);
    }

    public double Vibrance
    {
        get => _vibrance;
        set => SetDevelopValue(ref _vibrance, value);
    }

    public double Vignette
    {
        get => _vignette;
        set => SetDevelopValue(ref _vignette, value);
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
                OnPropertyChanged(nameof(IsDevelopPresentation));
                OnPropertyChanged(nameof(IsComparePresentation));
                OnPropertyChanged(nameof(IsSurveyPresentation));
            }
        }
    }

    public bool IsLibraryPresentation => Presentation == "Library";

    public bool IsDevelopPresentation => Presentation == "Develop";

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
            var progress = new Progress<ImportProgress>(value =>
            {
                Status = $"Importing… {value.Imported:N0} new · {value.AlreadyPresent:N0} already present";
            });
            var result = await _catalog.ImportFolderAsync(
                folder,
                includeSubfolders: true,
                progress,
                _operationCancellation!.Token);
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
            SyncEditorFromRecipe(result.Recipe);
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
        CancelAndDispose(ref _editCancellation);
        await AwaitBackgroundTaskAsync(_filterTask);
        await AwaitBackgroundTaskAsync(_thumbnailTask);
        await AwaitBackgroundTaskAsync(_indexTask);
        await AwaitBackgroundTaskAsync(_editTask);
        await AwaitBackgroundTaskAsync(_previewTask);
        DisposeLibraryItems();
        Preview = null;
        SecondaryPreview = null;
        _thumbnailCache.Dispose();
        if (_renderer is IDisposable rendererDisposable)
        {
            rendererDisposable.Dispose();
        }

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
        using var concurrency = new SemaphoreSlim(2, 2);
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
        finally
        {
            try
            {
                await _thumbnailCache.FlushAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Status = $"Unable to save thumbnail cache index: {exception.Message}";
            }
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
            Histogram = null;
            return;
        }

        if (photo.IsMissing)
        {
            Preview = null;
            Histogram = null;
            Status = $"{photo.FileName} is missing. Use Relink to locate the original.";
            return;
        }

        try
        {
            var recipe = ShowBefore
                ? EditRecipe.Default
                : IsPreviewingAiSuggestion && photo.AiSuggestion?.Status == DevelopSuggestionStatus.Pending
                    ? photo.AiSuggestion.MergeOnto(photo.Edit)
                    : photo.Edit;
            var rendered = await _renderer.RenderPreviewAsync(photo.OriginalPath, recipe, 1800, token);
            token.ThrowIfCancellationRequested();
            Preview = CreateBitmap(rendered.Data);
            Histogram = ImageHistogramCalculator.Calculate(rendered.Data);
            Status = $"{photo.FileName} · {rendered.Width}×{rendered.Height}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Preview unavailable: {exception.Message}";
            Preview = null;
            Histogram = null;
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

    private void ScheduleEditUpdate()
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        CancelAndDispose(ref _editCancellation);
        _editCancellation = new CancellationTokenSource();
        var edit = (photo.Edit with
        {
            ExposureEv = Exposure,
            Contrast = Contrast,
            Saturation = Saturation,
            Temperature = Temperature,
            Tint = Tint,
            RotationDegrees = RotationDegrees,
            Highlights = Highlights,
            Shadows = Shadows,
            Whites = Whites,
            Blacks = Blacks,
            Vibrance = Vibrance,
            Vignette = Vignette
        }).Normalize();
        _editTask = ApplyEditAsync(photo.Id, edit, _editCancellation.Token);
    }

    private async Task ApplyEditAsync(Guid photoId, EditRecipe edit, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            var photo = SelectedPhoto;
            if (photo?.Id != photoId)
            {
                return;
            }

            await _catalog.UpdateEditAsync(photo.Id, edit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplacePhoto(photo with { Edit = edit });
            await RenderSelectedAsync();
            await RefreshEditHistoryAsync(photo.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Unable to save edit: {exception.Message}";
        }
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
        await AwaitBackgroundTaskAsync(_editTask);
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        await _catalog.UpdateEditAsync(photo.Id, EditRecipe.Default);
        ReplacePhoto(photo with { Edit = EditRecipe.Default });
        SyncEditorFromRecipe(EditRecipe.Default);
        await RenderSelectedAsync();
        await RefreshEditHistoryAsync(photo.Id);
    }

    private async Task RefreshEditHistoryAsync(Guid? id)
    {
        EditHistory = null;
        EditRevisions.Clear();
        EditSnapshots.Clear();
        if (id is not Guid photoId || _disposed)
        {
            return;
        }

        try
        {
            var history = await _catalog.GetEditHistoryAsync(photoId);
            var snapshots = await _catalog.GetEditSnapshotsAsync(photoId);
            if (_disposed || SelectedPhoto?.Id != photoId)
            {
                return;
            }

            EditHistory = history;
            foreach (var revision in history.Revisions)
            {
                EditRevisions.Add(revision);
            }

            foreach (var snapshot in snapshots)
            {
                EditSnapshots.Add(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            Status = $"Unable to load edit history: {exception.Message}";
        }
    }

    private async Task UndoAsync() =>
        await ApplyHistoryRecipeAsync(() => _catalog.UndoEditAsync(SelectedPhoto!.Id));

    private async Task RedoAsync() =>
        await ApplyHistoryRecipeAsync(() => _catalog.RedoEditAsync(SelectedPhoto!.Id));

    private async Task ApplyHistoryRecipeAsync(Func<Task<EditRecipe?>> operation)
    {
        await AwaitBackgroundTaskAsync(_editTask);
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        BeginOperation("Updating edit history…");
        try
        {
            var recipe = await operation();
            if (recipe is null)
            {
                return;
            }

            ReplacePhoto(photo with { Edit = recipe });
            SyncEditorFromRecipe(recipe);
            await RefreshEditHistoryAsync(photo.Id);
            await RenderSelectedAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Edit history update cancelled.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            Status = $"Unable to update edit history: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task CreateSnapshotAsync()
    {
        await AwaitBackgroundTaskAsync(_editTask);
        var photo = SelectedPhoto;
        var name = NewSnapshotName.Trim();
        if (photo is null || name.Length == 0)
        {
            return;
        }

        BeginOperation("Saving snapshot…");
        try
        {
            await _catalog.CreateEditSnapshotAsync(photo.Id, name);
            NewSnapshotName = string.Empty;
            await RefreshEditHistoryAsync(photo.Id);
            Status = $"Snapshot created: {name}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            Status = $"Unable to create snapshot: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreSnapshotAsync(EditSnapshot? snapshot)
    {
        await AwaitBackgroundTaskAsync(_editTask);
        var photo = SelectedPhoto;
        if (photo is null || snapshot is null)
        {
            return;
        }

        BeginOperation("Restoring snapshot…");
        try
        {
            var recipe = await _catalog.RestoreEditSnapshotAsync(photo.Id, snapshot.Id);
            ReplacePhoto(photo with { Edit = recipe });
            SyncEditorFromRecipe(recipe);
            await RefreshEditHistoryAsync(photo.Id);
            await RenderSelectedAsync();
            Status = $"Restored snapshot: {snapshot.Name}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            Status = $"Unable to restore snapshot: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void SyncEditorFromRecipe(EditRecipe recipe)
    {
        _syncingSelection = true;
        Exposure = recipe.ExposureEv;
        Contrast = recipe.Contrast;
        Saturation = recipe.Saturation;
        Temperature = recipe.Temperature;
        Tint = recipe.Tint;
        RotationDegrees = recipe.RotationDegrees;
        Highlights = recipe.Highlights;
        Shadows = recipe.Shadows;
        Whites = recipe.Whites;
        Blacks = recipe.Blacks;
        Vibrance = recipe.Vibrance;
        Vignette = recipe.Vignette;
        _syncingSelection = false;
    }

    private void SetDevelopValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, Math.Clamp(value, -100, 100), propertyName) && !_syncingSelection)
        {
            ScheduleEditUpdate();
        }
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
            var suggestion = analysis.EffectiveSuggestion.Normalize();
            analysis = analysis with { DevelopSuggestion = suggestion };
            await _catalog.UpdateAnalysisAsync(photo.Id, analysis, token);
            ReplacePhoto(photo with { AiSummary = analysis.Summary, AiSuggestion = suggestion });
            IsPreviewingAiSuggestion = true;
            Status = $"AI proposal staged · {suggestion.Confidence:P0} confidence · review before applying";
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

    private void ToggleAiSuggestionPreview()
    {
        if (HasPendingAiSuggestion)
        {
            IsPreviewingAiSuggestion = !IsPreviewingAiSuggestion;
            Status = IsPreviewingAiSuggestion
                ? "Previewing the AI parameter proposal; the active edit is unchanged."
                : "AI preview off; showing the active edit.";
        }
    }

    private async Task ApplyAiSuggestionAsync()
    {
        await AwaitBackgroundTaskAsync(_editTask);
        var photo = SelectedPhoto;
        var suggestion = photo?.AiSuggestion;
        if (photo is null || suggestion?.Status != DevelopSuggestionStatus.Pending)
        {
            return;
        }

        BeginOperation("Applying AI parameter proposal…");
        try
        {
            var token = _operationCancellation!.Token;
            var merged = suggestion.MergeOnto(photo.Edit);
            await _catalog.ApplyDevelopSuggestionAsync(
                photo.Id,
                suggestion.Id,
                merged,
                token);
            var appliedSuggestion = suggestion with { Status = DevelopSuggestionStatus.Applied };
            _isPreviewingAiSuggestion = false;
            OnPropertyChanged(nameof(IsPreviewingAiSuggestion));
            OnPropertyChanged(nameof(AiPreviewLabel));
            ReplacePhoto(photo with { Edit = merged, AiSuggestion = appliedSuggestion });
            SyncEditorFromRecipe(merged);
            await RefreshEditHistoryAsync(photo.Id);
            await RenderSelectedAsync();
            Status = "AI parameter proposal applied as one nondestructive revision. Undo is available.";
        }
        catch (OperationCanceledException)
        {
            Status = "Applying the AI proposal was cancelled.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            Status = $"Unable to apply AI proposal: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RejectAiSuggestionAsync()
    {
        var photo = SelectedPhoto;
        var suggestion = photo?.AiSuggestion;
        if (photo is null || suggestion?.Status != DevelopSuggestionStatus.Pending)
        {
            return;
        }

        BeginOperation("Rejecting AI parameter proposal…");
        try
        {
            var token = _operationCancellation!.Token;
            await _catalog.UpdateDevelopSuggestionStatusAsync(
                photo.Id,
                suggestion.Id,
                DevelopSuggestionStatus.Rejected,
                token);
            _isPreviewingAiSuggestion = false;
            OnPropertyChanged(nameof(IsPreviewingAiSuggestion));
            OnPropertyChanged(nameof(AiPreviewLabel));
            ReplacePhoto(photo with
            {
                AiSuggestion = suggestion with { Status = DevelopSuggestionStatus.Rejected }
            });
            await RenderSelectedAsync();
            Status = "AI proposal rejected; the active edit and history were not changed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Rejecting the AI proposal was cancelled.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            Status = $"Unable to reject AI proposal: {exception.Message}";
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
        NotifyAiSuggestionChanged();
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
        PreviewAiSuggestionCommand.NotifyCanExecuteChanged();
        ApplyAiSuggestionCommand.NotifyCanExecuteChanged();
        RejectAiSuggestionCommand.NotifyCanExecuteChanged();
        PickCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        ResetEditCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        ShowDevelopCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        CreateSnapshotCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAiSuggestionChanged()
    {
        OnPropertyChanged(nameof(AiSuggestion));
        OnPropertyChanged(nameof(HasPendingAiSuggestion));
        OnPropertyChanged(nameof(AiSuggestionConfidence));
        OnPropertyChanged(nameof(AiSuggestionDecisions));
        OnPropertyChanged(nameof(AiParameterChanges));
        OnPropertyChanged(nameof(AiSuggestionWarnings));
        PreviewAiSuggestionCommand.NotifyCanExecuteChanged();
        ApplyAiSuggestionCommand.NotifyCanExecuteChanged();
        RejectAiSuggestionCommand.NotifyCanExecuteChanged();
    }

    private DevelopParameterChangeViewModel[] BuildAiParameterChanges()
    {
        var photo = SelectedPhoto;
        var suggestion = photo?.AiSuggestion;
        if (photo is null || suggestion is null)
        {
            return [];
        }

        var parameters = suggestion.ControlledParameters ??
        [
            nameof(EditRecipe.ExposureEv), nameof(EditRecipe.Contrast), nameof(EditRecipe.Highlights),
            nameof(EditRecipe.Shadows), nameof(EditRecipe.Whites), nameof(EditRecipe.Blacks),
            nameof(EditRecipe.Temperature), nameof(EditRecipe.Tint), nameof(EditRecipe.Vibrance),
            nameof(EditRecipe.Saturation), nameof(EditRecipe.Vignette), nameof(EditRecipe.RotationDegrees)
        ];
        var reasons = suggestion.Decisions
            .GroupBy(decision => decision.Parameter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.OrdinalIgnoreCase);

        return parameters
            .Select(parameter =>
            {
                var current = ReadRecipeParameter(photo.Edit, parameter);
                var proposed = ReadRecipeParameter(suggestion.Recipe, parameter);
                var unit = parameter == nameof(EditRecipe.ExposureEv) ? " EV" :
                    parameter == nameof(EditRecipe.RotationDegrees) ? "°" : string.Empty;
                var reason = reasons.GetValueOrDefault(parameter) ??
                    reasons.FirstOrDefault(pair => parameter.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)).Value ??
                    "Chosen from the local visual analysis.";
                return new DevelopParameterChangeViewModel(
                    FormatParameterName(parameter),
                    $"{proposed:+0.##;-0.##;0}{unit}",
                    $"{proposed - current:+0.##;-0.##;0}",
                    reason);
            })
            .Where(change => change.Delta != "0")
            .ToArray();
    }

    private static double ReadRecipeParameter(EditRecipe recipe, string parameter) => parameter switch
    {
        nameof(EditRecipe.ExposureEv) => recipe.ExposureEv,
        nameof(EditRecipe.Contrast) => recipe.Contrast,
        nameof(EditRecipe.Highlights) => recipe.Highlights,
        nameof(EditRecipe.Shadows) => recipe.Shadows,
        nameof(EditRecipe.Whites) => recipe.Whites,
        nameof(EditRecipe.Blacks) => recipe.Blacks,
        nameof(EditRecipe.Temperature) => recipe.Temperature,
        nameof(EditRecipe.Tint) => recipe.Tint,
        nameof(EditRecipe.Vibrance) => recipe.Vibrance,
        nameof(EditRecipe.Saturation) => recipe.Saturation,
        nameof(EditRecipe.Vignette) => recipe.Vignette,
        nameof(EditRecipe.RotationDegrees) => recipe.RotationDegrees,
        _ => 0
    };

    private static string FormatParameterName(string parameter) => parameter switch
    {
        nameof(EditRecipe.ExposureEv) => "Exposure",
        nameof(EditRecipe.RotationDegrees) => "Straighten",
        _ => parameter
    };

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

public sealed record DevelopParameterChangeViewModel(
    string Parameter,
    string ProposedValue,
    string Delta,
    string Reason);
