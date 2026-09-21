using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class SqlitePhotoCatalogTests
{
    private static readonly string[] PortraitTags = ["portrait"];

    [Fact]
    public async Task ImportsSupportedFilesAndSkipsDuplicates()
    {
        var root = Temp(); try
        {
            File.WriteAllText(Path.Combine(root, "a.jpg"), "x"); File.WriteAllText(Path.Combine(root, "b.txt"), "x");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            var first = await catalog.ImportFolderAsync(root, false); var second = await catalog.ImportFolderAsync(root, false);
            Assert.Equal(1, first.Imported); Assert.Equal(0, first.AlreadyPresent); Assert.Equal(1, second.AlreadyPresent); Assert.Single(await catalog.GetPhotosAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecursiveFlagControlsSubfolders()
    {
        var root = Temp(); try
        {
            var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName; File.WriteAllText(Path.Combine(child, "a.nef"), "x");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "db.sqlite"));
            Assert.Equal(0, (await catalog.ImportFolderAsync(root, false)).Imported); Assert.Equal(1, (await catalog.ImportFolderAsync(root, true)).Imported);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpdatesPersistAndRatingIsClamped()
    {
        var root = Temp(); try
        {
            var path = Path.Combine(root, "a.png"); File.WriteAllText(path, "x"); await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "db")); await catalog.ImportFolderAsync(root, false); var photo = (await catalog.GetPhotosAsync()).Single();
            var edit = new EditRecipe(ExposureEv: 99); await catalog.UpdateEditAsync(photo.Id, edit); await catalog.UpdateRatingAsync(photo.Id, 99); await catalog.UpdatePickStateAsync(photo.Id, PickState.Pick);
            await catalog.UpdateAnalysisAsync(photo.Id, new PhotoAnalysis("great", .8, .9, true, PortraitTags, edit));
            var updated = await catalog.GetPhotoAsync(photo.Id); Assert.NotNull(updated); Assert.Equal(5, updated!.Rating); Assert.Equal(PickState.Pick, updated.PickState); Assert.Equal(5, updated.Edit.ExposureEv); Assert.Equal("great", updated.AiSummary);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationIsHonored()
    {
        var root = Temp(); try
        {
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "db")); using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.InitializeAsync(cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CreatesMissingCatalogDirectoryAndRejectsUnknownPhotoUpdates()
    {
        var root = Temp();
        try
        {
            var database = Path.Combine(root, "nested", "catalog.db");
            await using var catalog = new SqlitePhotoCatalog(database);
            await catalog.InitializeAsync();
            Assert.True(File.Exists(database));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => catalog.UpdateRatingAsync(Guid.NewGuid(), 3));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "openlume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
