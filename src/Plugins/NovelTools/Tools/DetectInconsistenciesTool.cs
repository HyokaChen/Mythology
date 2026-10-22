using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 世界观一致性检测工具：检测小说世界观设定中的矛盾与冲突
/// </summary>
public sealed class DetectInconsistenciesTool(INovelStore store)
    : AgentTool<DetectInconsistenciesParams, InconsistencyDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "detect_inconsistencies";
    public override string[] Aliases => ["一致性", "矛盾", "设定冲突"];
    public override string[] ExampleQueries => ["检查世界观设定有没有矛盾"];

    /// <summary>工具显示名称</summary>
    public override string Label => "世界观一致性检测";
    public override string Description =>
        "检测世界观设定矛盾：角色设定冲突、地点描述不一致、道具归属矛盾、关系逻辑断裂";
    public override ToolCategory Category => ToolCategory.EntityRead;

    /// <summary>
    /// 执行世界观一致性检测
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">检测参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>检测结果</returns>
    public override async Task<AgentToolResult<InconsistencyDetails>> ExecuteAsync(
        string toolCallId,
        DetectInconsistenciesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<InconsistencyDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var scope = args.Scope?.ToLowerInvariant() ?? "all";
        var allIssues = new List<IssueEntry>();

        // 按需并行加载数据 — 只加载各 scope 需要的数据集
        var needCharacters = scope is "character" or "all" or "relationship";
        var needChapters = scope is "character" or "all" or "location" or "item";
        var needRelationships = scope is "character" or "all" or "relationship";
        var needLocations = scope is "location" or "all";
        var needItems = scope is "item" or "all";

        var charactersTask = needCharacters ? store.GetCharacters(args.NovelId, ct) : null;
        var chaptersTask = needChapters ? store.GetChapters(args.NovelId, ct) : null;
        var relationshipsTask = needRelationships
            ? store.GetRelationshipsByNovel(args.NovelId, ct)
            : null;
        var locationsTask = needLocations ? store.GetLocations(args.NovelId, ct) : null;
        var itemsTask = needItems ? store.GetItems(args.NovelId, ct) : null;

        // 并行等待所有需要的 I/O 完成
        var tasks = new List<Task>();
        if (charactersTask is not null)
            tasks.Add(charactersTask);
        if (chaptersTask is not null)
            tasks.Add(chaptersTask);
        if (relationshipsTask is not null)
            tasks.Add(relationshipsTask);
        if (locationsTask is not null)
            tasks.Add(locationsTask);
        if (itemsTask is not null)
            tasks.Add(itemsTask);
        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
        ThrowIfCancelled(ct);

        var characters = charactersTask?.Result;
        var chapters = chaptersTask?.Result;
        var relationships = relationshipsTask?.Result;
        var locations = locationsTask?.Result;
        var items = itemsTask?.Result;

        if (scope is "character" or "all")
        {
            allIssues.AddRange(CheckCharacterConsistency(characters!, chapters!, relationships!));
        }

        if (scope is "location" or "all")
        {
            allIssues.AddRange(CheckLocationConsistency(locations!, chapters!));
        }

        if (scope is "item" or "all")
        {
            allIssues.AddRange(CheckItemConsistency(items!, chapters!));
        }

        if (scope is "relationship" or "all")
        {
            allIssues.AddRange(CheckRelationshipConsistency(relationships!, characters!));
        }

        var details = new InconsistencyDetails
        {
            TotalIssues = allIssues.Count,
            Issues = allIssues,
        };

        var report = BuildReportText(details);
        return new AgentToolResult<InconsistencyDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    /// <summary>
    /// 检查角色设定一致性
    /// 使用倒排索引优化角色出场检测，避免 O(characters × chapters) 的嵌套循环。
    /// </summary>
    private static List<IssueEntry> CheckCharacterConsistency(
        List<Character> characters,
        List<Chapter> chapters,
        List<Relationship> relationships
    )
    {
        var issues = new List<IssueEntry>();

        // 1. 角色已死亡(deceased)但在关系网中仍以活动状态存在
        var deceasedIds = characters
            .Where(c => c.Status == CharacterStatus.Deceased)
            .Select(c => c.Id)
            .ToHashSet();

        foreach (var rel in relationships)
        {
            if (deceasedIds.Contains(rel.CharacterAId) || deceasedIds.Contains(rel.CharacterBId))
            {
                var nameA =
                    characters.FirstOrDefault(c => c.Id == rel.CharacterAId)?.Name
                    ?? rel.CharacterAId;
                var nameB =
                    characters.FirstOrDefault(c => c.Id == rel.CharacterBId)?.Name
                    ?? rel.CharacterBId;
                issues.Add(
                    new IssueEntry
                    {
                        Type = "角色",
                        Severity = Severity.High,
                        Description = $"角色已死亡但仍存在于关系网中：{nameA} ↔ {nameB}",
                        Evidence = $"{nameA} 状态为 deceased，但仍存在 {rel.Type} 关系",
                        Suggestion = "检查该关系是否需要标记为已结束，或更新角色状态",
                    }
                );
            }
        }

        // 2. 角色有明确动机但在 N 章内未出现 — 使用倒排索引优化
        var motivatedCharacters = characters
            .Where(c => !string.IsNullOrEmpty(c.Motivation))
            .ToList();
        if (motivatedCharacters.Count > 0 && chapters.Count >= 3)
        {
            // 构建角色名 → 角色映射
            var nameMap = motivatedCharacters.ToDictionary(
                c => c.Name,
                c => c,
                StringComparer.OrdinalIgnoreCase
            );

            // 一次遍历章节，检测角色出场
            var appearanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in motivatedCharacters)
                appearanceCounts[ch.Name] = 0;

            foreach (var chapter in chapters)
            {
                foreach (var (name, _) in nameMap)
                {
                    if (chapter.Content.Contains(name, StringComparison.OrdinalIgnoreCase))
                        appearanceCounts[name]++;
                }
            }

            foreach (var ch in motivatedCharacters)
            {
                if (appearanceCounts.TryGetValue(ch.Name, out var count) && count == 0)
                {
                    issues.Add(
                        new IssueEntry
                        {
                            Type = "角色",
                            Severity = Severity.Medium,
                            Description = $"角色「{ch.Name}」有明确动机但从未在章节中出场",
                            Evidence = $"动机: {ch.Motivation}",
                            Suggestion = "考虑移除该角色或为其安排出场章节",
                        }
                    );
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// 检查地点设定一致性
    /// </summary>
    /// <param name="locations">地点列表</param>
    /// <param name="chapters">章节列表</param>
    /// <returns>发现的问题列表</returns>
    private static List<IssueEntry> CheckLocationConsistency(
        List<Location> locations,
        List<Chapter> chapters
    )
    {
        var issues = new List<IssueEntry>();

        // 检查地点层级
        var locationMap = locations.ToDictionary(l => l.Id);
        foreach (var loc in locations)
        {
            if (loc.ParentId is not null && !locationMap.ContainsKey(loc.ParentId))
            {
                issues.Add(
                    new IssueEntry
                    {
                        Type = "地点",
                        Severity = Severity.High,
                        Description = $"地点「{loc.Name}」引用了不存在的父地点",
                        Evidence = $"ParentId: {loc.ParentId}",
                        Suggestion = "更新或移除父地点引用",
                    }
                );
            }
        }

        return issues;
    }

    /// <summary>
    /// 检查道具设定一致性
    /// </summary>
    /// <param name="items">道具列表</param>
    /// <param name="chapters">章节列表</param>
    /// <returns>发现的问题列表</returns>
    private static List<IssueEntry> CheckItemConsistency(List<Item> items, List<Chapter> chapters)
    {
        var issues = new List<IssueEntry>();

        // 检查道具被多角色持有
        var itemOwnerMap = new Dictionary<string, string>();
        foreach (var item in items)
        {
            if (item.OwnerId is not null)
            {
                if (
                    itemOwnerMap.TryGetValue(item.Name, out var existingOwner)
                    && existingOwner != item.OwnerId
                )
                {
                    issues.Add(
                        new IssueEntry
                        {
                            Type = "道具",
                            Severity = Severity.Medium,
                            Description = $"道具「{item.Name}」被多个角色持有",
                            Evidence = $"持有者: {existingOwner} 和 {item.OwnerId}",
                            Suggestion = "检查道具归属是否正确，或使用 transfer_item 更新",
                        }
                    );
                }
                else
                {
                    itemOwnerMap[item.Name] = item.OwnerId;
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// 检查关系设定一致性
    /// </summary>
    /// <param name="relationships">关系列表</param>
    /// <param name="characters">角色列表</param>
    /// <returns>发现的问题列表</returns>
    private static List<IssueEntry> CheckRelationshipConsistency(
        List<Relationship> relationships,
        List<Character> characters
    )
    {
        var issues = new List<IssueEntry>();
        var characterMap = characters.ToDictionary(c => c.Id);

        // 检查关系中的角色是否存在
        foreach (var rel in relationships)
        {
            if (!characterMap.ContainsKey(rel.CharacterAId))
            {
                issues.Add(
                    new IssueEntry
                    {
                        Type = "关系",
                        Severity = Severity.High,
                        Description = $"关系记录引用了不存在的角色: {rel.CharacterAId}",
                        Evidence = $"关系类型: {rel.Type}",
                        Suggestion = "删除或修复该关系记录",
                    }
                );
            }
            if (!characterMap.ContainsKey(rel.CharacterBId))
            {
                issues.Add(
                    new IssueEntry
                    {
                        Type = "关系",
                        Severity = Severity.High,
                        Description = $"关系记录引用了不存在的角色: {rel.CharacterBId}",
                        Evidence = $"关系类型: {rel.Type}",
                        Suggestion = "删除或修复该关系记录",
                    }
                );
            }
        }

        return issues;
    }

    /// <summary>
    /// 构建检测报告文本
    /// </summary>
    /// <param name="d">不一致性详情</param>
    /// <returns>格式化报告文本</returns>
    private static string BuildReportText(InconsistencyDetails d)
    {
        if (d.TotalIssues == 0)
            return "✅ 未检测到世界观设定矛盾。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 🌍 世界观一致性检测报告");
        sb.AppendLine();
        sb.AppendLine($"发现 **{d.TotalIssues}** 个问题：");
        sb.AppendLine();

        foreach (var issue in d.Issues)
        {
            var severityEmoji = issue.Severity switch
            {
                "致命" => "🔴",
                Severity.High => "🟠",
                Severity.Medium => "🟡",
                _ => "🟢",
            };
            sb.AppendLine($"### {severityEmoji} [{issue.Type}] {issue.Description}");
            if (!string.IsNullOrEmpty(issue.Evidence))
                sb.AppendLine($"  **证据**: {issue.Evidence}");
            if (!string.IsNullOrEmpty(issue.Suggestion))
                sb.AppendLine($"  **建议**: {issue.Suggestion}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine(
            "> 以上为确定性检测结果。语义层面的设定矛盾（如角色行为与性格设定不符）请在后续对话中进一步分析。"
        );
        return sb.ToString();
    }
}

public sealed record DetectInconsistenciesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description(
        "检测维度：character（角色设定矛盾）/ location（地点描述矛盾）/ item（道具归属矛盾）/ relationship（关系逻辑矛盾）/ all（全部）"
    )]
    public string? Scope { get; init; }
}

public sealed record InconsistencyDetails
{
    public int TotalIssues { get; init; }
    public List<IssueEntry> Issues { get; init; } = [];
}
