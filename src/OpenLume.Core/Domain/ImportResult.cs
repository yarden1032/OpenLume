namespace OpenLume.Core.Domain;

public sealed record ImportResult(int Imported, int AlreadyPresent, int Failed, IReadOnlyList<string> Errors);

