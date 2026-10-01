using CommunityToolkit.Mvvm.ComponentModel;
using OpenLume.Core.Domain;

namespace OpenLume.App.ViewModels;

public sealed class LocalMaskViewModel(LocalMask recipe, Action changed) : ObservableObject
{
    private LocalMask _recipe = recipe.Normalize();
    public LocalMask Recipe => _recipe;
    public string Name { get => _recipe.Name; set => Update(_recipe with { Name = value }, nameof(Name)); }
    public string Kind => _recipe.Kind.ToString();
    public bool IsRadial => _recipe.Kind == LocalMaskKind.Radial;
    public bool IsLinear => _recipe.Kind == LocalMaskKind.Linear;
    public bool Enabled { get => _recipe.Enabled; set => Update(_recipe with { Enabled = value }, nameof(Enabled)); }
    public bool Inverted { get => _recipe.Inverted; set => Update(_recipe with { Inverted = value }, nameof(Inverted)); }
    public double CenterX { get => _recipe.CenterX; set => Update(_recipe with { CenterX = value }, nameof(CenterX)); }
    public double CenterY { get => _recipe.CenterY; set => Update(_recipe with { CenterY = value }, nameof(CenterY)); }
    public double RadiusX { get => _recipe.RadiusX; set => Update(_recipe with { RadiusX = value }, nameof(RadiusX)); }
    public double RadiusY { get => _recipe.RadiusY; set => Update(_recipe with { RadiusY = value }, nameof(RadiusY)); }
    public double Feather { get => _recipe.Feather; set => Update(_recipe with { Feather = value }, nameof(Feather)); }
    public double AngleDegrees { get => _recipe.AngleDegrees; set => Update(_recipe with { AngleDegrees = value }, nameof(AngleDegrees)); }
    public double Density { get => _recipe.Density; set => Update(_recipe with { Density = value }, nameof(Density)); }
    public double ExposureEv { get => _recipe.ExposureEv; set => Update(_recipe with { ExposureEv = value }, nameof(ExposureEv)); }
    public double Contrast { get => _recipe.Contrast; set => Update(_recipe with { Contrast = value }, nameof(Contrast)); }
    public double Saturation { get => _recipe.Saturation; set => Update(_recipe with { Saturation = value }, nameof(Saturation)); }
    public double Temperature { get => _recipe.Temperature; set => Update(_recipe with { Temperature = value }, nameof(Temperature)); }
    public double Tint { get => _recipe.Tint; set => Update(_recipe with { Tint = value }, nameof(Tint)); }

    private void Update(LocalMask value, string property)
    {
        value = value.Normalize();
        if (_recipe == value) return;
        _recipe = value;
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(Recipe));
        changed();
    }
}
