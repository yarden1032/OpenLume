using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class CatalogMigrationAndScaleTests
{
    [Fact]
    public async Task VersionOneCatalogMigratesWithoutLosingPhotoState()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var photoPath = Path.Combine(root, "legacy.jpg");
            await File.WriteAllTextAsync(photoPath, "legacy");
            var databasePath = Path.Combine(root, "catalog.db");
            var id = Guid.NewGuid();
            await CreateVersionOneCatalogAsync(databasePath, id, photoPath);

            await using (var catalog = new SqlitePhotoCatalog(databasePath))
            {
                await catalog.InitializeAsync();
                var photo = await catalog.GetPhotoAsync(id);

                Assert.NotNull(photo);
                Assert.Equal(4, photo!.Rating);
                Assert.Equal(PickState.Pick, photo.PickState);
                Assert.Equal(1.25, photo.Edit.ExposureEv);
                Assert.Equal("legacy analysis", photo.AiSummary);
                Assert.Equal(Path.GetDirectoryName(photoPath), (await catalog.GetFoldersAsync()).Single().Path);
                Assert.Single(await catalog.GetPendingMetadataAsync(10));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticHundredThousandPhotoCatalogSupportsPagedIndexedQueries()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "catalog.db");
            await using (var catalog = new SqlitePhotoCatalog(databasePath))
            {
                await catalog.InitializeAsync();
            }

            await InsertSyntheticPhotosAsync(databasePath, 100_000);
            await using (var reopened = new SqlitePhotoCatalog(databasePath))
            {
                var stopwatch = Stopwatch.StartNew();
                var total = await reopened.GetPhotoCountAsync();
                var page = await reopened.QueryAsync(new CatalogFilter(
                    MinimumRating: 4,
                    PickState: PickState.Pick,
                    Extension: ".jpg",
                    SearchText: "photo-09",
                    Offset: 25,
                    Limit: 100));
                stopwatch.Stop();

                Assert.Equal(100_000, total);
                Assert.Equal(100, page.Count);
                Assert.All(page, photo =>
                {
                    Assert.True(photo.Rating >= 4);
                    Assert.Equal(PickState.Pick, photo.PickState);
                    Assert.Equal(".jpg", photo.Extension);
                    Assert.Contains("photo-09", photo.FileName, StringComparison.Ordinal);
                });
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"Paged 100k catalog query took {stopwatch.Elapsed}.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateVersionOneCatalogAsync(string databasePath, Guid id, string photoPath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE photos (
                id TEXT PRIMARY KEY,
                original_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                file_name TEXT NOT NULL,
                extension TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                imported_at TEXT NOT NULL,
                captured_at TEXT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                rating INTEGER NOT NULL DEFAULT 0,
                pick_state INTEGER NOT NULL DEFAULT 0,
                edit_json TEXT NOT NULL,
                ai_summary TEXT NULL,
                ai_technical REAL NULL,
                ai_aesthetic REAL NULL,
                ai_suggested_pick INTEGER NULL,
                ai_tags_json TEXT NULL,
                ai_edit_json TEXT NULL
            );
            INSERT INTO photos(
                id, original_path, file_name, extension, file_size, imported_at,
                rating, pick_state, edit_json, ai_summary)
            VALUES($id, $path, 'legacy.jpg', '.jpg', 6, $importedAt, 4, 1, $edit, 'legacy analysis');
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$path", photoPath);
        command.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$edit", "{\"exposureEv\":1.25}");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSyntheticPhotosAsync(string databasePath, int count)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO photos(
                id, original_path, directory_path, file_name, extension, file_size,
                imported_at, rating, pick_state, edit_json, source_last_write_ticks, metadata_state)
            VALUES($id, $path, $directory, $name, $extension, 1, $importedAt, $rating, $pick, '{}', 0, 0)
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var directory = command.Parameters.Add("$directory", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var extension = command.Parameters.Add("$extension", SqliteType.Text);
        var importedAt = command.Parameters.Add("$importedAt", SqliteType.Text);
        var rating = command.Parameters.Add("$rating", SqliteType.Integer);
        var pick = command.Parameters.Add("$pick", SqliteType.Integer);
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        for (var index = 0; index < count; index++)
        {
            var fileName = $"photo-{index:D6}.jpg";
            id.Value = Guid.NewGuid().ToString();
            path.Value = Path.Combine("Z:\\synthetic", fileName);
            directory.Value = "Z:\\synthetic";
            name.Value = fileName;
            extension.Value = ".jpg";
            importedAt.Value = timestamp;
            rating.Value = index % 6;
            pick.Value = index % 2;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openlume-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
