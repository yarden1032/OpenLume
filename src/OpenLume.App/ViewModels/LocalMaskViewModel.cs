using CommunityToolkit.Mvvm.ComponentModel;
using OpenLume.Core.Domain;

namespace OpenLume.App.ViewModels;

public sealed class LocalMaskViewModel(LocalMask recipe, Action changed) : ObservableObject
{
    private LocalMask _recipe = recipe.Normalize();
    public LocalMask Recipe => _recipe;
    public LocalMask CommittedRecipe => _beforeStroke ?? _recipe;
    public string Name { get => _recipe.Name; set => Update(_recipe with { Name = value }, nameof(Name)); }
    public string Kind => _recipe.Kind.ToString();
    public bool IsRadial => _recipe.Kind == LocalMaskKind.Radial;
    public bool IsLinear => _recipe.Kind == LocalMaskKind.Linear;
    public bool IsBrush => _recipe.Kind == LocalMaskKind.Brush;
    public bool IsGradient => !IsBrush;
    private LocalMask? _beforeStroke;
    private BrushStroke? _stroke;
    private readonly List<MaskPoint> _points = [];
    private double _brushSize = .03, _brushFeather = .6, _brushFlow = .5;
    private bool _brushErase;
    public double BrushSize { get => _brushSize; set => SetProperty(ref _brushSize, Math.Clamp(value, .001, .5)); }
    public double BrushFeather { get => _brushFeather; set => SetProperty(ref _brushFeather, Math.Clamp(value, 0, 1)); }
    public double BrushFlow { get => _brushFlow; set => SetProperty(ref _brushFlow, Math.Clamp(value, 0, 1)); }
    public bool BrushErase { get => _brushErase; set => SetProperty(ref _brushErase, value); }
    public bool IsPainting => _beforeStroke is not null;

    public bool BeginStroke(MaskPoint point, double aspectRatio)
    {
        if (!IsBrush || IsPainting || _recipe.BrushStrokes!.Count >= 128 ||
            _recipe.BrushStrokes.Sum(stroke => stroke.Points.Count) >= 16384) return false;
        _beforeStroke = _recipe;
        _points.Clear();
        _stroke = new(new([]), BrushSize, BrushSize * aspectRatio, BrushFeather, BrushFlow, BrushErase);
        OnPropertyChanged(nameof(IsPainting));
        AppendStroke(point);
        return true;
    }

    public void AppendStroke(MaskPoint point)
    {
        if (_beforeStroke is null || _stroke is null) return;
        point = point.Normalize();
        if (_points.Count > 0 && _points[^1] == point) return;
        var limit = Math.Min(2048, 16384 - _beforeStroke.BrushStrokes!.Sum(stroke => stroke.Points.Count));
        if (_points.Count >= limit) _points[^1] = point;
        else _points.Add(point);
        _recipe = (_recipe with { BrushStrokes = new(_beforeStroke.BrushStrokes!.Concat([_stroke with { Points = new(_points) }])) }).Normalize();
        OnPropertyChanged(nameof(Recipe));
    }

    public void FinishStroke()
    {
        if (_beforeStroke is null) return;
        var different = _recipe != _beforeStroke;
        _beforeStroke = null;
        _stroke = null;
        _points.Clear();
        OnPropertyChanged(nameof(IsPainting));
        if (different) changed();
    }

    public void CancelStroke()
    {
        if (_beforeStroke is null) return;
        _recipe = _beforeStroke;
        _beforeStroke = null;
        _stroke = null;
        _points.Clear();
        OnPropertyChanged(string.Empty);
    }
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
        if (!IsPainting) changed();
    }
}
