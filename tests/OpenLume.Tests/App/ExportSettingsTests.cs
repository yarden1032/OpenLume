using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using OpenLume.App.Views;

namespace OpenLume.Tests.App;

[Collection("Renderer budget")]
public sealed class ExportSettingsTests
{
    [AvaloniaFact]
    public void DialogLoadsAndDisablesJpegQualityForPng()
    {
        var dialog = new ExportSettingsWindow();
        try
        {
            var format = dialog.FindControl<ComboBox>("FormatPicker")!;
            var quality = dialog.FindControl<NumericUpDown>("QualityPicker")!;
            var size = dialog.FindControl<NumericUpDown>("SizePicker")!;
            Assert.Equal(92m, quality.Value);
            Assert.Equal(0m, size.Value);
            Assert.True(quality.IsEnabled);
            format.SelectedIndex = 1;
            Assert.False(quality.IsEnabled);
            format.SelectedIndex = 0;
            Assert.True(quality.IsEnabled);
        }
        finally { dialog.Close(); }
    }
}
