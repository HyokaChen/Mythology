using System.ComponentModel;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Microsoft.Extensions.DependencyInjection;

// 权限声明 — 编译进 DLL，受强名称签名保护
[assembly: PluginPermission(PluginPermissions.NovelRead)]
[assembly: PluginPermission(PluginPermissions.VectorRead)]
[assembly: PluginPermission(PluginPermissions.CloudHarness)]

namespace Plugins.SemanticRetrievalTool;

/// <summary>
/// 语义检索工具插件：提供基于向量语义搜索的工具集。
/// 包括搜索记忆、检索角色、检索情节、一致性检查等功能。
/// </summary>
public sealed class SemanticRetrievalToolPlugin : IToolPlugin
{
    private RetrievalKernel _kernel = null!;
    private PluginStore _store = null!;
    private Agent? _agent;
    private INovelStore? _novelStore;
    private IFictionHarnessClient? _cloudHarness;

    public void Initialize(IServiceProvider sp)
    {
        _kernel = sp.GetRequiredService<RetrievalKernel>();
        _store = sp.GetRequiredService<PluginStore>();
        _agent = sp.GetService<Agent>();
        _novelStore = sp.GetService<INovelStore>();
        _cloudHarness = sp.GetService<IFictionHarnessClient>();
    }

    public IEnumerable<AgentTool> GetTools()
    {
        yield return new SearchMemoryTool(_kernel);
        yield return new BatchSearchMemoryTool(_kernel);
        yield return new RecallCharacterTool(_kernel, _store);
        yield return new RecallPlotTool(_kernel);
        yield return new FindConsistencyTool(_store);
        yield return new RecallSceneTool(_kernel, _store);
        yield return new RecallTimelineTool(_kernel, _store);
        yield return new RecallRelatedTool(_kernel, _store);

        // 联想记忆（云端能力）：索引与召回都在云端，本地只做触发与渲染
        if (_agent is not null && _novelStore is not null)
        {
            yield return new IndexAssociativeMemoryTool(_agent, _novelStore, _cloudHarness);
            yield return new RecallAssociativeTool(_agent, _novelStore, _cloudHarness);
        }
    }
}

// ── 搜索记忆工具 ──

/// <summary>
/// 搜索记忆工具：在整个项目向量库中进行语义搜索。
/// </summary>
public sealed class SearchMemoryTool(RetrievalKernel kernel)
    : AgentTool<SearchMemoryParams, SearchMemoryDetails>
{
    public override string Name => "search_memory";
    public override string Label => "搜索记忆";
    public override string Description => "在整个项目向量库中语义搜索";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<SearchMemoryDetails>> ExecuteAsync(
        string toolCallId,
        SearchMemoryParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<SearchMemoryDetails>>? onUpdate = null
    )
    {
        var documentKinds = args.TypeFilter switch
        {
            "character" => new List<DocumentKind> { DocumentKind.CharacterProfile },
            "chapter" => new List<DocumentKind>
            {
                DocumentKind.ChapterSummary,
                DocumentKind.ChapterBodyChunk,
            },
            "plotline" => new List<DocumentKind> { DocumentKind.PlotlineProfile },
            "faction" => new List<DocumentKind> { DocumentKind.FactionProfile },
            "location" => new List<DocumentKind> { DocumentKind.LocationProfile },
            "item" => new List<DocumentKind> { DocumentKind.ItemProfile },
            _ => null,
        };
        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "local",
            NovelId = args.NovelId,
            Query = args.Query,
            DocumentKinds = documentKinds,
            FinalTopK = args.TopK,
        };
        var results = await kernel.SearchAsync(request, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r =>
            $"[{r.DocumentKind.ToChineseLabel()}] (score: {r.Score:F2}) {r.Text}"
        );
        var text = results.Count == 0 ? "未找到相关内容" : string.Join("\n", lines);
        return new AgentToolResult<SearchMemoryDetails>
        {
            Content = [new TextContent(text)],
            Details = new SearchMemoryDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 搜索记忆工具的参数。
/// </summary>
public sealed record SearchMemoryParams
{
    [Description("要搜索的小说ID")]
    public string NovelId { get; init; } = "";

    [Description("搜索查询文本")]
    public string Query { get; init; } = "";

    [Description(
        "类型过滤：character / chapter / plotline / faction / location / item，留空则搜索全部"
    )]
    public string? TypeFilter { get; init; }

    [Description("返回结果数量（默认 10）")]
    public int TopK { get; init; } = 10;
}

/// <summary>
/// 搜索记忆工具的返回详情。
/// </summary>
public sealed record SearchMemoryDetails
{
    public int Count { get; init; }
}

// ── 检索角色工具 ──

/// <summary>
/// 检索角色工具：根据描述语义检索相关角色设定。
/// </summary>
public sealed class RecallCharacterTool(RetrievalKernel kernel, PluginStore pluginStore)
    : AgentTool<RecallCharacterParams, RecallCharacterDetails>
{
    public override string Name => "recall_character";
    public override string Label => "检索角色";
    public override string Description =>
        "检索与名称或描述相关的角色设定（支持按名精确查找和语义搜索）";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override string EntityType => "character";

    public override async Task<AgentToolResult<RecallCharacterDetails>> ExecuteAsync(
        string toolCallId,
        RecallCharacterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallCharacterDetails>>? onUpdate = null
    )
    {
        // 支持两个参数字段作为搜索文本来源（Name 优先，Query 兼容 LLM 的 query 传参习惯）
        var query = !string.IsNullOrWhiteSpace(args.Name) ? args.Name : args.Query ?? "";

        var lines = new List<string>();

        // 1. 确定性按名查找（精确匹配优先，子串匹配次之）
        if (!string.IsNullOrWhiteSpace(query))
        {
            var characters = await pluginStore.GetCharacters(args.NovelId, ct);
            ThrowIfCancelled(ct);

            var exact = characters.FirstOrDefault(c => c.Name == query);
            if (exact is not null)
            {
                lines.Add($"- {exact.Name}: 性格={exact.Personality}, 外貌={exact.Appearance}");
            }
            else
            {
                foreach (var c in characters)
                {
                    if (
                        c.Name.Contains(query, StringComparison.Ordinal)
                        || query.Contains(c.Name, StringComparison.Ordinal)
                    )
                    {
                        lines.Add($"- {c.Name}: 性格={c.Personality}, 外貌={c.Appearance}");
                    }
                }
            }
        }

        // 2. 语义搜索补充（名称查找未命中时）
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(query))
        {
            var request = new RetrievalSearchRequest
            {
                Domain = RetrievalDomain.NovelKnowledge,
                OwnerId = "local",
                NovelId = args.NovelId,
                Query = query,
                DocumentKinds = [DocumentKind.CharacterProfile],
                FinalTopK = 5,
            };
            var results = await kernel.SearchAsync(request, ct);
            ThrowIfCancelled(ct);

            foreach (var r in results)
            {
                if (r.SourceEntityId is null)
                    continue;

                var ch = await pluginStore.GetCharacter(args.NovelId, r.SourceEntityId, ct);
                ThrowIfCancelled(ct);
                lines.Add(
                    ch is not null
                        ? $"- {ch.Name}: 性格={ch.Personality}, 外貌={ch.Appearance}"
                        : $"- {r.Text} (score: {r.Score:F2})"
                );
            }
        }

        var text = lines.Count == 0 ? "未找到相关角色" : string.Join("\n", lines);
        return new AgentToolResult<RecallCharacterDetails>
        {
            Content = [new TextContent(text)],
            Details = new RecallCharacterDetails { Count = lines.Count },
        };
    }
}

/// <summary>
/// 检索角色工具的参数。
/// </summary>
public sealed record RecallCharacterParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("角色名称，用于精确匹配。也可传入描述文本进行语义搜索")]
    public string Name { get; init; } = "";

    [Description("角色描述/名称，用于语义检索匹配")]
    public string? Query { get; init; }
}

