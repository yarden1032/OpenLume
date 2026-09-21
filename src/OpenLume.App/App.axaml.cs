using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenLume.App.ViewModels;
using OpenLume.App.Views;
using OpenLume.Imaging;
using OpenLume.Infrastructure.AI;
using OpenLume.Infrastructure.Catalog;
using OpenLume.Infrastructure.Presets;

namespace OpenLume.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenLume");
            Directory.CreateDirectory(appData);

            var catalog = new SqlitePhotoCatalog(Path.Combine(appData, "catalog.db"));
            var renderer = new SkiaImageRenderer();
            var analysis = new OllamaPhotoAnalysisProvider();
            var thumbnailCache = new ThumbnailCache(
                Path.Combine(appData, "thumbnails"),
                maxBytes: 2L * 1024 * 1024 * 1024,
                renderer);
            var metadataIndexer = new MetadataIndexingService(catalog);
            var viewModel = new MainWindowViewModel(
                catalog,
                renderer,
                analysis,
                new XmpPresetImporter(),
                thumbnailCache,
                metadataIndexer);
            desktop.MainWindow = new MainWindow(viewModel);
            desktop.Exit += async (_, _) => await viewModel.DisposeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

