using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class CatalogIssue10Tests
{
    [Fact]
    public async Task FolderFiltersCollectionsStacksAndRelinkPreserveState()
    {
        var root = Path.Combine(Path.GetTempPath(), "lume-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try { var file = Path.Combine(root, "one.jpg"); File.WriteAllText(file, "x"); await using var c = new SqlitePhotoCatalog(Path.Combine(root, "db.sqlite")); await c.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken); var p = (await c.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(); await c.UpdateRatingAsync(p.Id, 4, cancellationToken: TestContext.Current.CancellationToken); var set = await c.CreateCollectionAsync("keepers", cancellationToken: TestContext.Current.CancellationToken); await c.SetCollectionMembershipAsync(set, p.Id, true, cancellationToken: TestContext.Current.CancellationToken); var stack = await c.CreateStackAsync("burst", cancellationToken: TestContext.Current.CancellationToken); await c.SetStackMembershipAsync(stack, new[] { p.Id }, cancellationToken: TestContext.Current.CancellationToken); Assert.Single(await c.QueryAsync(new CatalogFilter(MinimumRating: 4, CollectionId: set), cancellationToken: TestContext.Current.CancellationToken)); Assert.Single((await c.GetStacksAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().PhotoIds); var moved = Path.Combine(root, "moved.jpg"); File.Move(file, moved); Assert.True(await c.RelinkAsync(p.Id, moved, cancellationToken: TestContext.Current.CancellationToken)); Assert.Equal(4, (await c.GetPhotoAsync(p.Id, cancellationToken: TestContext.Current.CancellationToken))!.Rating); Assert.Empty(await c.FindMissingAsync(cancellationToken: TestContext.Current.CancellationToken)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MetadataPendingAndMissingDetectionWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "lume-" + Guid.NewGuid()); Directory.CreateDirectory(root); try { var f = Path.Combine(root, "a.png"); File.WriteAllText(f, "x"); await using var c = new SqlitePhotoCatalog(Path.Combine(root, "db")); await c.ImportFolderAsync(root, false, cancellationToken: TestContext.Current.CancellationToken); var p = (await c.GetPhotosAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(); Assert.Single(await c.GetPendingMetadataAsync(10, cancellationToken: TestContext.Current.CancellationToken)); await c.UpdateMetadataAsync(p.Id, new PhotoMetadata(10, 20, DateTimeOffset.UtcNow, new FileInfo(f).LastWriteTimeUtc.Ticks), cancellationToken: TestContext.Current.CancellationToken); Assert.Empty(await c.GetPendingMetadataAsync(10, cancellationToken: TestContext.Current.CancellationToken)); File.Delete(f); Assert.Contains(p.Id, await c.FindMissingAsync(cancellationToken: TestContext.Current.CancellationToken)); } finally { Directory.Delete(root, true); }
    }
}
