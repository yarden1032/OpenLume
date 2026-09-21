namespace OpenLume.Core.Domain;

public enum PickState
{
    Unflagged = 0,
    Pick = 1,
    Reject = 2
}

public sealed record PhotoAsset(
    Guid Id,
    string OriginalPath,
    string FileName,
    string Extension,
    long FileSizeBytes,
    DateTimeOffset ImportedAt,
    DateTimeOffset? CapturedAt,
    int PixelWidth,
    int PixelHeight,
    int Rating,
    PickState PickState,
    EditRecipe Edit,
    string? AiSummary = null)
{
    public bool IsRaw => SupportedPhotoFormats.RawExtensions.Contains(Extension);
}

