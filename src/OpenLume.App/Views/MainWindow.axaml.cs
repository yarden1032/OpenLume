using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenLume.App.ViewModels;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly string[] JpegPatterns = ["*.jpg", "*.jpeg"];
    private static readonly string[] PngPatterns = ["*.png"];
    private static readonly string[] TiffPatterns = ["*.tif", "*.tiff"];
    private static readonly string[] XmpPatterns = ["*.xmp"];
    private static readonly string[] CatalogPatterns = ["*.db"];
    private static readonly string[] PhotoPatterns = SupportedPhotoFormats.RasterExtensions
        .Concat(SupportedPhotoFormats.RawExtensions)
        .Select(extension => "*" + extension)
        .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;
    private CatalogRecoveryService _catalogRecovery = null!;
    private CatalogRestorePlan? _pendingRestore;
    private bool _closeInProgress;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, CatalogRecoveryService catalogRecovery) : this()
    {
        DataContext = viewModel;
        _catalogRecovery = catalogRecovery;
        Loaded += InitializeAndShowRecoveryNotice_OnLoaded;
        Closing += CloseAfterRecovery_OnClosing;
    }

    private async void ImportFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a photo folder",
            AllowMultiple = false
        });
        if (folders.Count == 1)
        {
            await ViewModel.ImportFolderAsync(folders[0].Path.LocalPath);
        }
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        var photo = ViewModel.SelectedPhoto;
        if (photo is null) return;
        var options = await new ExportSettingsWindow().ShowDialog<ExportOptions?>(this);
        if (options is null) return;
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export edited photo — choose a new filename",
            SuggestedFileName = Path.GetFileNameWithoutExtension(photo.FileName) + "-OpenLume." + options.Extension,
            DefaultExtension = options.Extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(options.Format switch
                {
                    ImageExportFormat.Png => "PNG image",
                    ImageExportFormat.Tiff => "TIFF image",
                    _ => "JPEG image"
                })
                { Patterns = options.Format switch
                {
                    ImageExportFormat.Png => PngPatterns,
                    ImageExportFormat.Tiff => TiffPatterns,
                    _ => JpegPatterns
                } }
            }
        });
        if (destination is not null)
        {
            await ViewModel.ExportSelectedAsync(destination.Path.LocalPath, options);
        }
    }

    private async void CatalogRecovery_OnClick(object? sender, RoutedEventArgs e)
    {
        var action = await new CatalogRecoveryWindow().ShowDialog<CatalogRecoveryAction?>(this);
        if (action == CatalogRecoveryAction.Backup)
        {
            await CreateCatalogBackupAsync();
        }
        else if (action == CatalogRecoveryAction.Restore)
        {
            await RestoreCatalogBackupAsync();
        }
    }

    private async Task CreateCatalogBackupAsync()
    {
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create OpenLume catalog backup",
            SuggestedFileName = $"OpenLume-catalog-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db",
            DefaultExtension = "db",
            FileTypeChoices = [new FilePickerFileType("OpenLume catalog backup") { Patterns = CatalogPatterns }]
        });
        if (destination is null) return;

        try
        {
            await _catalogRecovery.CreateBackupAsync(destination.Path.LocalPath);
            ViewModel.ShowStatusMessage($"Catalog backup created: {Path.GetFileName(destination.Path.LocalPath)}");
        }
        catch (Exception exception)
        {
            ViewModel.ShowStatusMessage($"Catalog backup failed: {exception.Message}");
        }
    }

    private async Task RestoreCatalogBackupAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an OpenLume catalog backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenLume catalog backup") { Patterns = CatalogPatterns }]
        });
        if (files.Count != 1) return;

        var backupPath = files[0].Path.LocalPath;
        var confirmed = await new CatalogRestoreConfirmationWindow(backupPath).ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            ViewModel.ShowStatusMessage("Validating backup and creating a safety copy…");
            _pendingRestore = await _catalogRecovery.PrepareRestoreAsync(backupPath);
            ViewModel.ShowStatusMessage("Backup validated. OpenLume is closing to restore the catalog safely…");
            Close();
        }
        catch (Exception exception)
        {
            _pendingRestore = null;
            ViewModel.ShowStatusMessage($"Catalog restore was not started: {exception.Message}");
        }
    }

    private async void InitializeAndShowRecoveryNotice_OnLoaded(object? sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        var notice = await _catalogRecovery.ReadNoticeAsync();
        if (notice is null) return;

        await new CatalogRecoveryNoticeWindow(notice).ShowDialog(this);
        _catalogRecovery.AcknowledgeNotice();
    }

    private async void CloseAfterRecovery_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;

        try
        {
            await ViewModel.DisposeAsync();
            if (_pendingRestore is not null)
            {
                await _catalogRecovery.RestoreAfterShutdownAsync(_pendingRestore);
            }
        }
        catch (Exception exception)
        {
            if (_pendingRestore is not null)
            {
                try
                {
                    await _catalogRecovery.RecordFailureAsync(
                        _pendingRestore,
                        $"The app could not shut down cleanly for catalog restore: {exception.Message}");
                }
                catch
                {
                    // Keep the app shutdown path moving even if the recovery receipt cannot be written.
                }
            }
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private async void ImportPreset_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhoto is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a Lightroom XMP preset",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Adobe Camera Raw preset") { Patterns = XmpPatterns }
            }
        });
        if (files.Count == 1)
        {
            await ViewModel.ImportPresetAsync(files[0].Path.LocalPath);
        }
    }

    private async void ExportXmpSidecar_OnClick(object? sender, RoutedEventArgs e)
    {
        var photo = ViewModel.SelectedPhoto;
        if (photo is null) return;

        var photoDirectory = Path.GetDirectoryName(photo.OriginalPath);
        var suggestedFolder = photoDirectory is not null && Directory.Exists(photoDirectory)
            ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(photoDirectory))
            : null;

        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Write Adobe Camera Raw XMP sidecar",
            SuggestedFileName = Path.GetFileNameWithoutExtension(photo.FileName) + ".xmp",
            SuggestedStartLocation = suggestedFolder,
            ShowOverwritePrompt = true,
            DefaultExtension = "xmp",
            FileTypeChoices = [new FilePickerFileType("Adobe Camera Raw XMP sidecar") { Patterns = XmpPatterns }]
        });
        if (destination is not null)
        {
            await ViewModel.ExportSelectedXmpSidecarAsync(destination.Path.LocalPath);
        }
    }

    private void LibraryList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItems is not null)
        {
            ViewModel.SetSelectedItems(listBox.SelectedItems.OfType<LibraryPhotoItemViewModel>());
        }
    }

    private void LibraryGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.ShowDevelopCommand.CanExecute(null))
        {
            ViewModel.ShowDevelopCommand.Execute(null);
        }
    }

    private async void Relink_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhoto is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate the missing original",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported photos") { Patterns = PhotoPatterns }
            ]
        });
        if (files.Count == 1)
        {
            await ViewModel.RelinkSelectedAsync(files[0].Path.LocalPath);
        }
    }
}
