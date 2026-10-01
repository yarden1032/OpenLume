using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class EditHistoryTests
{
    [Fact]
    public async Task ImportedPhotoStartsAtOriginalAndNoOpDoesNotCreateRevision()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                var initial = await catalog.GetEditHistoryAsync(photo.Id);
                Assert.Single(initial.Revisions);
                Assert.Equal(0, initial.CurrentSequence);
                Assert.Equal(EditRecipe.Default, initial.Current!.Recipe);

                await catalog.UpdateEditAsync(photo.Id, EditRecipe.Default);

                Assert.Single((await catalog.GetEditHistoryAsync(photo.Id)).Revisions);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task UpdatesUndoRedoAndBranchPruningPersist()
    {
        var root = CreateTemporaryDirectory();
        var databasePath = Path.Combine(root, "catalog.db");
        try
        {
            Guid photoId;
            await using (var catalog = new SqlitePhotoCatalog(databasePath))
            {
                await File.WriteAllTextAsync(Path.Combine(root, "photo.jpg"), "image");
                await catalog.ImportFolderAsync(root, includeSubfolders: false);
                photoId = (await catalog.GetPhotosAsync()).Single().Id;
                await catalog.UpdateEditAsync(photoId, new EditRecipe(ExposureEv: 1));
                await catalog.UpdateEditAsync(photoId, new EditRecipe(ExposureEv: 2));

                Assert.Equal(1, (await catalog.UndoEditAsync(photoId))!.ExposureEv);
                Assert.Equal(2, (await catalog.RedoEditAsync(photoId))!.ExposureEv);
                await catalog.UndoEditAsync(photoId);
                await catalog.UpdateEditAsync(photoId, new EditRecipe(ExposureEv: 3));

                var branched = await catalog.GetEditHistoryAsync(photoId);
                Assert.Equal([0L, 1L, 2L], branched.Revisions.Select(revision => revision.Sequence));
                Assert.Equal(3, branched.Current!.Recipe.ExposureEv);
                Assert.False(branched.CanRedo);
            }

            await using var reopened = new SqlitePhotoCatalog(databasePath);
            var persisted = await reopened.GetEditHistoryAsync(photoId);
            Assert.Equal(2, persisted.CurrentSequence);
            Assert.Equal(3, (await reopened.GetPhotoAsync(photoId))!.Edit.ExposureEv);
            Assert.Equal(1, (await reopened.UndoEditAsync(photoId))!.ExposureEv);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task SnapshotRestoresRecipeAndRejectsDuplicateName()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                await catalog.UpdateEditAsync(photo.Id, new EditRecipe(Contrast: 12));
                var snapshotId = await catalog.CreateEditSnapshotAsync(photo.Id, "  Soft look  ");
                await catalog.UpdateEditAsync(photo.Id, new EditRecipe(Contrast: 45));

                var restored = await catalog.RestoreEditSnapshotAsync(photo.Id, snapshotId);

                Assert.Equal(12, restored.Contrast);
                var snapshot = Assert.Single(await catalog.GetEditSnapshotsAsync(photo.Id));
                Assert.Equal("Soft look", snapshot.Name);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => catalog.CreateEditSnapshotAsync(photo.Id, "soft LOOK"));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ColorMixerPersistsAndParticipatesInUndoRedo()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                var first = new EditRecipe(
                    ColorMixer: new HslColorMixer(
                        Orange: new HslChannelAdjustment(Hue: -11, Saturation: 18, Luminance: 7)),
                    ToneCurve: new ParametricToneCurve(Lights: 14, Darks: -8, ShadowSplit: 22));
                var second = first with
                {
                    ColorMixer = first.ColorMixer! with
                    {
                        Blue = new HslChannelAdjustment(Hue: 9, Saturation: -24, Luminance: -5)
                    }
                };
                await catalog.UpdateEditAsync(photo.Id, first);
                await catalog.UpdateEditAsync(photo.Id, second);

                var persisted = (await catalog.GetPhotoAsync(photo.Id))!.Edit.Normalize();
                Assert.Equal(-24, persisted.ColorMixer!.Blue!.Saturation);
                Assert.Equal(14, persisted.ToneCurve!.Lights);
                Assert.Equal(22, persisted.ToneCurve.ShadowSplit);
                var undone = await catalog.UndoEditAsync(photo.Id);
                Assert.Equal(18, undone!.ColorMixer!.Orange!.Saturation);
                Assert.Equal(0, undone.ColorMixer.Blue!.Saturation);
                Assert.Equal(-8, undone.ToneCurve!.Darks);
                var redone = await catalog.RedoEditAsync(photo.Id);
                Assert.Equal(-24, redone!.ColorMixer!.Blue!.Saturation);
                Assert.Equal(14, redone.ToneCurve!.Lights);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CropGeometryPersistsAndParticipatesInUndoRedo()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                var first = new EditRecipe(Crop: new CropGeometry(.1, .2, .8, .7, 1));
                var second = first with
                {
                    Crop = new CropGeometry(.2, .1, .6, .8, 2, FlipHorizontal: true)
                };
                await catalog.UpdateEditAsync(photo.Id, first);
                await catalog.UpdateEditAsync(photo.Id, second);

                var persisted = (await catalog.GetPhotoAsync(photo.Id))!.Edit.Normalize();
                Assert.Equal(second.Normalize().Crop, persisted.Crop);
                Assert.Equal(first.Normalize().Crop, (await catalog.UndoEditAsync(photo.Id))!.Crop);
                Assert.Equal(second.Normalize().Crop, (await catalog.RedoEditAsync(photo.Id))!.Crop);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task OpticsCorrectionsPersistAndParticipateInUndoRedo()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                var first = new EditRecipe(Optics: new OpticsCorrections(-12, 20, 16, 45));
                var second = first with { Optics = new OpticsCorrections(18, 42, -10, 62) };
                await catalog.UpdateEditAsync(photo.Id, first);
                await catalog.UpdateEditAsync(photo.Id, second);

                var persisted = (await catalog.GetPhotoAsync(photo.Id))!.Edit.Normalize();
                Assert.Equal(second.Normalize().Optics, persisted.Optics);
                Assert.Equal(first.Normalize().Optics, (await catalog.UndoEditAsync(photo.Id))!.Optics);
                Assert.Equal(second.Normalize().Optics, (await catalog.RedoEditAsync(photo.Id))!.Optics);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task VersionTwoCatalogMigratesCurrentEditAndOriginal()
    {
        var root = CreateTemporaryDirectory();
        var databasePath = Path.Combine(root, "catalog.db");
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await catalog.DisposeAsync();
            var legacyRecipe = new EditRecipe(ExposureEv: 1.5, Contrast: 20);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE edit_snapshots;
                    DROP TABLE edit_heads;
                    DROP TABLE edit_revisions;
                    UPDATE photos SET edit_json=$recipe WHERE id=$photo;
                    PRAGMA user_version=2;
                    """;
                command.Parameters.AddWithValue("$recipe", JsonSerializer.Serialize(legacyRecipe));
                command.Parameters.AddWithValue("$photo", photo.Id.ToString());
                await command.ExecuteNonQueryAsync();
            }

            await using var migrated = new SqlitePhotoCatalog(databasePath);
            await migrated.InitializeAsync();
            var history = await migrated.GetEditHistoryAsync(photo.Id);

            Assert.Equal(4, await ReadSchemaVersionAsync(databasePath));
            Assert.Equal(2, history.Revisions.Count);
            Assert.Equal(EditRecipe.Default, history.Revisions[0].Recipe);
            Assert.Equal(legacyRecipe.Normalize(), history.Current!.Recipe);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ConcurrentUpdatesRemainContiguousAndUseOneCurrentRecipe()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var (catalog, photo) = await CreateCatalogWithPhotoAsync(root);
            await using (catalog)
            {
                var updates = Enumerable.Range(1, 8)
                    .Select(value => catalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: value / 10d)));
                await Task.WhenAll(updates);

                var history = await catalog.GetEditHistoryAsync(photo.Id);
                Assert.Equal(9, history.Revisions.Count);
                Assert.Equal(Enumerable.Range(0, 9).Select(value => (long)value),
                    history.Revisions.Select(revision => revision.Sequence));
                Assert.Equal(history.Current!.Recipe, (await catalog.GetPhotoAsync(photo.Id))!.Edit);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CancellationAndMissingIdsAreReported()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.InitializeAsync(cancelled.Token));

            await catalog.InitializeAsync();
            var missing = Guid.NewGuid();
            await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.GetEditHistoryAsync(missing));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.GetEditSnapshotsAsync(missing));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task<(SqlitePhotoCatalog Catalog, PhotoAsset Photo)> CreateCatalogWithPhotoAsync(string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "photo.jpg"), "image");
        var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
        await catalog.ImportFolderAsync(root, includeSubfolders: false);
        return (catalog, (await catalog.GetPhotosAsync()).Single());
    }

    private static async Task<int> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openlume-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(path, recursive: true);
    }
}
