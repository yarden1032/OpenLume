namespace OpenLume.Core.Domain;

public sealed record EditRecipe(
    int Version = 2,
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
    double Vignette = 0)
{
    public const int CurrentVersion = 2;

    public static EditRecipe Default { get; } = new();

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
        Vignette = Math.Clamp(Vignette, -100, 100)
    };
}

