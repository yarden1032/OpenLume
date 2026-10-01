using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenLume.App.Views;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.App;

[Collection("Renderer budget")]
public sealed class CatalogRecoveryWindowTests
{
    [AvaloniaFact]
    public void RecoveryDialogsLoadSelectedBackupAndNextLaunchResult()
    {
        var backupPath = Path.Combine(Path.GetTempPath(), "OpenLume selected backup.db");
        var menu = new CatalogRecoveryWindow();
        var confirmation = new CatalogRestoreConfirmationWindow(backupPath);
        var notice = new CatalogRecoveryNoticeWindow(
            new CatalogRecoveryNotice(false, "Restore failed safely.", "C:\\OpenLume\\backups\\catalog-before-restore.db"));
        try
        {
            Assert.Equal("Catalog safety", menu.Title);
            Assert.Equal(backupPath, confirmation.FindControl<TextBlock>("BackupPath")!.Text);
            Assert.Equal("Catalog restore failed", notice.FindControl<TextBlock>("Heading")!.Text);
            Assert.Contains("Restore failed safely.", notice.FindControl<TextBlock>("Message")!.Text);
            Assert.Contains("catalog-before-restore.db", notice.FindControl<TextBlock>("SafetyBackup")!.Text);
        }
        finally
        {
            menu.Close();
            confirmation.Close();
            notice.Close();
        }
    }
}
