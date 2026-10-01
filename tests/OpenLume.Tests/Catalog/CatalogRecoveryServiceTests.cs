using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class CatalogRecoveryServiceTests
{
    [Fact]
    public async Task RestoreAfterShutdownCreatesSafetyCopyAndReportsResultOnNextLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlume-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var activePath = Path.Combine(root, "catalog.db");
            var activePhotos = Directory.CreateDirectory(Path.Combine(root, "active-photos")).FullName;
            var backupPhotos = Directory.CreateDirectory(Path.Combine(root, "backup-photos")).FullName;
            await File.WriteAllTextAsync(Path.Combine(activePhotos, "current.jpg"), "active", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(backupPhotos, "restored.jpg"), "backup", cancellationToken);

            await using var activeCatalog = new SqlitePhotoCatalog(activePath);
            await activeCatalog.ImportFolderAsync(activePhotos, includeSubfolders: false, cancellationToken: cancellationToken);
            var backupCatalog = new SqlitePhotoCatalog(Path.Combine(root, "backup-source.db"));
            await backupCatalog.ImportFolderAsync(backupPhotos, includeSubfolders: false, cancellationToken: cancellationToken);
            var backupPhoto = Assert.Single(await backupCatalog.GetPhotosAsync(cancellationToken));
            await backupCatalog.UpdateEditAsync(backupPhoto.Id, new EditRecipe(ExposureEv: 1.25), cancellationToken);
            var selectedBackup = Path.Combine(root, "selected-backup.db");
            var backupSourcePath = Path.Combine(root, "backup-source.db");
            var backupRecovery = new CatalogRecoveryService(root, backupSourcePath, backupCatalog);
            await backupRecovery.CreateBackupAsync(selectedBackup, cancellationToken);
            await backupCatalog.UpdateRatingAsync(backupPhoto.Id, 5, cancellationToken);
            await backupRecovery.CreateBackupAsync(selectedBackup, cancellationToken);
            await Assert.ThrowsAsync<IOException>(() => backupRecovery.CreateBackupAsync(backupSourcePath, cancellationToken));
            await backupCatalog.ValidateBackupAsync(selectedBackup, cancellationToken);
            await backupCatalog.DisposeAsync();

            var recovery = new CatalogRecoveryService(root, activePath, activeCatalog);
            var plan = await recovery.PrepareRestoreAsync(selectedBackup, cancellationToken);
            Assert.True(File.Exists(plan.SafetyBackupPath));
            await activeCatalog.DisposeAsync();

            await using (var safetyCatalog = new SqlitePhotoCatalog(plan.SafetyBackupPath))
            {
                Assert.Equal("current.jpg", Assert.Single(await safetyCatalog.GetPhotosAsync(cancellationToken)).FileName);
            }

            var notice = await recovery.RestoreAfterShutdownAsync(plan, cancellationToken);

            Assert.True(notice.Succeeded);
            Assert.Equal(plan.SafetyBackupPath, notice.SafetyBackupPath);
            await using (var restoredCatalog = new SqlitePhotoCatalog(activePath))
            {
                var restored = Assert.Single(await restoredCatalog.GetPhotosAsync(cancellationToken));
                Assert.Equal("restored.jpg", restored.FileName);
                Assert.Equal(1.25, restored.Edit.ExposureEv);
                Assert.Equal(5, restored.Rating);
            }

            Assert.Equal(notice, await recovery.ReadNoticeAsync(cancellationToken));
            recovery.AcknowledgeNotice();
            Assert.Null(await recovery.ReadNoticeAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidBackupIsRejectedBeforeSafetyBackupIsCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlume-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var activePath = Path.Combine(root, "catalog.db");
            await using var activeCatalog = new SqlitePhotoCatalog(activePath);
            await activeCatalog.InitializeAsync(cancellationToken);
            var recovery = new CatalogRecoveryService(root, activePath, activeCatalog);
            var invalidBackupPath = Path.Combine(root, "invalid.db");
            await File.WriteAllTextAsync(invalidBackupPath, "not a sqlite catalog", cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => recovery.PrepareRestoreAsync(invalidBackupPath, cancellationToken));

            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
            Assert.Null(await recovery.ReadNoticeAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupChangedAfterPreparationLeavesActiveCatalogAndReportsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "openlume-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var activePath = Path.Combine(root, "catalog.db");
            var activePhotos = Directory.CreateDirectory(Path.Combine(root, "active-photos")).FullName;
            var backupPhotos = Directory.CreateDirectory(Path.Combine(root, "backup-photos")).FullName;
            await File.WriteAllTextAsync(Path.Combine(activePhotos, "current.jpg"), "active", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(backupPhotos, "backup.jpg"), "backup", cancellationToken);

            await using var activeCatalog = new SqlitePhotoCatalog(activePath);
            await activeCatalog.ImportFolderAsync(activePhotos, includeSubfolders: false, cancellationToken: cancellationToken);
            var backupCatalog = new SqlitePhotoCatalog(Path.Combine(root, "backup-source.db"));
            await backupCatalog.ImportFolderAsync(backupPhotos, includeSubfolders: false, cancellationToken: cancellationToken);
            var backupPath = Path.Combine(root, "selected-backup.db");
            await backupCatalog.BackupAsync(backupPath, cancellationToken);
            await backupCatalog.DisposeAsync();
            var recovery = new CatalogRecoveryService(root, activePath, activeCatalog);
            var plan = await recovery.PrepareRestoreAsync(backupPath, cancellationToken);
            await activeCatalog.DisposeAsync();

            await File.WriteAllTextAsync(backupPath, "changed after validation", cancellationToken);
            var notice = await recovery.RestoreAfterShutdownAsync(plan, cancellationToken);

            Assert.False(notice.Succeeded);
            Assert.Equal(plan.SafetyBackupPath, notice.SafetyBackupPath);
            await using (var stillActive = new SqlitePhotoCatalog(activePath))
            {
                Assert.Equal("current.jpg", Assert.Single(await stillActive.GetPhotosAsync(cancellationToken)).FileName);
            }
            Assert.Equal(notice, await recovery.ReadNoticeAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
