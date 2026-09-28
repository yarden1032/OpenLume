using CommunityToolkit.Mvvm.ComponentModel;
using OpenLume.Core.Domain;

namespace OpenLume.App.ViewModels;

public sealed class HslChannelViewModel : ObservableObject
{
    private readonly Action _changed;
    private double _hue;
    private double _saturation;
    private double _luminance;

    public HslChannelViewModel(
        string key,
        string name,
        string swatch,
        Action changed)
    {
        Key = key;
        Name = name;
        Swatch = swatch;
        _changed = changed;
    }

    public string Key { get; }
    public string Name { get; }
    public string Swatch { get; }

    public double Hue
    {
        get => _hue;
        set => SetMixerValue(ref _hue, value);
    }

    public double Saturation
    {
        get => _saturation;
        set => SetMixerValue(ref _saturation, value);
    }

    public double Luminance
    {
        get => _luminance;
        set => SetMixerValue(ref _luminance, value);
    }

    public HslChannelAdjustment ToAdjustment() =>
        new HslChannelAdjustment(Hue, Saturation, Luminance).Normalize();

    public void Load(HslChannelAdjustment? adjustment)
    {
        var normalized = (adjustment ?? new HslChannelAdjustment()).Normalize();
        Hue = normalized.Hue;
        Saturation = normalized.Saturation;
        Luminance = normalized.Luminance;
    }

    private void SetMixerValue(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, Math.Clamp(value, -100, 100), propertyName))
        {
            _changed();
        }
    }
}
