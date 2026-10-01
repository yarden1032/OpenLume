using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.App.Views;

public sealed partial class CatalogRecoveryNoticeWindow : Window
{
    public CatalogRecoveryNoticeWindow() => InitializeComponent();

    public CatalogRecoveryNoticeWindow(CatalogRecoveryNotice notice)
        : this()
    {
        this.FindControl<TextBlock>("Heading")!.Text = notice.Succeeded ? "Catalog restored" : "Catalog restore failed";
        this.FindControl<TextBlock>("Message")!.Text = notice.Message;
        var safetyBackup = this.FindControl<TextBlock>("SafetyBackup")!;
        safetyBackup.Text = string.IsNullOrWhiteSpace(notice.SafetyBackupPath)
            ? "No safety backup path was recorded."
            : $"Safety backup of the previous catalog: {notice.SafetyBackupPath}";
    }

    private void Continue_OnClick(object? sender, RoutedEventArgs e) => Close();
}
