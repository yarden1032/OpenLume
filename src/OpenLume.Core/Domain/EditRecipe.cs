namespace OpenLume.Core.Domain;

public sealed record EditRecipe(
    int Version = 9,
    double ExposureEv = 0,
    double Contrast = 0,
    double Saturation = 0,
    double Temperature = 0,
    double Tint = 0,
    double RotationDegrees = 0,
    double Highlights = 0,
    double Shadows = 0,
    double Whites = 0,
    double Blacks = 0,
    double Vibrance = 0,
    double Vignette = 0,
    double Texture = 0,
    double Clarity = 0,
    double Dehaze = 0,
    double Sharpening = 0,
    double NoiseReduction = 0,
    double Grain = 0,
    HslColorMixer? ColorMixer = null,
    ParametricToneCurve? ToneCurve = null,
    CropGeometry? Crop = null,
    OpticsCorrections? Optics = null,
    double ColorNoiseReduction = 0,
    double SharpeningRadius = .8,
    double SharpeningMasking = 0)
{
    public const int CurrentVersion = 9;

    public static EditRecipe Default { get; } = new(
        ColorMixer: HslColorMixer.Neutral,
        ToneCurve: ParametricToneCurve.Identity,
        Crop: CropGeometry.FullFrame,
        Optics: OpticsCorrections.Neutral);

    public EditRecipe Normalize() => this with
    {
        Version = CurrentVersion,
        ExposureEv = Math.Clamp(ExposureEv, -5, 5),
        Contrast = Math.Clamp(Contrast, -100, 100),
        Saturation = Math.Clamp(Saturation, -100, 100),
        Temperature = Math.Clamp(Temperature, -100, 100),
        Tint = Math.Clamp(Tint, -100, 100),
        RotationDegrees = Math.Clamp(RotationDegrees, -45, 45),
        Highlights = Math.Clamp(Highlights, -100, 100),
        Shadows = Math.Clamp(Shadows, -100, 100),
        Whites = Math.Clamp(Whites, -100, 100),
        Blacks = Math.Clamp(Blacks, -100, 100),
        Vibrance = Math.Clamp(Vibrance, -100, 100),
        Vignette = Math.Clamp(Vignette, -100, 100),
        Texture = Math.Clamp(Texture, -100, 100),
        Clarity = Math.Clamp(Clarity, -100, 100),
        Dehaze = Math.Clamp(Dehaze, -100, 100),
        Sharpening = Math.Clamp(Sharpening, 0, 100),
        SharpeningRadius = double.IsFinite(SharpeningRadius) ? Math.Clamp(SharpeningRadius, .5, 3) : .8,
        SharpeningMasking = double.IsFinite(SharpeningMasking) ? Math.Clamp(SharpeningMasking, 0, 100) : 0,
        NoiseReduction = Math.Clamp(NoiseReduction, 0, 100),
        ColorNoiseReduction = double.IsFinite(ColorNoiseReduction) ? Math.Clamp(ColorNoiseReduction, 0, 100) : 0,
        Grain = Math.Clamp(Grain, 0, 100),
        ColorMixer = (ColorMixer ?? HslColorMixer.Neutral).Normalize(),
        ToneCurve = (ToneCurve ?? ParametricToneCurve.Identity).Normalize(),
        Crop = (Crop ?? CropGeometry.FullFrame).Normalize(),
        Optics = (Optics ?? OpticsCorrections.Neutral).Normalize()
    };
}

