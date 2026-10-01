using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenLume.App.Views;

public sealed partial class CatalogRestoreConfirmationWindow : Window
{
    public CatalogRestoreConfirmationWindow() => InitializeComponent();

    public CatalogRestoreConfirmationWindow(string backupPath)
        : this()
    {
        this.FindControl<TextBlock>("BackupPath")!.Text = backupPath;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}
