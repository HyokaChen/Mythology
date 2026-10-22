using ClientPlatform;
using Core.Agent;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 平台写作素材库检索 — 经 /v1/knowledge/search（BM25 jieba + 向量余弦 RRF 混合排序）。
/// 每次检索按次计费扣 KnowledgeUnitCost 积分（失败请求不扣费）；
/// 命中含文档元信息与匹配片段摘录，深读用 knowledge_document 取全文。
/// </summary>
public sealed class KnowledgeSearchTool(KnowledgeRestClient? knowledge = null)
    : AgentTool<KnowledgeSearchParams, KnowledgeSearchDetails>
{
    public override string Name => "knowledge_search";
    public override string Label => "素材知识检索";
    public override string Description =>
        "检索平台写作素材库（角色设计/写作技巧/场景地点/古言常识等；BM25+向量混合排序，按次计费）。"
        + "命中返回文档标题/分类与匹配片段。";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<KnowledgeSearchDetails>> ExecuteAsync(
        string toolCallId,
        KnowledgeSearchParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<KnowledgeSearchDetails>>? onUpdate = null
    )
    {
        if (knowledge is null)
        {
            return Error("云端知识库客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.Query))
        {
            return Error("query 参数必填。");
        }
        var mode = string.IsNullOrWhiteSpace(args.Mode) ? "hybrid" : args.Mode!.Trim();
        if (mode is not ("hybrid" or "lexical"))
        {
            return Error("mode 只能是 hybrid 或 lexical。");
        }

        try
        {
            onUpdate?.Report(Progress("正在检索素材库…"));
            var json = await knowledge.SearchAsync(
                new
                {
                    query = args.Query,
                    limit = Math.Clamp(args.Limit ?? 8, 1, 20),
                    category = args.Category,
                    subcategory = args.Subcategory,
                    mode,
                },
                ct
            );
            return new AgentToolResult<KnowledgeSearchDetails>
            {
                Content = [new TextContent(json)],
                Details = new KnowledgeSearchDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"素材检索失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("素材检索"));
        }
        catch (Exception ex)
        {
            return Error($"素材检索失败：{ex.Message}");
        }
    }

    private static AgentToolResult<KnowledgeSearchDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new KnowledgeSearchDetails(),
        };

    private static AgentToolResult<KnowledgeSearchDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new KnowledgeSearchDetails() };
}

/// <summary>
/// 按当前场景获取素材改写建议 — 经 /v1/knowledge/hint（卡片级；按次计费）。
/// 返回 prompt-block + 摘录/用途/溯源，不返回整篇文档正文。
/// </summary>
public sealed class KnowledgeHintTool(
    KnowledgeRestClient? knowledge = null,
    INovelStore? store = null,
    Agent? agent = null
) : AgentTool<KnowledgeHintParams, KnowledgeHintDetails>
{
    public override string Name => "knowledge_hint";
    public override string Label => "素材智能提示";
    public override string Description =>
        "按当前场景检索平台素材卡片，返回可改写进 prompt 的建议（摘录+用途+溯源；按次计费）。"
        + "适合在写场景前主动取参考，而不是事后检索。"
        + "会自动带上当前章/场景的本体状态变化（首次出场/伤病/关系反转/规则触发等）作为检索条件；"
        + "写新场景、角色首次登场、伤病/关系反转、战斗/礼仪/医术等专业细节前，若缺具体素材就调用；"
        + "纯对话推进或已有细节可忽略。需要指定章节或场景时传 chapterId/sceneId。";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => false;

    public override async Task<AgentToolResult<KnowledgeHintDetails>> ExecuteAsync(
        string toolCallId,
        KnowledgeHintParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<KnowledgeHintDetails>>? onUpdate = null
    )
    {
        if (knowledge is null)
        {
            return Error("云端知识库客户端未初始化。");
        }
        if (string.IsNullOrWhiteSpace(args.SceneGoal))
        {
            return Error("sceneGoal 参数必填。");
        }

        try
        {
            onUpdate?.Report(Progress("正在获取素材提示…"));
            var ontology = await TryBuildOntologyAsync(args, ct);
            var json = await knowledge.HintAsync(
                new
                {
                    sceneGoal = args.SceneGoal,
                    currentText = args.CurrentText,
                    genre = args.Genre,
                    tags = args.Tags,
                    kinds = args.Kinds,
                    limit = Math.Clamp(args.Limit ?? 5, 1, 10),
                    maxChars = Math.Clamp(args.MaxChars ?? 1200, 200, 4000),
                    expand = args.Expand ?? false,
                    ontology,
                },
                ct
            );
            return new AgentToolResult<KnowledgeHintDetails>
            {
                Content = [new TextContent(json)],
                Details = new KnowledgeHintDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"素材提示失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("素材提示"));
        }
        catch (Exception ex)
        {
            return Error($"素材提示失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 自动拼装本体 delta：优先 args 指定章/场景，否则取最新章 + 其全部场景。
    /// 失败静默返回 null（提示仍走纯文本检索）。
    /// </summary>
    private async Task<object?> TryBuildOntologyAsync(
        KnowledgeHintParams args,
        CancellationToken ct
    )
    {
        if (store is null)
        {
            return null;
        }
        try
        {
            var novelId = args.NovelId ?? agent?.CurrentNovelId;
            if (string.IsNullOrWhiteSpace(novelId))
            {
                return null;
            }
            var novel = await store.GetNovel(novelId, ct);
            if (novel is null)
            {
                return null;
            }
            var chapters = await store.GetChapters(novelId, ct);
            var chapter = !string.IsNullOrWhiteSpace(args.ChapterId)
                ? chapters.FirstOrDefault(c => c.Id == args.ChapterId)
                : chapters.OrderBy(c => c.Order).LastOrDefault();
            if (chapter is null)
            {
                return null;
            }
            var scenes = await store.GetScenesByChapter(chapter.Id, ct);
            var worldRules = await store.GetWorldRules(novelId, ct);
            var view = await EntityTimelineView.BuildAsync(store, novelId, ct);
            var recentActions = await store.GetOntologyActions(novelId, ct);
            var boundaries = MaterialBoundaryDetector.DetectForChapter(
                view,
                novel,
                chapter,
                scenes,
                worldRules,
                recentActions: recentActions
            );
            var selected = !string.IsNullOrWhiteSpace(args.SceneId)
                ? boundaries.Where(b => b.Scene.Id == args.SceneId)
                : boundaries;
            var deltas = selected
                .SelectMany(b => b.Deltas)
                .GroupBy(d => d.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(20)
                .ToList();
            if (deltas.Count == 0)
            {
                return null;
            }
            return new
            {
                chapterOrder = chapter.Order,
                deltas = deltas.Select(d => new
                {
                    id = d.Id,
                    kind = d.Kind.ToString().ToLowerInvariant(),
                    entityType = d.EntityType.ToKebabCaseString(),
                    entityId = d.EntityId,
                    entityName = d.EntityName,
                    aspect = d.Aspect?.ToString().ToLowerInvariant(),
                    from = d.From,
                    to = d.To,
                    summary = d.Summary,
                    need = d.Need.ToString().ToLowerInvariant(),
                }),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static AgentToolResult<KnowledgeHintDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new KnowledgeHintDetails(),
        };

    private static AgentToolResult<KnowledgeHintDetails> Progress(string message) =>
        new() { Content = [new TextContent(message)], Details = new KnowledgeHintDetails() };
}

/// <summary>
/// 取整篇素材文档 — 经 /v1/knowledge/documents/{docId}（含 Markdown 正文，零价）。
/// documentId 来自 knowledge_search 的命中结果。
/// </summary>
public sealed class KnowledgeDocumentTool(KnowledgeRestClient? knowledge = null)
    : AgentTool<KnowledgeDocumentParams, KnowledgeDocumentDetails>
{
    public override string Name => "knowledge_document";
    public override string Label => "素材文档调阅";
    public override string Description =>
        "取素材文档（materials 来源默认截断为摘录；crawl 来源为全文；零价）；documentId 来自 knowledge_search 命中";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<KnowledgeDocumentDetails>> ExecuteAsync(
        string toolCallId,
        KnowledgeDocumentParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<KnowledgeDocumentDetails>>? onUpdate = null
    )
    {
        if (knowledge is null)
        {
            return Error("云端知识库客户端未初始化。");
        }
        if (!Guid.TryParse(args.DocumentId, out var documentId))
        {
            return Error("documentId 必须是 Guid 字符串（取自 knowledge_search 命中）。");
        }

        try
        {
            var json = await knowledge.GetDocumentAsync(documentId, ct);
            return new AgentToolResult<KnowledgeDocumentDetails>
            {
                Content = [new TextContent(json)],
                Details = new KnowledgeDocumentDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"素材文档调阅失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("素材文档调阅"));
        }
        catch (Exception ex)
        {
            return Error($"素材文档调阅失败：{ex.Message}");
        }
    }

    private static AgentToolResult<KnowledgeDocumentDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new KnowledgeDocumentDetails(),
        };
}

/// <summary>
/// 素材目录浏览 — 经 GET /v1/knowledge/documents 按分类/子分类分页浏览文档元信息（零价，游标翻页）。
/// </summary>
public sealed class KnowledgeBrowseTool(KnowledgeRestClient? knowledge = null)
    : AgentTool<KnowledgeBrowseParams, KnowledgeBrowseDetails>
{
    public override string Name => "knowledge_browse";
    public override string Label => "素材目录浏览";
    public override string Description =>
        "按分类/子分类分页浏览素材库文档元信息（零价；cursor 翻页，取自上一页的 nextCursor）。";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<KnowledgeBrowseDetails>> ExecuteAsync(
        string toolCallId,
        KnowledgeBrowseParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<KnowledgeBrowseDetails>>? onUpdate = null
    )
    {
        if (knowledge is null)
        {
            return Error("云端知识库客户端未初始化。");
        }

        try
        {
            var json = await knowledge.BrowseAsync(
                args.Category,
                args.Subcategory,
                Math.Clamp(args.Limit ?? 20, 1, 100),
                args.Cursor,
                ct
            );
            return new AgentToolResult<KnowledgeBrowseDetails>
            {
                Content = [new TextContent(json)],
                Details = new KnowledgeBrowseDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"素材目录浏览失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("素材目录浏览"));
        }
        catch (Exception ex)
        {
            return Error($"素材目录浏览失败：{ex.Message}");
        }
    }

    private static AgentToolResult<KnowledgeBrowseDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new KnowledgeBrowseDetails(),
        };
}

/// <summary>
/// 素材分类清单 — 经 GET /v1/knowledge/categories 列出全部分类与子分类及文档数（零价）。
/// </summary>
public sealed class KnowledgeCategoriesTool(KnowledgeRestClient? knowledge = null)
    : AgentTool<KnowledgeCategoriesParams, KnowledgeCategoriesDetails>
{
    public override string Name => "knowledge_categories";
    public override string Label => "素材分类清单";
    public override string Description =>
        "列出素材库全部分类与子分类及文档数（零价），用于了解素材库结构";
    public override ToolCategory Category => ToolCategory.SearchTool;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<KnowledgeCategoriesDetails>> ExecuteAsync(
        string toolCallId,
        KnowledgeCategoriesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<KnowledgeCategoriesDetails>>? onUpdate = null
    )
    {
        if (knowledge is null)
        {
            return Error("云端知识库客户端未初始化。");
        }

        try
        {
            var json = await knowledge.CategoriesAsync(ct);
            return new AgentToolResult<KnowledgeCategoriesDetails>
            {
                Content = [new TextContent(json)],
                Details = new KnowledgeCategoriesDetails { Json = json },
            };
        }
        catch (CloudApiException ex)
        {
            return Error($"素材分类清单获取失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error(CloudToolErrors.Timeout("素材分类清单获取"));
        }
        catch (Exception ex)
        {
            return Error($"素材分类清单获取失败：{ex.Message}");
        }
    }

    private static AgentToolResult<KnowledgeCategoriesDetails> Error(string message) =>
        new()
        {
            Content = [new TextContent(message)],
            IsError = true,
            Details = new KnowledgeCategoriesDetails(),
        };
}

public sealed record KnowledgeSearchParams
{
    public string? Query { get; init; }
    public int? Limit { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }

    /// <summary>hybrid（默认，BM25+向量 RRF）或 lexical（强制词法单分支）。</summary>
    public string? Mode { get; init; }
}

public sealed record KnowledgeSearchDetails
{
    public string Json { get; init; } = "";
}

public sealed record KnowledgeHintParams
{
    /// <summary>当前场景目标（必填，≤500 字）。</summary>
    public string? SceneGoal { get; init; }

    /// <summary>当前正文（可选，≤8000 字；用于语义召回）。</summary>
    public string? CurrentText { get; init; }
    public string? Genre { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Kinds { get; init; }
    public int? Limit { get; init; }
    public int? MaxChars { get; init; }
    public bool? Expand { get; init; }

    /// <summary>小说 ID；空则用 Agent 当前小说。</summary>
    public string? NovelId { get; init; }

    /// <summary>章节 ID；空则用最新章。</summary>
    public string? ChapterId { get; init; }

    /// <summary>场景 ID；空则取本章全部场景的 delta。</summary>
    public string? SceneId { get; init; }
}

public sealed record KnowledgeHintDetails
{
    public string Json { get; init; } = "";
}

public sealed record KnowledgeDocumentParams
{
    public string? DocumentId { get; init; }
}

public sealed record KnowledgeDocumentDetails
{
    public string Json { get; init; } = "";
}

public sealed record KnowledgeBrowseParams
{
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}

public sealed record KnowledgeBrowseDetails
{
    public string Json { get; init; } = "";
}

public sealed record KnowledgeCategoriesParams;

public sealed record KnowledgeCategoriesDetails
{
    public string Json { get; init; } = "";
}
