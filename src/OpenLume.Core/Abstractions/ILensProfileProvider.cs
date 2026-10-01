using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public sealed record LensProfileMatch(
    string Id,
    string DisplayName,
    OpticsCorrections Corrections,
    double Confidence);

/// <summary>
/// Local-first extension point for matching embedded camera/lens metadata to correction profiles.
/// Providers return bounded recipe parameters; they never receive or generate rendered pixels.
/// </summary>
public interface ILensProfileProvider
{
    ValueTask<IReadOnlyList<LensProfileMatch>> FindMatchesAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
