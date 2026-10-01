using OpenLume.Core.Abstractions;

namespace OpenLume.Core.Domain;

/// <summary>sRGB, 8-bit output with source metadata stripped. Zero size means original dimensions.</summary>
public sealed record ExportOptions(ImageExportFormat Format = ImageExportFormat.Jpeg, int Quality = 92, int MaxDimension = 0)
{
    public string Extension => Format == ImageExportFormat.Png ? "png" : "jpg";

    public ExportOptions Normalize()
    {
        if (!Enum.IsDefined(Format)) throw new ArgumentOutOfRangeException(nameof(Format));
        if (MaxDimension < 0 || MaxDimension > 32768) throw new ArgumentOutOfRangeException(nameof(MaxDimension));
        return this with { Quality = Math.Clamp(Quality, 0, 100) };
    }
}
