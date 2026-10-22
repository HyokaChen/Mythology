namespace Core.Tools;

public sealed record VariantResult
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Content { get; init; } = "";

    public string? Description { get; init; }

    public bool IsFailed { get; init; }

    public string? ReasoningContent { get; init; }
}
