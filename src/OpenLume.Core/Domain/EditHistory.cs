namespace OpenLume.Core.Domain;

public sealed record EditRevision(
    Guid Id,
    long Sequence,
    EditRecipe Recipe,
    DateTimeOffset CreatedAt);

public sealed record EditHistory(
    IReadOnlyList<EditRevision> Revisions,
    long CurrentSequence)
{
    public bool CanUndo => CurrentSequence > 0;

    public bool CanRedo => Revisions.Any(revision => revision.Sequence > CurrentSequence);

    public EditRevision? Current => Revisions.FirstOrDefault(revision => revision.Sequence == CurrentSequence);
}

public sealed record EditSnapshot(
    Guid Id,
    string Name,
    EditRecipe Recipe,
    DateTimeOffset CreatedAt);
