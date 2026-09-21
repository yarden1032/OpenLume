namespace OpenLume.Core.Domain;

public sealed record PresetImportResult(string? Name, EditRecipe Recipe, IReadOnlyList<string> UnsupportedParameters, IReadOnlyList<string> Warnings, bool Success);
