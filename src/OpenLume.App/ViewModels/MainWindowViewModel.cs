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
    private double _texture;
    private double _clarity;
    private double _dehaze;
    private double _sharpening;
    private double _noiseReduction;
    private double _grain;
    private double _toneCurveHighlights;
    private double _toneCurveLights;
    private double _toneCurveDarks;
    private double _toneCurveShadows;
    private double _toneCurveShadowSplit = 25;
    private double _toneCurveMidtoneSplit = 50;
    private double _toneCurveHighlightSplit = 75;
    private bool _isCropMode;
    private double _cropX;
    private double _cropY;
    private double _cropWidth = 1;
    private double _cropHeight = 1;
    private int _cropQuarterTurns;
    private bool _cropFlipHorizontal;
    private bool _cropFlipVertical;
    private double _cropPreviewAspectRatio = 1;
    private string _selectedCropAspect = "Free";
    private double _lensDistortion;
    private double _chromaticAberration;
    private double _lensVignette;
    private double _lensVignetteMidpoint = 50;
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

        ColorMixerChannels =
        [
            new("Red", "Red", "#E05252", ColorMixerChanged),
            new("Orange", "Orange", "#E8893A", ColorMixerChanged),
            new("Yellow", "Yellow", "#D6B63C", ColorMixerChanged),
            new("Green", "Green", "#54A866", ColorMixerChanged),
            new("Aqua", "Aqua", "#48AEB1", ColorMixerChanged),
            new("Blue", "Blue", "#4D78D0", ColorMixerChanged),
            new("Purple", "Purple", "#8B63C8", ColorMixerChanged),
            new("Magenta", "Magenta", "#C45B9E", ColorMixerChanged)
        ];

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
        ResetToneCurveCommand = new RelayCommand(ResetToneCurve, () => SelectedPhoto is not null);
        ResetOpticsCommand = new RelayCommand(ResetOptics, () => SelectedPhoto is not null);
        StartCropCommand = new RelayCommand(StartCrop, () => SelectedPhoto is not null && !IsBusy);
        ApplyCropCommand = new AsyncRelayCommand(ApplyCropAsync, () => IsCropMode && SelectedPhoto is not null && !IsBusy);
        CancelCropCommand = new RelayCommand(CancelCrop, () => IsCropMode);
        RotateCropLeftCommand = new RelayCommand(() => RotateCrop(-1), () => IsCropMode);
        RotateCropRightCommand = new RelayCommand(() => RotateCrop(1), () => IsCropMode);
        FlipCropHorizontalCommand = new RelayCommand(() => ToggleCropFlip(horizontal: true), () => IsCropMode);
        FlipCropVerticalCommand = new RelayCommand(() => ToggleCropFlip(horizontal: false), () => IsCropMode);
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
    public ObservableCollection<HslChannelViewModel> ColorMixerChannels { get; }
    public IReadOnlyList<string> CropAspectOptions { get; } = ["Free", "Original", "1:1", "4:5", "3:2", "16:9"];

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IRelayCommand PreviewAiSuggestionCommand { get; }
    public IAsyncRelayCommand ApplyAiSuggestionCommand { get; }
    public IAsyncRelayCommand RejectAiSuggestionCommand { get; }
    public IAsyncRelayCommand PickCommand { get; }
    public IAsyncRelayCommand RejectCommand { get; }
    public IAsyncRelayCommand ResetEditCommand { get; }
    public IRelayCommand ResetToneCurveCommand { get; }
    public IRelayCommand ResetOpticsCommand { get; }
    public IRelayCommand StartCropCommand { get; }
    public IAsyncRelayCommand ApplyCropCommand { get; }
    public IRelayCommand CancelCropCommand { get; }
    public IRelayCommand RotateCropLeftCommand { get; }
    public IRelayCommand RotateCropRightCommand { get; }
    public IRelayCommand FlipCropHorizontalCommand { get; }
    public IRelayCommand FlipCropVerticalCommand { get; }
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
            IsCropMode = false;
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
                if (IsCropMode)
                {
                    _previewTask = RenderSelectedAsync();
                }
                else
                {
                    ScheduleEditUpdate();
                }
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

    public double Texture { get => _texture; set => SetDevelopValue(ref _texture, value); }

    public double Clarity { get => _clarity; set => SetDevelopValue(ref _clarity, value); }

    public double Dehaze { get => _dehaze; set => SetDevelopValue(ref _dehaze, value); }

    public double Sharpening { get => _sharpening; set => SetPositiveDevelopValue(ref _sharpening, value); }

    private double _colorNoiseReduction;
    public double ColorNoiseReduction { get => _colorNoiseReduction; set => SetPositiveDevelopValue(ref _colorNoiseReduction, value); }
    public double NoiseReduction { get => _noiseReduction; set => SetPositiveDevelopValue(ref _noiseReduction, value); }

    public double Grain { get => _grain; set => SetPositiveDevelopValue(ref _grain, value); }

    public double ToneCurveHighlights { get => _toneCurveHighlights; set => SetToneCurveValue(ref _toneCurveHighlights, value); }

    public double ToneCurveLights { get => _toneCurveLights; set => SetToneCurveValue(ref _toneCurveLights, value); }

    public double ToneCurveDarks { get => _toneCurveDarks; set => SetToneCurveValue(ref _toneCurveDarks, value); }

    public double ToneCurveShadows { get => _toneCurveShadows; set => SetToneCurveValue(ref _toneCurveShadows, value); }

    public double ToneCurveShadowSplit
    {
        get => _toneCurveShadowSplit;
        set => SetToneCurveSplit(ref _toneCurveShadowSplit, Math.Clamp(value, 5, ToneCurveMidtoneSplit - 5));
    }

    public double ToneCurveMidtoneSplit
    {
        get => _toneCurveMidtoneSplit;
        set => SetToneCurveSplit(ref _toneCurveMidtoneSplit, Math.Clamp(value, ToneCurveShadowSplit + 5, ToneCurveHighlightSplit - 5));
    }

    public double ToneCurveHighlightSplit
    {
        get => _toneCurveHighlightSplit;
        set => SetToneCurveSplit(ref _toneCurveHighlightSplit, Math.Clamp(value, ToneCurveMidtoneSplit + 5, 95));
    }

    public ParametricToneCurve CurrentToneCurve => BuildToneCurve();

    public bool IsCropMode
    {
        get => _isCropMode;
        private set
        {
            if (SetProperty(ref _isCropMode, value))
            {
                OnPropertyChanged(nameof(CropModeLabel));
                NotifyCommands();
            }
        }
    }

    public string CropModeLabel => IsCropMode ? "Editing crop · drag the frame or corner handles" : "Visual crop, rotate, flip, and straighten";
    public double CropX { get => _cropX; set => SetCropValue(ref _cropX, Math.Clamp(value, 0, 1 - CropWidth)); }
    public double CropY { get => _cropY; set => SetCropValue(ref _cropY, Math.Clamp(value, 0, 1 - CropHeight)); }
    public double CropWidth { get => _cropWidth; set => SetCropValue(ref _cropWidth, Math.Clamp(value, .01, 1 - CropX)); }
    public double CropHeight { get => _cropHeight; set => SetCropValue(ref _cropHeight, Math.Clamp(value, .01, 1 - CropY)); }
    public double CropPreviewAspectRatio
    {
        get => _cropPreviewAspectRatio;
        private set
        {
            if (SetProperty(ref _cropPreviewAspectRatio, Math.Max(.01, value)))
            {
                OnPropertyChanged(nameof(CropLockedNormalizedAspectRatio));
                ApplyCropAspectPreset();
            }
        }
    }

    public double CropLockedNormalizedAspectRatio => SelectedCropAspect switch
    {
        "Original" => 1,
        "1:1" => 1 / CropPreviewAspectRatio,
        "4:5" => .8 / CropPreviewAspectRatio,
        "3:2" => 1.5 / CropPreviewAspectRatio,
        "16:9" => (16d / 9) / CropPreviewAspectRatio,
        _ => 0
    };

    public string SelectedCropAspect
    {
        get => _selectedCropAspect;
        set
        {
            if (SetProperty(ref _selectedCropAspect, CropAspectOptions.Contains(value) ? value : "Free"))
            {
                OnPropertyChanged(nameof(CropLockedNormalizedAspectRatio));
                ApplyCropAspectPreset();
            }
        }
    }

    public string CropOrientationSummary =>
        $"{CropQuarterTurns * 90}° · H {(CropFlipHorizontal ? "flipped" : "normal")} · V {(CropFlipVertical ? "flipped" : "normal")}";
    public int CropQuarterTurns => _cropQuarterTurns;
    public bool CropFlipHorizontal => _cropFlipHorizontal;
    public bool CropFlipVertical => _cropFlipVertical;

    public double LensDistortion
    {
        get => _lensDistortion;
        set => SetDevelopValue(ref _lensDistortion, value);
    }

    public double ChromaticAberration
    {
        get => _chromaticAberration;
        set => SetPositiveDevelopValue(ref _chromaticAberration, value);
    }

    public double LensVignette
    {
        get => _lensVignette;
        set => SetDevelopValue(ref _lensVignette, value);
    }

    public double LensVignetteMidpoint
    {
        get => _lensVignetteMidpoint;
        set => SetPositiveDevelopValue(ref _lensVignetteMidpoint, value);
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
            if (IsCropMode)
            {
                recipe = recipe with
                {
                    RotationDegrees = RotationDegrees,
                    Crop = BuildPendingCrop().WithoutCrop()
                };
            }
            var rendered = await _renderer.RenderPreviewAsync(photo.OriginalPath, recipe, 1800, token);
            token.ThrowIfCancellationRequested();
            Preview = CreateBitmap(rendered.Data);
            CropPreviewAspectRatio = rendered.Width / (double)Math.Max(1, rendered.Height);
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
            RotationDegrees = IsCropMode ? photo.Edit.RotationDegrees : RotationDegrees,
            Highlights = Highlights,
            Shadows = Shadows,
            Whites = Whites,
            Blacks = Blacks,
            Vibrance = Vibrance,
            Vignette = Vignette,
            Texture = Texture,
            Clarity = Clarity,
            Dehaze = Dehaze,
            Sharpening = Sharpening,
            NoiseReduction = NoiseReduction,
            ColorNoiseReduction = ColorNoiseReduction,
            Grain = Grain,
            ColorMixer = BuildColorMixer(),
            ToneCurve = BuildToneCurve(),
            Optics = BuildOpticsCorrections()
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
        Texture = recipe.Texture;
        Clarity = recipe.Clarity;
        Dehaze = recipe.Dehaze;
        Sharpening = recipe.Sharpening;
        NoiseReduction = recipe.NoiseReduction;
        ColorNoiseReduction = recipe.ColorNoiseReduction;
        Grain = recipe.Grain;
        LoadColorMixer(recipe.ColorMixer);
        LoadToneCurve(recipe.ToneCurve);
        LoadCrop(recipe.Crop);
        LoadOpticsCorrections(recipe.Optics);
        _syncingSelection = false;
    }

    private CropGeometry BuildPendingCrop() => new CropGeometry(
        CropX,
        CropY,
        CropWidth,
        CropHeight,
        CropQuarterTurns,
        CropFlipHorizontal,
        CropFlipVertical).Normalize();

    private void LoadCrop(CropGeometry? crop)
    {
        var normalized = (crop ?? CropGeometry.FullFrame).Normalize();
        _cropX = normalized.X;
        _cropY = normalized.Y;
        _cropWidth = normalized.Width;
        _cropHeight = normalized.Height;
        _cropQuarterTurns = normalized.QuarterTurns;
        _cropFlipHorizontal = normalized.FlipHorizontal;
        _cropFlipVertical = normalized.FlipVertical;
        _selectedCropAspect = "Free";
        OnPropertyChanged(nameof(CropX));
        OnPropertyChanged(nameof(CropY));
        OnPropertyChanged(nameof(CropWidth));
        OnPropertyChanged(nameof(CropHeight));
        OnPropertyChanged(nameof(CropQuarterTurns));
        OnPropertyChanged(nameof(CropFlipHorizontal));
        OnPropertyChanged(nameof(CropFlipVertical));
        OnPropertyChanged(nameof(CropOrientationSummary));
        OnPropertyChanged(nameof(SelectedCropAspect));
        OnPropertyChanged(nameof(CropLockedNormalizedAspectRatio));
    }

    private void StartCrop()
    {
        var photo = SelectedPhoto;
        if (photo is null)
        {
            return;
        }

        LoadCrop(photo.Edit.Crop);
        IsCropMode = true;
        Status = "Crop mode · drag inside to move, drag a corner to resize";
        _previewTask = RenderSelectedAsync();
    }

    private async Task ApplyCropAsync()
    {
        await AwaitBackgroundTaskAsync(_editTask);
        // Geometry renders update the aspect constraint before the pending frame is committed.
        await AwaitBackgroundTaskAsync(_previewTask);
        var photo = SelectedPhoto;
        if (photo is null || !IsCropMode)
        {
            return;
        }

        BeginOperation("Applying crop…");
        try
        {
            var edit = (photo.Edit with
            {
                RotationDegrees = RotationDegrees,
                Crop = BuildPendingCrop()
            }).Normalize();
            await _catalog.UpdateEditAsync(photo.Id, edit, _operationCancellation!.Token);
            ReplacePhoto(photo with { Edit = edit });
            IsCropMode = false;
            await RefreshEditHistoryAsync(photo.Id);
            await RenderSelectedAsync();
            Status = "Crop applied as one reversible edit.";
        }
        catch (OperationCanceledException)
        {
            Status = "Crop cancelled.";
        }
        catch (Exception exception)
        {
            Status = $"Unable to apply crop: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void CancelCrop()
    {
        LoadCrop(SelectedPhoto?.Edit.Crop);
        _rotationDegrees = SelectedPhoto?.Edit.RotationDegrees ?? 0;
        OnPropertyChanged(nameof(RotationDegrees));
        IsCropMode = false;
        Status = "Crop changes discarded.";
        _previewTask = RenderSelectedAsync();
    }

    private void RotateCrop(int delta)
    {
        _cropQuarterTurns = ((_cropQuarterTurns + delta) % 4 + 4) % 4;
        OnPropertyChanged(nameof(CropQuarterTurns));
        OnPropertyChanged(nameof(CropOrientationSummary));
        _previewTask = RenderSelectedAsync();
    }

    private void ToggleCropFlip(bool horizontal)
    {
        if (horizontal)
        {
            _cropFlipHorizontal = !_cropFlipHorizontal;
            OnPropertyChanged(nameof(CropFlipHorizontal));
        }
        else
        {
            _cropFlipVertical = !_cropFlipVertical;
            OnPropertyChanged(nameof(CropFlipVertical));
        }

        OnPropertyChanged(nameof(CropOrientationSummary));
        _previewTask = RenderSelectedAsync();
    }

    private void SetCropValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(CropOrientationSummary));
        }
    }

    private void ApplyCropAspectPreset()
    {
        var ratio = CropLockedNormalizedAspectRatio;
        if (!IsCropMode || ratio <= .01)
        {
            return;
        }

        double width;
        double height;
        if (ratio >= 1)
        {
            width = 1;
            height = 1 / ratio;
        }
        else
        {
            width = ratio;
            height = 1;
        }

        _cropX = (1 - width) / 2;
        _cropY = (1 - height) / 2;
        _cropWidth = width;
        _cropHeight = height;
        OnPropertyChanged(nameof(CropX));
        OnPropertyChanged(nameof(CropY));
        OnPropertyChanged(nameof(CropWidth));
        OnPropertyChanged(nameof(CropHeight));
    }

    private ParametricToneCurve BuildToneCurve() => new ParametricToneCurve(
        ToneCurveHighlights,
        ToneCurveLights,
        ToneCurveDarks,
        ToneCurveShadows,
        ToneCurveShadowSplit,
        ToneCurveMidtoneSplit,
        ToneCurveHighlightSplit).Normalize();

    private void LoadToneCurve(ParametricToneCurve? curve)
    {
        var normalized = (curve ?? ParametricToneCurve.Identity).Normalize();
        _toneCurveHighlights = normalized.Highlights;
        _toneCurveLights = normalized.Lights;
        _toneCurveDarks = normalized.Darks;
        _toneCurveShadows = normalized.Shadows;
        _toneCurveShadowSplit = normalized.ShadowSplit;
        _toneCurveMidtoneSplit = normalized.MidtoneSplit;
        _toneCurveHighlightSplit = normalized.HighlightSplit;
        OnPropertyChanged(nameof(ToneCurveHighlights));
        OnPropertyChanged(nameof(ToneCurveLights));
        OnPropertyChanged(nameof(ToneCurveDarks));
        OnPropertyChanged(nameof(ToneCurveShadows));
        OnPropertyChanged(nameof(ToneCurveShadowSplit));
        OnPropertyChanged(nameof(ToneCurveMidtoneSplit));
        OnPropertyChanged(nameof(ToneCurveHighlightSplit));
        OnPropertyChanged(nameof(CurrentToneCurve));
    }

    private void SetToneCurveValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, Math.Clamp(value, -100, 100), propertyName))
        {
            OnPropertyChanged(nameof(CurrentToneCurve));
            if (!_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    private void SetToneCurveSplit(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(CurrentToneCurve));
            if (!_syncingSelection)
            {
                ScheduleEditUpdate();
            }
        }
    }

    private void ResetToneCurve()
    {
        _syncingSelection = true;
        LoadToneCurve(ParametricToneCurve.Identity);
        _syncingSelection = false;
        ScheduleEditUpdate();
    }

    private void ResetOptics()
    {
        _syncingSelection = true;
        LoadOpticsCorrections(OpticsCorrections.Neutral);
        _syncingSelection = false;
        ScheduleEditUpdate();
    }

    private OpticsCorrections BuildOpticsCorrections() => new(
        LensDistortion,
        ChromaticAberration,
        LensVignette,
        LensVignetteMidpoint);

    private void LoadOpticsCorrections(OpticsCorrections? corrections)
    {
        var normalized = (corrections ?? OpticsCorrections.Neutral).Normalize();
        _lensDistortion = normalized.Distortion;
        _chromaticAberration = normalized.ChromaticAberration;
        _lensVignette = normalized.LensVignette;
        _lensVignetteMidpoint = normalized.VignetteMidpoint;
        OnPropertyChanged(nameof(LensDistortion));
        OnPropertyChanged(nameof(ChromaticAberration));
        OnPropertyChanged(nameof(LensVignette));
        OnPropertyChanged(nameof(LensVignetteMidpoint));
    }

    private void ColorMixerChanged()
    {
        if (!_syncingSelection)
        {
            ScheduleEditUpdate();
        }
    }

    private HslColorMixer BuildColorMixer()
    {
        var channels = ColorMixerChannels.ToDictionary(channel => channel.Key, StringComparer.Ordinal);
        return new HslColorMixer(
            channels["Red"].ToAdjustment(),
            channels["Orange"].ToAdjustment(),
            channels["Yellow"].ToAdjustment(),
            channels["Green"].ToAdjustment(),
            channels["Aqua"].ToAdjustment(),
            channels["Blue"].ToAdjustment(),
            channels["Purple"].ToAdjustment(),
            channels["Magenta"].ToAdjustment()).Normalize();
    }

    private void LoadColorMixer(HslColorMixer? mixer)
    {
        var normalized = (mixer ?? HslColorMixer.Neutral).Normalize();
        var values = new Dictionary<string, HslChannelAdjustment?>
        {
            ["Red"] = normalized.Red,
            ["Orange"] = normalized.Orange,
            ["Yellow"] = normalized.Yellow,
            ["Green"] = normalized.Green,
            ["Aqua"] = normalized.Aqua,
            ["Blue"] = normalized.Blue,
            ["Purple"] = normalized.Purple,
            ["Magenta"] = normalized.Magenta
        };
        foreach (var channel in ColorMixerChannels)
        {
            channel.Load(values[channel.Key]);
        }
    }

    private void SetDevelopValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, Math.Clamp(value, -100, 100), propertyName) && !_syncingSelection)
        {
            ScheduleEditUpdate();
        }
    }

    private void SetPositiveDevelopValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, Math.Clamp(value, 0, 100), propertyName) && !_syncingSelection)
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
        ResetToneCurveCommand.NotifyCanExecuteChanged();
        ResetOpticsCommand.NotifyCanExecuteChanged();
        StartCropCommand.NotifyCanExecuteChanged();
        ApplyCropCommand.NotifyCanExecuteChanged();
        CancelCropCommand.NotifyCanExecuteChanged();
        RotateCropLeftCommand.NotifyCanExecuteChanged();
        RotateCropRightCommand.NotifyCanExecuteChanged();
        FlipCropHorizontalCommand.NotifyCanExecuteChanged();
        FlipCropVerticalCommand.NotifyCanExecuteChanged();
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
            nameof(EditRecipe.Saturation), nameof(EditRecipe.Vignette), nameof(EditRecipe.RotationDegrees),
            nameof(EditRecipe.Texture), nameof(EditRecipe.Clarity), nameof(EditRecipe.Dehaze),
            nameof(EditRecipe.Sharpening), nameof(EditRecipe.NoiseReduction), nameof(EditRecipe.Grain),
            nameof(EditRecipe.ColorMixer), DevelopSuggestion.ToneCurveHighlightsParameter,
            DevelopSuggestion.ToneCurveLightsParameter, DevelopSuggestion.ToneCurveDarksParameter,
            DevelopSuggestion.ToneCurveShadowsParameter, DevelopSuggestion.OpticsDistortionParameter,
            DevelopSuggestion.OpticsChromaticAberrationParameter, DevelopSuggestion.OpticsLensVignetteParameter,
            DevelopSuggestion.OpticsVignetteMidpointParameter
        ];
        var reasons = suggestion.Decisions
            .GroupBy(decision => decision.Parameter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.OrdinalIgnoreCase);

        return parameters
            .Select(parameter =>
            {
                if (parameter == nameof(EditRecipe.ColorMixer))
                {
                    var colorMixerReason = reasons.GetValueOrDefault(parameter) ??
                        "Targeted color refinement chosen from the local visual analysis.";
                    return new DevelopParameterChangeViewModel("Color Mixer", "Custom HSL mix", "Changed", colorMixerReason);
                }

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
        nameof(EditRecipe.Texture) => recipe.Texture,
        nameof(EditRecipe.Clarity) => recipe.Clarity,
        nameof(EditRecipe.Dehaze) => recipe.Dehaze,
        nameof(EditRecipe.Sharpening) => recipe.Sharpening,
        nameof(EditRecipe.NoiseReduction) => recipe.NoiseReduction,
        nameof(EditRecipe.ColorNoiseReduction) => recipe.ColorNoiseReduction,
        nameof(EditRecipe.Grain) => recipe.Grain,
        nameof(EditRecipe.RotationDegrees) => recipe.RotationDegrees,
        DevelopSuggestion.ToneCurveHighlightsParameter => recipe.ToneCurve?.Highlights ?? 0,
        DevelopSuggestion.ToneCurveLightsParameter => recipe.ToneCurve?.Lights ?? 0,
        DevelopSuggestion.ToneCurveDarksParameter => recipe.ToneCurve?.Darks ?? 0,
        DevelopSuggestion.ToneCurveShadowsParameter => recipe.ToneCurve?.Shadows ?? 0,
        DevelopSuggestion.OpticsDistortionParameter => recipe.Optics?.Distortion ?? 0,
        DevelopSuggestion.OpticsChromaticAberrationParameter => recipe.Optics?.ChromaticAberration ?? 0,
        DevelopSuggestion.OpticsLensVignetteParameter => recipe.Optics?.LensVignette ?? 0,
        DevelopSuggestion.OpticsVignetteMidpointParameter => recipe.Optics?.VignetteMidpoint ?? 50,
        _ => 0
    };

    private static string FormatParameterName(string parameter) => parameter switch
    {
        nameof(EditRecipe.ExposureEv) => "Exposure",
        nameof(EditRecipe.RotationDegrees) => "Straighten",
        DevelopSuggestion.ToneCurveHighlightsParameter => "Curve Highlights",
        DevelopSuggestion.ToneCurveLightsParameter => "Curve Lights",
        DevelopSuggestion.ToneCurveDarksParameter => "Curve Darks",
        DevelopSuggestion.ToneCurveShadowsParameter => "Curve Shadows",
        DevelopSuggestion.OpticsDistortionParameter => "Lens Distortion",
        DevelopSuggestion.OpticsChromaticAberrationParameter => "Chromatic Aberration",
        DevelopSuggestion.OpticsLensVignetteParameter => "Lens Vignette",
        DevelopSuggestion.OpticsVignetteMidpointParameter => "Vignette Midpoint",
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
