namespace Core.Tools;

public sealed record ExploreDetails
{
    public string VariantSetId { get; init; } = "";

    public string ExploreType { get; init; } = "";

    public string Prompt { get; init; } = "";

    public List<VariantResult> Variants { get; init; } = [];

    public VariantApplyTarget? ApplyTarget { get; init; }
}
