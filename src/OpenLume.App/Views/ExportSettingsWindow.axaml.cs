using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.App.Views;

public sealed partial class ExportSettingsWindow : Window
{
    public ExportSettingsWindow()
    {
        InitializeComponent();
        this.FindControl<ComboBox>("FormatPicker")!.SelectionChanged += (_, _) =>
            this.FindControl<NumericUpDown>("QualityPicker")!.IsEnabled =
                this.FindControl<ComboBox>("FormatPicker")!.SelectedIndex == 0;
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void Continue_OnClick(object? sender, RoutedEventArgs e) => Close(new ExportOptions(
        this.FindControl<ComboBox>("FormatPicker")!.SelectedIndex == 1 ? ImageExportFormat.Png : ImageExportFormat.Jpeg,
        (int)(this.FindControl<NumericUpDown>("QualityPicker")!.Value ?? 92),
        (int)(this.FindControl<NumericUpDown>("SizePicker")!.Value ?? 0)));
}
