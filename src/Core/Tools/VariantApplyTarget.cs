namespace Core.Tools;

public sealed record VariantApplyTarget
{
    public string NovelId { get; init; } = "";

    public string? ChapterId { get; init; }

    public string Title { get; init; } = "";

    public int Order { get; init; }

    public string? BaseContentHash { get; init; }
}
