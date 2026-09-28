namespace OpenLume.Core.Domain;

public sealed record PhotoAnalysis(
    string Summary,
    double TechnicalScore,
    double AestheticScore,
    bool SuggestedPick,
    IReadOnlyList<string> Tags,
    EditRecipe SuggestedEdit,
    DevelopSuggestion? DevelopSuggestion = null)
{
    public DevelopSuggestion EffectiveSuggestion => DevelopSuggestion ?? new DevelopSuggestion(
        Guid.NewGuid(),
        "Balanced automatic development",
        0.5,
        SuggestedEdit.Normalize(),
        [],
        [],
        DevelopSuggestionStatus.Pending,
        DateTimeOffset.UtcNow,
        [
            nameof(EditRecipe.ExposureEv), nameof(EditRecipe.Contrast), nameof(EditRecipe.Saturation),
            nameof(EditRecipe.Temperature), nameof(EditRecipe.Tint)
        ]);
}

public enum DevelopSuggestionStatus
{
    Pending = 0,
    Applied = 1,
    Rejected = 2
}

public sealed record EditParameterDecision(string Parameter, string Reason);

public sealed record DevelopSuggestion(
    Guid Id,
    string Intent,
    double Confidence,
    EditRecipe Recipe,
    IReadOnlyList<EditParameterDecision> Decisions,
    IReadOnlyList<string> Warnings,
    DevelopSuggestionStatus Status,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string>? ControlledParameters = null)
{
    private static readonly HashSet<string> SupportedParameters =
    [
        nameof(EditRecipe.ExposureEv), nameof(EditRecipe.Contrast), nameof(EditRecipe.Saturation),
        nameof(EditRecipe.Temperature), nameof(EditRecipe.Tint), nameof(EditRecipe.RotationDegrees),
        nameof(EditRecipe.Highlights), nameof(EditRecipe.Shadows), nameof(EditRecipe.Whites),
        nameof(EditRecipe.Blacks), nameof(EditRecipe.Vibrance), nameof(EditRecipe.Vignette),
        nameof(EditRecipe.Texture), nameof(EditRecipe.Clarity), nameof(EditRecipe.Dehaze),
        nameof(EditRecipe.Sharpening), nameof(EditRecipe.NoiseReduction), nameof(EditRecipe.Grain)
    ];

    public DevelopSuggestion Normalize() => this with
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
        Intent = string.IsNullOrWhiteSpace(Intent) ? "Balanced automatic development" : Intent.Trim()[..Math.Min(Intent.Trim().Length, 240)],
        Confidence = Math.Clamp(Confidence, 0, 1),
        Recipe = (Recipe ?? EditRecipe.Default).Normalize(),
        Decisions = (Decisions ?? [])
            .Where(decision => !string.IsNullOrWhiteSpace(decision.Parameter) && !string.IsNullOrWhiteSpace(decision.Reason))
            .Select(decision => new EditParameterDecision(
                decision.Parameter.Trim()[..Math.Min(decision.Parameter.Trim().Length, 40)],
                decision.Reason.Trim()[..Math.Min(decision.Reason.Trim().Length, 240)]))
            .Take(16)
            .ToArray(),
        Warnings = (Warnings ?? [])
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim()[..Math.Min(warning.Trim().Length, 240)])
            .Take(8)
            .ToArray(),
        ControlledParameters = ControlledParameters is null
            ? null
            : ControlledParameters
                .Where(SupportedParameters.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        Status = Enum.IsDefined(Status) ? Status : DevelopSuggestionStatus.Pending,
        GeneratedAt = GeneratedAt == default ? DateTimeOffset.UtcNow : GeneratedAt
    };

    public EditRecipe MergeOnto(EditRecipe current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var proposed = Recipe.Normalize();
        var controlled = ControlledParameters is null
            ? SupportedParameters
            : ControlledParameters.ToHashSet(StringComparer.Ordinal);
        return (current with
        {
            ExposureEv = controlled.Contains(nameof(EditRecipe.ExposureEv)) ? proposed.ExposureEv : current.ExposureEv,
            Contrast = controlled.Contains(nameof(EditRecipe.Contrast)) ? proposed.Contrast : current.Contrast,
            Saturation = controlled.Contains(nameof(EditRecipe.Saturation)) ? proposed.Saturation : current.Saturation,
            Temperature = controlled.Contains(nameof(EditRecipe.Temperature)) ? proposed.Temperature : current.Temperature,
            Tint = controlled.Contains(nameof(EditRecipe.Tint)) ? proposed.Tint : current.Tint,
            RotationDegrees = controlled.Contains(nameof(EditRecipe.RotationDegrees)) ? proposed.RotationDegrees : current.RotationDegrees,
            Highlights = controlled.Contains(nameof(EditRecipe.Highlights)) ? proposed.Highlights : current.Highlights,
            Shadows = controlled.Contains(nameof(EditRecipe.Shadows)) ? proposed.Shadows : current.Shadows,
            Whites = controlled.Contains(nameof(EditRecipe.Whites)) ? proposed.Whites : current.Whites,
            Blacks = controlled.Contains(nameof(EditRecipe.Blacks)) ? proposed.Blacks : current.Blacks,
            Vibrance = controlled.Contains(nameof(EditRecipe.Vibrance)) ? proposed.Vibrance : current.Vibrance,
            Vignette = controlled.Contains(nameof(EditRecipe.Vignette)) ? proposed.Vignette : current.Vignette,
            Texture = controlled.Contains(nameof(EditRecipe.Texture)) ? proposed.Texture : current.Texture,
            Clarity = controlled.Contains(nameof(EditRecipe.Clarity)) ? proposed.Clarity : current.Clarity,
            Dehaze = controlled.Contains(nameof(EditRecipe.Dehaze)) ? proposed.Dehaze : current.Dehaze,
            Sharpening = controlled.Contains(nameof(EditRecipe.Sharpening)) ? proposed.Sharpening : current.Sharpening,
            NoiseReduction = controlled.Contains(nameof(EditRecipe.NoiseReduction)) ? proposed.NoiseReduction : current.NoiseReduction,
            Grain = controlled.Contains(nameof(EditRecipe.Grain)) ? proposed.Grain : current.Grain
        }).Normalize();
    }
}

