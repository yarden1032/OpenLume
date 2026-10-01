using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
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
            var suggestion = new DevelopSuggestion(
                Guid.NewGuid(), "Lift the subject", .84, new EditRecipe(ExposureEv: .6, Highlights: -20),
                [new EditParameterDecision("Exposure", "The subject is underexposed")], [],
                DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow);
            var historyBeforeProposal = await catalog.GetEditHistoryAsync(photo.Id);
            await catalog.UpdateAnalysisAsync(photo.Id, new PhotoAnalysis("great", .8, .9, true, PortraitTags, edit, suggestion));
            var updated = await catalog.GetPhotoAsync(photo.Id); Assert.NotNull(updated); Assert.Equal(5, updated!.Rating); Assert.Equal(PickState.Pick, updated.PickState); Assert.Equal(5, updated.Edit.ExposureEv); Assert.Equal("great", updated.AiSummary);
            Assert.NotNull(updated.AiSuggestion);
            Assert.Equal(suggestion.Id, updated.AiSuggestion!.Id);
            Assert.Equal(DevelopSuggestionStatus.Pending, updated.AiSuggestion.Status);

            await catalog.UpdateDevelopSuggestionStatusAsync(photo.Id, suggestion.Id, DevelopSuggestionStatus.Rejected);
            var rejected = await catalog.GetPhotoAsync(photo.Id);
            Assert.Equal(DevelopSuggestionStatus.Rejected, rejected!.AiSuggestion!.Status);
            Assert.Equal(5, rejected.Edit.ExposureEv);
            Assert.Equal(historyBeforeProposal.Revisions.Count, (await catalog.GetEditHistoryAsync(photo.Id)).Revisions.Count);
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
    public async Task ApplyingAiSuggestionAtomicallyCreatesOneRevisionAndMarksItApplied()
    {
        var root = Temp();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "ai.jpg"), "image");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var photo = Assert.Single(await catalog.GetPhotosAsync());
            var suggestion = new DevelopSuggestion(
                Guid.NewGuid(), "Recover highlights", .91,
                new EditRecipe(ExposureEv: .4, Highlights: -45, Vibrance: 12),
                [new EditParameterDecision("Highlights", "Protect bright detail")], [],
                DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow);
            await catalog.UpdateAnalysisAsync(
                photo.Id,
                new PhotoAnalysis("Balanced correction", .8, .8, true, PortraitTags, suggestion.Recipe, suggestion));
            var before = await catalog.GetEditHistoryAsync(photo.Id);
            var merged = suggestion.MergeOnto(photo.Edit);

            await catalog.ApplyDevelopSuggestionAsync(photo.Id, suggestion.Id, merged);

            var after = await catalog.GetEditHistoryAsync(photo.Id);
            var updated = await catalog.GetPhotoAsync(photo.Id);
            Assert.Equal(before.Revisions.Count + 1, after.Revisions.Count);
            Assert.Equal(merged, after.Current!.Recipe);
            Assert.Equal(merged, updated!.Edit);
            Assert.Equal(DevelopSuggestionStatus.Applied, updated.AiSuggestion!.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => catalog.ApplyDevelopSuggestionAsync(photo.Id, suggestion.Id, merged));
            Assert.Equal(after.Revisions.Count, (await catalog.GetEditHistoryAsync(photo.Id)).Revisions.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
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

    [Fact]
    public async Task BackupAndRestoreRoundTripCatalogWithoutOverwritingBackup()
    {
        var root = Temp();
        try
        {
            var sourcePhoto = Path.Combine(root, "source.jpg");
            await File.WriteAllTextAsync(sourcePhoto, "original image");
            var catalogPath = Path.Combine(root, "catalog.db");
            var backupPath = Path.Combine(root, "backup.db");
            await using var catalog = new SqlitePhotoCatalog(catalogPath);
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var photo = Assert.Single(await catalog.GetPhotosAsync());
            await catalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: 1.25));
            await catalog.UpdateRatingAsync(photo.Id, 4);
            await catalog.UpdatePickStateAsync(photo.Id, PickState.Pick);
            await catalog.BackupAsync(backupPath);
            await catalog.ValidateBackupAsync(backupPath);
            var backupBytes = await File.ReadAllBytesAsync(backupPath);

            await catalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: -2));
            await catalog.UpdateRatingAsync(photo.Id, 1);
            await catalog.RestoreBackupAsync(backupPath);

            var restored = Assert.Single(await catalog.GetPhotosAsync());
            Assert.Equal(1.25, restored.Edit.ExposureEv);
            Assert.Equal(4, restored.Rating);
            Assert.Equal(PickState.Pick, restored.PickState);
            Assert.Equal("original image", await File.ReadAllTextAsync(sourcePhoto));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
            await Assert.ThrowsAsync<IOException>(() => catalog.BackupAsync(backupPath));
            Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidCatalogBackupLeavesCurrentCatalogUntouched()
    {
        var root = Temp();
        try
        {
            var photoPath = Path.Combine(root, "source.jpg");
            await File.WriteAllTextAsync(photoPath, "original");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var before = Assert.Single(await catalog.GetPhotosAsync());
            var invalidBackup = Path.Combine(root, "bad.db");
            await File.WriteAllTextAsync(invalidBackup, "not a sqlite database");

            await Assert.ThrowsAnyAsync<Exception>(() => catalog.RestoreBackupAsync(invalidBackup));

            var after = Assert.Single(await catalog.GetPhotosAsync());
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Edit, after.Edit);
            Assert.Empty(Directory.GetFiles(root, ".catalog-restore-*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptySqliteDatabaseIsNotAcceptedAsCatalogBackup()
    {
        var root = Temp();
        try
        {
            var photoPath = Path.Combine(root, "source.jpg");
            await File.WriteAllTextAsync(photoPath, "original");
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false);
            var before = Assert.Single(await catalog.GetPhotosAsync());

            var emptySqliteBackup = Path.Combine(root, "empty.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = emptySqliteBackup,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=0;";
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ValidateBackupAsync(emptySqliteBackup));
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.RestoreBackupAsync(emptySqliteBackup));

            var after = Assert.Single(await catalog.GetPhotosAsync());
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Edit, after.Edit);
            Assert.Empty(Directory.GetFiles(root, ".catalog-restore-*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [SuppressMessage("xUnit", "xUnit1051", Justification = "xUnit 2.9 does not expose a per-test cancellation token.")]
    public async Task OlderCatalogBackupIsMigratedBeforeItReplacesTheCurrentCatalog()
    {
        var root = Temp();
        try
        {
            var legacyDirectory = Directory.CreateDirectory(Path.Combine(root, "legacy-photos")).FullName;
            var currentDirectory = Directory.CreateDirectory(Path.Combine(root, "current-photos")).FullName;
            var legacyImage = Path.Combine(legacyDirectory, "legacy.jpg");
            var currentImage = Path.Combine(currentDirectory, "current.jpg");
            await File.WriteAllTextAsync(legacyImage, "legacy source");
            await File.WriteAllTextAsync(currentImage, "current source");
            var legacyPath = Path.Combine(root, "legacy.db");
            var backupPath = Path.Combine(root, "legacy-backup.db");
            await using (var legacyCatalog = new SqlitePhotoCatalog(legacyPath))
            {
                await legacyCatalog.ImportFolderAsync(legacyDirectory, includeSubfolders: false);
                var photo = (await legacyCatalog.GetPhotosAsync()).Single(item => item.FileName == "legacy.jpg");
                await legacyCatalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: 1.5, Contrast: 20));
            }

            var legacyConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(legacyConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE edit_snapshots; DROP TABLE edit_heads; DROP TABLE edit_revisions; PRAGMA user_version=2;";
                await command.ExecuteNonQueryAsync();
            }

            File.Copy(legacyPath, backupPath);
            var backupBeforeValidation = await File.ReadAllBytesAsync(backupPath);
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "active.db"));
            await catalog.InitializeAsync();
            await catalog.ImportFolderAsync(currentDirectory, includeSubfolders: false);
            var current = (await catalog.GetPhotosAsync()).Single(item => item.FileName == "current.jpg");
            await catalog.UpdateRatingAsync(current.Id, 5);

            await catalog.ValidateBackupAsync(backupPath);
            Assert.Equal(backupBeforeValidation, await File.ReadAllBytesAsync(backupPath));
            await catalog.RestoreBackupAsync(backupPath);

            var restored = Assert.Single(await catalog.GetPhotosAsync());
            Assert.Equal("legacy.jpg", restored.FileName);
            Assert.Equal(1.5, restored.Edit.ExposureEv);
            Assert.Equal(20, restored.Edit.Contrast);
            Assert.Equal(EditRecipe.Default, (await catalog.UndoEditAsync(restored.Id)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BackupOfUninitializedCatalogCreatesRestorableEmptyCatalog()
    {
        var root = Temp();
        try
        {
            var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            var backup = Path.Combine(root, "backup.db");
            await catalog.BackupAsync(backup);
            Assert.True(File.Exists(backup));
            await catalog.RestoreBackupAsync(backup);
            Assert.Empty(await catalog.GetPhotosAsync());
            await catalog.DisposeAsync();
            Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "openlume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
