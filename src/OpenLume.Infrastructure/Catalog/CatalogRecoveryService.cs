using System.Text.Json;
using OpenLume.Core.Abstractions;

namespace OpenLume.Infrastructure.Catalog;

public sealed class CatalogRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPhotoCatalog _catalog;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly string _noticePath;

    public CatalogRecoveryService(string appDataDirectory, string databasePath, IPhotoCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(catalog);

        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = Path.Combine(Path.GetFullPath(appDataDirectory), "backups");
        _noticePath = Path.Combine(Path.GetFullPath(appDataDirectory), "catalog-recovery-notice.json");
        _catalog = catalog;
    }

    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (PathsEqual(fullDestinationPath, _databasePath))
        {
            throw new IOException("A catalog backup cannot replace the active catalog.");
        }

        var directory = Path.GetDirectoryName(fullDestinationPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullDestinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await _catalog.BackupAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<CatalogRestorePlan> PrepareRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        await _catalog.ValidateBackupAsync(fullBackupPath, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_backupDirectory);
        var safetyBackupPath = Path.Combine(
            _backupDirectory,
            $"catalog-before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.db");
        await _catalog.BackupAsync(safetyBackupPath, cancellationToken).ConfigureAwait(false);
        return new CatalogRestorePlan(fullBackupPath, safetyBackupPath);
    }

    public async Task<CatalogRecoveryNotice> RestoreAfterShutdownAsync(
        CatalogRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await WriteNoticeAsync(
            new CatalogRecoveryNotice(
                Succeeded: false,
                Message: "Catalog restore did not finish. The current catalog may be unchanged; the safety backup is available below.",
                SafetyBackupPath: plan.SafetyBackupPath),
            cancellationToken).ConfigureAwait(false);

        CatalogRecoveryNotice notice;
        try
        {
            await using var restoredCatalog = new SqlitePhotoCatalog(_databasePath);
            await restoredCatalog.RestoreBackupAsync(plan.BackupPath, cancellationToken).ConfigureAwait(false);
            notice = new CatalogRecoveryNotice(
                Succeeded: true,
                Message: "The catalog was restored successfully.",
                SafetyBackupPath: plan.SafetyBackupPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            notice = new CatalogRecoveryNotice(
                Succeeded: false,
                Message: $"The catalog could not be restored: {exception.Message}",
                SafetyBackupPath: plan.SafetyBackupPath);
        }

        await WriteNoticeAsync(notice, cancellationToken).ConfigureAwait(false);
        return notice;
    }

    public async Task<CatalogRecoveryNotice> RecordFailureAsync(
        CatalogRestorePlan plan,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var notice = new CatalogRecoveryNotice(
            Succeeded: false,
            Message: message,
            SafetyBackupPath: plan.SafetyBackupPath);
        await WriteNoticeAsync(notice, cancellationToken).ConfigureAwait(false);
        return notice;
    }

    public async Task<CatalogRecoveryNotice?> ReadNoticeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_noticePath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_noticePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CatalogRecoveryNotice>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new CatalogRecoveryNotice(
                Succeeded: false,
                Message: "The last catalog recovery result could not be read.",
                SafetyBackupPath: null);
        }
    }

    public void AcknowledgeNotice()
    {
        if (File.Exists(_noticePath)) File.Delete(_noticePath);
    }

    private async Task WriteNoticeAsync(CatalogRecoveryNotice notice, CancellationToken cancellationToken)
    {
        var temporaryPath = _noticePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(notice, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _noticePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record CatalogRestorePlan(string BackupPath, string SafetyBackupPath);

public sealed record CatalogRecoveryNotice(bool Succeeded, string Message, string? SafetyBackupPath);
