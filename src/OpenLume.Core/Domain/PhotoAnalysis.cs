namespace OpenLume.Core.Domain;

public sealed record PhotoAnalysis(
    string Summary,
    double TechnicalScore,
    double AestheticScore,
    bool SuggestedPick,
    IReadOnlyList<string> Tags,
    EditRecipe SuggestedEdit);

