namespace OpenLume.Core.Domain;

public sealed record EditRecipe(
    int Version = 1,
    double ExposureEv = 0,
    double Contrast = 0,
    double Saturation = 0,
    double Temperature = 0,
    double Tint = 0,
    double RotationDegrees = 0)
{
    public static EditRecipe Default { get; } = new();

    public EditRecipe Normalize() => this with
    {
        ExposureEv = Math.Clamp(ExposureEv, -5, 5),
        Contrast = Math.Clamp(Contrast, -100, 100),
        Saturation = Math.Clamp(Saturation, -100, 100),
        Temperature = Math.Clamp(Temperature, -100, 100),
        Tint = Math.Clamp(Tint, -100, 100),
        RotationDegrees = Math.Clamp(RotationDegrees, -45, 45)
    };
}