/// <summary>
/// 检索角色工具的返回详情。
/// </summary>
public sealed record RecallCharacterDetails
{
    public int Count { get; init; }
}

// ── 检索情节工具 ──

/// <summary>
/// 检索情节工具：根据描述语义检索类似情节片段。
/// </summary>
public sealed class RecallPlotTool(RetrievalKernel kernel)
    : AgentTool<RecallPlotParams, RecallPlotDetails>
{
    public override string Name => "recall_plot";
    public override string Label => "检索情节";
    public override string Description => "检索类似情节片段";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<RecallPlotDetails>> ExecuteAsync(
        string toolCallId,
        RecallPlotParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallPlotDetails>>? onUpdate = null
    )
    {
        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "local",
            NovelId = args.NovelId,
            Query = args.Description,
            DocumentKinds = [DocumentKind.PlotlineProfile],
            FinalTopK = 5,
        };
        var results = await kernel.SearchAsync(request, ct);
        ThrowIfCancelled(ct);
        var lines = results.Select(r => $"- {r.Text} (score: {r.Score:F2})");
        var text = results.Count == 0 ? "未找到相关情节" : string.Join("\n", lines);
        return new AgentToolResult<RecallPlotDetails>
        {
            Content = [new TextContent(text)],
            Details = new RecallPlotDetails { Count = results.Count },
        };
    }
}

/// <summary>
/// 检索情节工具的参数。
/// </summary>
public sealed record RecallPlotParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("情节描述，用于语义检索匹配")]
    public string Description { get; init; } = "";
}

/// <summary>
/// 检索情节工具的返回详情。
/// </summary>
public sealed record RecallPlotDetails
{
    public int Count { get; init; }
}

// ── 一致性检查工具 ──

/// <summary>
/// 一致性检查工具：检测当前文本与已有设定的一致性（如角色外貌）。
/// </summary>
public sealed class FindConsistencyTool(PluginStore store)
    : AgentTool<FindConsistencyParams, ConsistencyDetails>
{
    public override string Name => "find_consistency";
    public override string Label => "一致性检查";
    public override string Description => "检测当前文本与已有设定的一致性（如角色外貌）";
    public override ToolCategory Category => ToolCategory.EntityRead;

    public override async Task<AgentToolResult<ConsistencyDetails>> ExecuteAsync(
        string toolCallId,
        FindConsistencyParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ConsistencyDetails>>? onUpdate = null
    )
    {
        // 简单实现：检查文本中提到的角色名是否在角色列表中
        var characters = await store.GetCharacters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var mentionedNames = characters.Where(c => args.Text.Contains(c.Name)).ToList();

        // 构建结果
        var lines = mentionedNames
            .Select(ch => $"角色「{ch.Name}」: 外貌={ch.Appearance}, 性格={ch.Personality}")
            .ToList();

        var text = lines.Count == 0 ? "未发现相关角色引用" : string.Join("\n", lines);
        return new AgentToolResult<ConsistencyDetails>
        {
            Content = [new TextContent(text)],
            Details = new ConsistencyDetails { CharacterCount = mentionedNames.Count },
        };
    }
}

/// <summary>
/// 一致性检查工具的参数。
/// </summary>
public sealed record FindConsistencyParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要检查一致性的文本内容")]
    public string Text { get; init; } = "";
}

/// <summary>
/// 一致性检查工具的返回详情。
/// </summary>
public sealed record ConsistencyDetails
{
    public int CharacterCount { get; init; }
}
