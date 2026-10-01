using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OpenLume.Core.Domain;

namespace OpenLume.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<LocalMaskViewModel> LocalMasks { get; } = [];
    private LocalMaskViewModel? _selectedLocalMask;
    private bool _isLocalMaskMode;
    private bool _showLocalMaskOverlay = true;
    public bool ShowLocalMaskOverlay { get => _showLocalMaskOverlay; set => SetProperty(ref _showLocalMaskOverlay, value); }
    public LocalMaskViewModel? SelectedLocalMask
    {
        get => _selectedLocalMask;
        set
        {
            if (ReferenceEquals(_selectedLocalMask, value)) return;
            _selectedLocalMask?.CancelStroke();
            if (SetProperty(ref _selectedLocalMask, value))
            {
                OnPropertyChanged(nameof(HasSelectedLocalMask));
                DuplicateLocalMaskCommand?.NotifyCanExecuteChanged();
            }
        }
    }
    public bool HasSelectedLocalMask => SelectedLocalMask is not null;
    public bool IsLocalMaskMode
    {
        get => _isLocalMaskMode;
        set
        {
            if (!SetProperty(ref _isLocalMaskMode, value)) return;
            if (!value) SelectedLocalMask?.CancelStroke();
            if (value && IsCropMode) CancelCrop();
            if (!_syncingSelection) _previewTask = RenderSelectedAsync();
        }
    }
    public IRelayCommand AddRadialMaskCommand { get; private set; } = null!;
    public IRelayCommand AddLinearMaskCommand { get; private set; } = null!;
    public IRelayCommand AddBrushMaskCommand { get; private set; } = null!;
    public IRelayCommand RemoveLocalMaskCommand { get; private set; } = null!;
    public IRelayCommand DuplicateLocalMaskCommand { get; private set; } = null!;
    public IRelayCommand MoveLocalMaskUpCommand { get; private set; } = null!;
    public IRelayCommand MoveLocalMaskDownCommand { get; private set; } = null!;

    private void InitializeLocalMaskCommands()
    {
        AddRadialMaskCommand = new RelayCommand(() => AddLocalMask(LocalMaskKind.Radial));
        AddLinearMaskCommand = new RelayCommand(() => AddLocalMask(LocalMaskKind.Linear));
        AddBrushMaskCommand = new RelayCommand(() => AddLocalMask(LocalMaskKind.Brush));
        DuplicateLocalMaskCommand = new RelayCommand(DuplicateLocalMask, CanDuplicateLocalMask);
        LocalMasks.CollectionChanged += (_, _) => DuplicateLocalMaskCommand.NotifyCanExecuteChanged();
        RemoveLocalMaskCommand = new RelayCommand(() =>
        {
            if (SelectedLocalMask is not { } selected) return;
            selected.CancelStroke();
            LocalMasks.Remove(selected);
            SelectedLocalMask = LocalMasks.LastOrDefault();
            ScheduleEditUpdate();
        });
        MoveLocalMaskUpCommand = new RelayCommand(() => MoveLocalMask(-1));
        MoveLocalMaskDownCommand = new RelayCommand(() => MoveLocalMask(1));
    }

    private void AddLocalMask(LocalMaskKind kind)
    {
        if (SelectedPhoto is null || LocalMasks.Count >= 32 || IsBusy) return;
        var item = new LocalMaskViewModel(new LocalMask(Guid.NewGuid(), $"{kind} {LocalMasks.Count + 1}", kind), LocalMaskChanged);
        LocalMasks.Add(item);
        SelectedLocalMask = item;
        IsLocalMaskMode = true;
        ScheduleEditUpdate();
    }

    private bool CanDuplicateLocalMask() => SelectedPhoto is not null && SelectedLocalMask is { } selected &&
        LocalMasks.Contains(selected) && LocalMasks.Count < 32 && !IsBusy;

    private void DuplicateLocalMask()
    {
        if (!CanDuplicateLocalMask() || SelectedLocalMask is not { } source) return;
        source.CancelStroke();
        var recipe = source.CommittedRecipe;
        var name = recipe.Name[..Math.Min(recipe.Name.Length, 59)] + " copy";
        var copy = new LocalMaskViewModel(recipe with { Id = Guid.NewGuid(), Name = name }, LocalMaskChanged);
        LocalMasks.Insert(LocalMasks.IndexOf(source) + 1, copy);
        SelectedLocalMask = copy;
        IsLocalMaskMode = true;
        ScheduleEditUpdate();
    }

    private void MoveLocalMask(int direction)
    {
        if (SelectedLocalMask is not { } selected) return;
        var index = LocalMasks.IndexOf(selected);
        if (index + direction < 0 || index + direction >= LocalMasks.Count) return;
        LocalMasks.Move(index, index + direction);
        ScheduleEditUpdate();
    }

    private void LocalMaskChanged()
    {
        if (!_syncingSelection) ScheduleEditUpdate();
    }

    private void LoadLocalMasks(EditRecipe recipe)
    {
        var selectedId = SelectedLocalMask?.Recipe.Id;
        LocalMasks.Clear();
        foreach (var mask in recipe.Normalize().LocalMasks!)
            LocalMasks.Add(new LocalMaskViewModel(mask, LocalMaskChanged));
        SelectedLocalMask = LocalMasks.FirstOrDefault(mask => mask.Recipe.Id == selectedId) ?? LocalMasks.FirstOrDefault();
    }
}
