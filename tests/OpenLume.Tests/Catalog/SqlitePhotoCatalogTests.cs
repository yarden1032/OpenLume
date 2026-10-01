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
            var first = await catalog.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken); var second = await catalog.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, first.Imported); Assert.Equal(0, first.AlreadyPresent); Assert.Equal(1, second.AlreadyPresent); Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
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
            Assert.Equal(0, (await catalog.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken)).Imported); Assert.Equal(1, (await catalog.ImportFolderAsync(root, true, cancellationToken: TestContext.Current.CancellationToken)).Imported);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpdatesPersistAndRatingIsClamped()
    {
        var root = Temp(); try
        {
            var path = Path.Combine(root, "a.png"); File.WriteAllText(path, "x"); await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "db")); await catalog.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken); var photo = (await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
            var edit = new EditRecipe(ExposureEv: 99); await catalog.UpdateEditAsync(photo.Id, edit, cancellationToken: TestContext.Current.CancellationToken); await catalog.UpdateRatingAsync(photo.Id, 99, cancellationToken: TestContext.Current.CancellationToken); await catalog.UpdatePickStateAsync(photo.Id, PickState.Pick, cancellationToken: TestContext.Current.CancellationToken);
            var suggestion = new DevelopSuggestion(
                Guid.NewGuid(), "Lift the subject", .84, new EditRecipe(ExposureEv: .6, Highlights: -20),
                [new EditParameterDecision("Exposure", "The subject is underexposed")], [],
                DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow);
            var historyBeforeProposal = await catalog.GetEditHistoryAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken);
            await catalog.UpdateAnalysisAsync(photo.Id, new PhotoAnalysis("great", .8, .9, true, PortraitTags, edit, suggestion), cancellationToken: TestContext.Current.CancellationToken);
            var updated = await catalog.GetPhotoAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken); Assert.NotNull(updated); Assert.Equal(5, updated!.Rating); Assert.Equal(PickState.Pick, updated.PickState); Assert.Equal(5, updated.Edit.ExposureEv); Assert.Equal("great", updated.AiSummary);
            Assert.NotNull(updated.AiSuggestion);
            Assert.Equal(suggestion.Id, updated.AiSuggestion!.Id);
            Assert.Equal(DevelopSuggestionStatus.Pending, updated.AiSuggestion.Status);

            await catalog.UpdateDevelopSuggestionStatusAsync(photo.Id, suggestion.Id, DevelopSuggestionStatus.Rejected, cancellationToken: TestContext.Current.CancellationToken);
            var rejected = await catalog.GetPhotoAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(DevelopSuggestionStatus.Rejected, rejected!.AiSuggestion!.Status);
            Assert.Equal(5, rejected.Edit.ExposureEv);
            Assert.Equal(historyBeforeProposal.Revisions.Count, (await catalog.GetEditHistoryAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken)).Revisions.Count);
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
            await File.WriteAllTextAsync(Path.Combine(root, "ai.jpg"), "image", cancellationToken: TestContext.Current.CancellationToken);
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false, cancellationToken: TestContext.Current.CancellationToken);
            var photo = Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            var suggestion = new DevelopSuggestion(
                Guid.NewGuid(), "Recover highlights", .91,
                new EditRecipe(ExposureEv: .4, Highlights: -45, Vibrance: 12),
                [new EditParameterDecision("Highlights", "Protect bright detail")], [],
                DevelopSuggestionStatus.Pending, DateTimeOffset.UtcNow);
            await catalog.UpdateAnalysisAsync(
                photo.Id,
                new PhotoAnalysis("Balanced correction", .8, .8, true, PortraitTags, suggestion.Recipe, suggestion), cancellationToken: TestContext.Current.CancellationToken);
            var before = await catalog.GetEditHistoryAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken);
            var merged = suggestion.MergeOnto(photo.Edit);

            await catalog.ApplyDevelopSuggestionAsync(photo.Id, suggestion.Id, merged, cancellationToken: TestContext.Current.CancellationToken);

            var after = await catalog.GetEditHistoryAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken);
            var updated = await catalog.GetPhotoAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(before.Revisions.Count + 1, after.Revisions.Count);
            Assert.Equal(merged, after.Current!.Recipe);
            Assert.Equal(merged, updated!.Edit);
            Assert.Equal(DevelopSuggestionStatus.Applied, updated.AiSuggestion!.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => catalog.ApplyDevelopSuggestionAsync(photo.Id, suggestion.Id, merged, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(after.Revisions.Count, (await catalog.GetEditHistoryAsync(photo.Id, cancellationToken: TestContext.Current.CancellationToken)).Revisions.Count);
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
            await catalog.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(File.Exists(database));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => catalog.UpdateRatingAsync(Guid.NewGuid(), 3, cancellationToken: TestContext.Current.CancellationToken));
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
            await File.WriteAllTextAsync(sourcePhoto, "original image", cancellationToken: TestContext.Current.CancellationToken);
            var catalogPath = Path.Combine(root, "catalog.db");
            var backupPath = Path.Combine(root, "backup.db");
            await using var catalog = new SqlitePhotoCatalog(catalogPath);
            await catalog.ImportFolderAsync(root, includeSubfolders: false, cancellationToken: TestContext.Current.CancellationToken);
            var photo = Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            await catalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: 1.25), cancellationToken: TestContext.Current.CancellationToken);
            await catalog.UpdateRatingAsync(photo.Id, 4, cancellationToken: TestContext.Current.CancellationToken);
            await catalog.UpdatePickStateAsync(photo.Id, PickState.Pick, cancellationToken: TestContext.Current.CancellationToken);
            await catalog.BackupAsync(backupPath, cancellationToken: TestContext.Current.CancellationToken);
            var backupBytes = await File.ReadAllBytesAsync(backupPath, cancellationToken: TestContext.Current.CancellationToken);

            await catalog.UpdateEditAsync(photo.Id, new EditRecipe(ExposureEv: -2), cancellationToken: TestContext.Current.CancellationToken);
            await catalog.UpdateRatingAsync(photo.Id, 1, cancellationToken: TestContext.Current.CancellationToken);
            await catalog.RestoreBackupAsync(backupPath, cancellationToken: TestContext.Current.CancellationToken);

            var restored = Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1.25, restored.Edit.ExposureEv);
            Assert.Equal(4, restored.Rating);
            Assert.Equal(PickState.Pick, restored.PickState);
            Assert.Equal("original image", await File.ReadAllTextAsync(sourcePhoto, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => catalog.BackupAsync(backupPath, cancellationToken: TestContext.Current.CancellationToken));
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
            await File.WriteAllTextAsync(photoPath, "original", cancellationToken: TestContext.Current.CancellationToken);
            await using var catalog = new SqlitePhotoCatalog(Path.Combine(root, "catalog.db"));
            await catalog.ImportFolderAsync(root, includeSubfolders: false, cancellationToken: TestContext.Current.CancellationToken);
            var before = Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            var invalidBackup = Path.Combine(root, "bad.db");
            await File.WriteAllTextAsync(invalidBackup, "not a sqlite database", cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<Exception>(() => catalog.RestoreBackupAsync(invalidBackup, cancellationToken: TestContext.Current.CancellationToken));

            var after = Assert.Single(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Edit, after.Edit);
            Assert.Empty(Directory.GetFiles(root, ".catalog-restore-*.tmp"));
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
            await catalog.BackupAsync(backup, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(File.Exists(backup));
            await catalog.RestoreBackupAsync(backup, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(await catalog.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken));
            await catalog.DisposeAsync();
            Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "openlume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
