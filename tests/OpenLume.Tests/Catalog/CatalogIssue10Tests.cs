using OpenLume.Core.Domain;
using OpenLume.Infrastructure.Catalog;

namespace OpenLume.Tests.Catalog;

public sealed class CatalogIssue10Tests
{
    [Fact]
    public async Task FolderFiltersCollectionsStacksAndRelinkPreserveState()
    {
        var root = Path.Combine(Path.GetTempPath(), "lume-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try { var file = Path.Combine(root, "one.jpg"); File.WriteAllText(file, "x"); await using var c = new SqlitePhotoCatalog(Path.Combine(root, "db.sqlite")); await c.ImportFolderAsync(root, false); var p = (await c.GetPhotosAsync()).Single(); await c.UpdateRatingAsync(p.Id, 4); var set = await c.CreateCollectionAsync("keepers"); await c.SetCollectionMembershipAsync(set, p.Id, true); var stack = await c.CreateStackAsync("burst"); await c.SetStackMembershipAsync(stack, new[] { p.Id }); Assert.Single(await c.QueryAsync(new CatalogFilter(MinimumRating: 4, CollectionId: set))); Assert.Single((await c.GetStacksAsync()).Single().PhotoIds); var moved = Path.Combine(root, "moved.jpg"); File.Move(file, moved); Assert.True(await c.RelinkAsync(p.Id, moved)); Assert.Equal(4, (await c.GetPhotoAsync(p.Id))!.Rating); Assert.Empty(await c.FindMissingAsync()); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MetadataPendingAndMissingDetectionWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "lume-" + Guid.NewGuid()); Directory.CreateDirectory(root); try { var f = Path.Combine(root, "a.png"); File.WriteAllText(f, "x"); await using var c = new SqlitePhotoCatalog(Path.Combine(root, "db")); await c.ImportFolderAsync(root, false); var p = (await c.GetPhotosAsync()).Single(); Assert.Single(await c.GetPendingMetadataAsync(10)); await c.UpdateMetadataAsync(p.Id, new PhotoMetadata(10, 20, DateTimeOffset.UtcNow, new FileInfo(f).LastWriteTimeUtc.Ticks)); Assert.Empty(await c.GetPendingMetadataAsync(10)); File.Delete(f); Assert.Contains(p.Id, await c.FindMissingAsync()); } finally { Directory.Delete(root, true); }
    }
}
