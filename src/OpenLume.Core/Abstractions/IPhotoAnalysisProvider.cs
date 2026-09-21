using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public interface IPhotoAnalysisProvider
{
    string Name { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<PhotoAnalysis> AnalyzeAsync(PhotoAsset photo, byte[] previewJpeg, CancellationToken cancellationToken = default);
}
