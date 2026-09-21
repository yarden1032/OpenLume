namespace OpenLume.Core.Domain;

public static class SupportedPhotoFormats
{
    public static readonly IReadOnlySet<string> RasterExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp" };

    public static readonly IReadOnlySet<string> RawExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".nef", ".nrw", ".cr2", ".cr3", ".arw", ".sr2", ".dng" };

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return RasterExtensions.Contains(extension) || RawExtensions.Contains(extension);
    }
}

