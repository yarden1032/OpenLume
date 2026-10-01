using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenLume.App.Views;

public enum CatalogRecoveryAction
{
    Backup,
    Restore
}

public sealed partial class CatalogRecoveryWindow : Window
{
    public CatalogRecoveryWindow() => InitializeComponent();

    private void CreateBackup_OnClick(object? sender, RoutedEventArgs e) => Close(CatalogRecoveryAction.Backup);

    private void RestoreBackup_OnClick(object? sender, RoutedEventArgs e) => Close(CatalogRecoveryAction.Restore);

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}
