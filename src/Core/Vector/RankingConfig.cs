namespace Core.Vector;

/// <summary>
/// 排序配置：不同 RankingProfile 的 dense/lexical/filter/recency 权重。
/// 集中定义并记录版本，禁止每个工具私自实现融合公式。
/// </summary>
public sealed record RankingConfig
{
    /// <summary>RRF 常数 k（防止排名靠前的项过度主导）。</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Dense 通道权重。</summary>
    public double DenseWeight { get; init; } = 1.0;

    /// <summary>Lexical 通道权重。</summary>
    public double LexicalWeight { get; init; } = 1.0;

    /// <summary>章节距离衰减因子（0 = 不衰减，越大衰减越快）。</summary>
    public double ChapterDistanceDecay { get; init; }

    /// <summary>当前章节序号（用于章节距离计算，运行时设置）。</summary>
    public int? CurrentChapterOrder { get; init; }

    /// <summary>多样性约束：同一章节最大结果数。</summary>
    public int? MaxPerChapter { get; init; }

    /// <summary>多样性约束：同一情节线最大结果数。</summary>
    public int? MaxPerPlotline { get; init; }

    /// <summary>相邻块合并：同一章节相邻块是否合并展示。</summary>
    public bool MergeAdjacentChunks { get; init; } = true;

    /// <summary>获取指定 RankingProfile 的默认配置。</summary>
    public static RankingConfig ForProfile(RankingProfile profile) =>
        profile switch
        {
            RankingProfile.WriteContext => new()
            {
                DenseWeight = 1.0,
                LexicalWeight = 0.8,
                ChapterDistanceDecay = 0.1,
                MaxPerChapter = 3,
                MergeAdjacentChunks = true,
            },
            RankingProfile.ConsistencyCheck => new()
            {
                DenseWeight = 0.7,
                LexicalWeight = 1.2,
                ChapterDistanceDecay = 0,
                MaxPerChapter = 5,
                MergeAdjacentChunks = false,
            },
            RankingProfile.ForeshadowingSearch => new()
            {
                DenseWeight = 1.3,
                LexicalWeight = 0.6,
                ChapterDistanceDecay = 0,
                MaxPerChapter = 2,
                MaxPerPlotline = 3,
                MergeAdjacentChunks = true,
            },
            RankingProfile.PreferenceRecall => new()
            {
                DenseWeight = 0.8,
                LexicalWeight = 1.0,
                ChapterDistanceDecay = 0,
                MaxPerChapter = null,
                MergeAdjacentChunks = false,
            },
            _ => new(),
        };
}
