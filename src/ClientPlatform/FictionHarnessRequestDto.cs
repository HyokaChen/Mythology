using Core.Cloud;

namespace ClientPlatform;

/// <summary>
/// Cloud Harness 调用请求的客户端出站 DTO — 云端 CreateHarnessInvocationRequest 的镜像。
/// 序列化经 <see cref="ClientJsonDefaults.Cloud"/>（kebab-case），
/// 因此 DTO 属性名保持 PascalCase 即可得到正确的 wire 键（contractVersion → contract-version）。
/// </summary>
public sealed class FictionHarnessRequestDto
{
    public string ContractVersion { get; init; } = "";
    public string Capability { get; init; } = "";
    public string Depth { get; init; } = "";
    public string Locale { get; init; } = "";
    public string? SceneGoal { get; init; }
    public string? NovelId { get; init; }

    /// <summary>
    /// 联想来源单元（仅 <c>fiction.associative.link</c> 使用）— 云端
    /// <c>CreateHarnessInvocationRequest.Sources</c> 的镜像；缺省时云端对非 quick 档
    /// 返回 warning（Sources 为空），索引不生效。
    /// </summary>
    public IReadOnlyList<AssociativeSourceInput>? Sources { get; init; }

    /// <summary>用户写作配置（题材 + 增强档位）— 云端 CreateHarnessInvocationRequest.Profile 的镜像。</summary>
    public WritingProfileInput? Profile { get; init; }

    public IEnumerable<ChapterDto>? Chapters { get; init; }
    public ContextDto? Context { get; init; }
    public SemanticExtractionDto? SemanticExtraction { get; init; }
    public AnalysisOptionsInput? Options { get; init; }

    public static FictionHarnessRequestDto From(HarnessRequest request) =>
        new()
        {
            ContractVersion = request.ContractVersion,
            Capability = request.Capability,
            Depth = request.Depth,
            Locale = request.Locale,
            SceneGoal = request.SceneGoal,
            NovelId = request.NovelId,
            Sources = request.Sources,
            Profile = request.Profile,
            Chapters = request.Chapters.Select(c => new ChapterDto
            {
                ClientChapterRef = c.ClientChapterRef,
                Order = c.Order,
                Title = c.Title,
                Role = c.Role,
                Content = c.Content,
            }),
            Context = request.Context is null
                ? null
                : new ContextDto
                {
                    PreviousChapterMode = request.Context.PreviousChapterMode,
                    PreviousChapters = request.Context.PreviousChapters,
                    CharacterHints = request.Context.CharacterHints,
                    PlotHints = request.Context.PlotHints,
                    CharacterDetails = request.Context.CharacterDetails,
                    Plotlines = request.Context.Plotlines,
                    Items = request.Context.Items,
                    NovelTitle = request.Context.NovelTitle,
                    CharacterVersion = request.Context.CharacterVersion,
                    PlotlineVersion = request.Context.PlotlineVersion,
                    Setup = request.Context.Setup,
                    Timeline = request.Context.Timeline,
                },
            SemanticExtraction = request.SemanticExtraction is null
                ? null
                : new SemanticExtractionDto
                {
                    Interactions = request.SemanticExtraction.Interactions,
                },
            Options = request.Options,
        };
}

public sealed class ChapterDto
{
    public string ClientChapterRef { get; init; } = "";
    public int Order { get; init; }
    public string Title { get; init; } = "";
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
}

public sealed class ContextDto
{
    public string PreviousChapterMode { get; init; } = "none";
    public IReadOnlyList<ChapterInput>? PreviousChapters { get; init; }
    public IReadOnlyList<string>? CharacterHints { get; init; }
    public IReadOnlyList<string>? PlotHints { get; init; }
    public IReadOnlyList<CloudCharacterInput>? CharacterDetails { get; init; }
    public IReadOnlyList<CloudPlotlineInput>? Plotlines { get; init; }
    public IReadOnlyList<CloudItemInput>? Items { get; init; }
    public string? NovelTitle { get; init; }

    /// <summary>
    /// 角色结构化数据的 SHA256 指纹（服务端 novel_meta 版本比对 / 跨请求去重传输）。
    /// 曾漏出现在站 DTO 上——客户端算得出、线上却丢，服务端去重因此永不触发（2026-10-18 修复）。
    /// </summary>
    public string? CharacterVersion { get; init; }

    /// <summary>情节线结构化数据的 SHA256 指纹（同上）。</summary>
    public string? PlotlineVersion { get; init; }

    public SetupCheckInput? Setup { get; init; }

    /// <summary>
    /// 时间线事件（kebab 键 <c>timeline</c>）— 云端 PlotHoleDetector.DetectTimelineConflicts 的数据源；
    /// 客户端自 2026-10-18 起上传（此前客户端契约无此字段 → 该检查长期空转）。
    /// </summary>
    public IReadOnlyList<CloudTimelineEventInput>? Timeline { get; init; }
}

public sealed class SemanticExtractionDto
{
    public IReadOnlyList<CharacterInteractionInput>? Interactions { get; init; }
}
