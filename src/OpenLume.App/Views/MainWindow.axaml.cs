using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenLume.App.ViewModels;
using OpenLume.Core.Domain;

namespace OpenLume.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly string[] JpegPatterns = ["*.jpg", "*.jpeg"];
    private static readonly string[] XmpPatterns = ["*.xmp"];
    private static readonly string[] PhotoPatterns = SupportedPhotoFormats.RasterExtensions
        .Concat(SupportedPhotoFormats.RawExtensions)
        .Select(extension => "*" + extension)
        .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
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
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export edited JPEG",
            SuggestedFileName = Path.GetFileNameWithoutExtension(photo.FileName) + "-OpenLume.jpg",
            DefaultExtension = "jpg",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JPEG image") { Patterns = JpegPatterns }
            }
        });
        if (destination is not null)
        {
            await ViewModel.ExportSelectedAsync(destination.Path.LocalPath);
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

    private void LibraryList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItems is not null)
        {
            ViewModel.SetSelectedItems(listBox.SelectedItems.OfType<LibraryPhotoItemViewModel>());
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
