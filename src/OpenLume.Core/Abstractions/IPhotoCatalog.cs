using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public interface IPhotoCatalog : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ImportResult> ImportFolderAsync(string folder, bool includeSubfolders, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(CancellationToken cancellationToken = default);
    Task<PhotoAsset?> GetPhotoAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateEditAsync(Guid id, EditRecipe edit, CancellationToken cancellationToken = default);
    Task UpdateRatingAsync(Guid id, int rating, CancellationToken cancellationToken = default);
    Task UpdatePickStateAsync(Guid id, PickState state, CancellationToken cancellationToken = default);
    Task UpdateAnalysisAsync(Guid id, PhotoAnalysis analysis, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Guid id, PhotoMetadata metadata, CancellationToken cancellationToken = default);
    Task MarkMetadataFailedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoAsset>> GetPendingMetadataAsync(int limit, CancellationToken cancellationToken = default);
    Task<int> GetPhotoCountAsync(CatalogFilter? filter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoAsset>> QueryFolderAsync(FolderQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoAsset>> QueryAsync(CatalogFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoSet>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);
    Task RenameCollectionAsync(Guid collectionId, string name, CancellationToken cancellationToken = default);
    Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task SetCollectionMembershipAsync(Guid collectionId, Guid photoId, bool included, CancellationToken cancellationToken = default);
    Task<Guid> CreateStackAsync(string name, CancellationToken cancellationToken = default);
    Task RenameStackAsync(Guid stackId, string name, CancellationToken cancellationToken = default);
    Task DeleteStackAsync(Guid stackId, CancellationToken cancellationToken = default);
    Task SetStackMembershipAsync(Guid stackId, IReadOnlyList<Guid> photoIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoStackGroup>> GetStacksAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> FindMissingAsync(CancellationToken cancellationToken = default);
    Task<bool> RelinkAsync(Guid id, string newPath, CancellationToken cancellationToken = default);
}

