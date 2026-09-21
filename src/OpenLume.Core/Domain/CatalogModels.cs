namespace OpenLume.Core.Domain;

public sealed record CatalogFolder(string Path, int PhotoCount, int MissingCount);

public sealed record FolderQuery(
    string Folder,
    bool IncludeSubfolders = true,
    int Offset = 0,
    int Limit = 500);

public sealed record PhotoSet(Guid Id, string Name, int PhotoCount = 0);

public sealed record PhotoStackGroup(Guid Id, string Name, IReadOnlyList<Guid> PhotoIds);

public sealed record CatalogFilter(
    int? MinimumRating = null,
    PickState? PickState = null,
    string? Extension = null,
    bool? Missing = null,
    string? SearchText = null,
    Guid? CollectionId = null,
    Guid? StackId = null,
    int Offset = 0,
    int Limit = 500,
    string? Folder = null,
    bool IncludeSubfolders = true);

public enum MetadataIndexState
{
    Pending = 0,
    Complete = 1,
    Failed = 2
}

public sealed record PhotoMetadata(
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset? CapturedAt,
    long SourceLastWriteUtcTicks);
