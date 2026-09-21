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
}

