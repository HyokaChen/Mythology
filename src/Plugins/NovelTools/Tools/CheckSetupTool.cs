using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 设定检查（CHANGE_10 · fiction.setup.check）— 对设定阶段（正文存在之前）的
/// 角色/组织/地点/情节线/场景/关系/大纲做结构一致性、时间线自洽与大纲趣味性检查。
/// 与 CloudAnalysisTool 基类不同：本工具发 Chapters=[] + Context.Setup（设定图），
/// 不依赖章节正文。
/// </summary>
public sealed class CheckSetupTool(INovelStore store, IFictionHarnessClient? cloudHarness = null)
    : AgentTool<CheckSetupParams, CheckSetupDetails>
{
    private const string Capability = "fiction.setup.check";

    public override string Name => "check_setup";
    public override string Label => "设定检查";
    public override string Description =>
        "对设定阶段（角色/组织/地点/情节线/场景/关系/大纲）做结构一致性、时间线自洽与大纲趣味性检查";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override string[] Aliases => ["设定检查", "设定体检", "检查设定", "setup check"];
    public override string[] ExampleQueries =>
        ["检查设定是否自洽", "设定体检", "我的大纲有没有问题", "检查伏笔和情节线"];

    public override async Task<AgentToolResult<CheckSetupDetails>> ExecuteAsync(
        string toolCallId,
        CheckSetupParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<CheckSetupDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novel = await store.GetNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
        {
            return new AgentToolResult<CheckSetupDetails>
            {
                Content = [new TextContent($"未找到小说（novel_id={args.NovelId}）")],
                IsError = true,
            };
        }

        var characters = await store.GetCharacters(args.NovelId, ct);
        var factions = await store.GetFactions(args.NovelId, ct);
        var relationships = await store.GetRelationshipsByNovel(args.NovelId, ct);
        var scenes = await store.GetScenesByNovel(args.NovelId, ct);
        var plotlines = await store.GetPlotlines(args.NovelId, ct);
        var chapters = await store.GetChapters(args.NovelId, ct);
        var timelineEvents = await store.GetTimelineEvents(args.NovelId, ct);
        ThrowIfCancelled(ct);

        if (
            characters.Count == 0
            && plotlines.Count == 0
            && scenes.Count == 0
            && chapters.Count == 0
        )
        {
            return new AgentToolResult<CheckSetupDetails>
            {
                Content = [new TextContent("设定为空，请先填写角色/情节线等设定")],
                IsError = true,
            };
        }

        var locations = await store.GetLocations(args.NovelId, ct);
        var items = await store.GetItems(args.NovelId, ct);
        var worldRules = await store.GetWorldRules(args.NovelId, ct);
        ThrowIfCancelled(ct);

        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            chapters,
            timelineEvents,
            locations,
            items,
            worldRules
        );

        const string depth = "standard";
        try
        {
            var result = await ExecuteWithDepthAsync(novel, setup, depth, ct);
            return MapResult(result);
        }
        catch (CloudApiException ex)
            when (ex.Code == "invalid_analysis_input" && ex.Message.Contains("Depth"))
        {
            // 服务端拒绝该深度（422 invalid_analysis_input，按能力声明）→ 优雅降级重试一次
            var result = await ExecuteWithDepthAsync(novel, setup, "quick", ct);
            return MapResult(result);
        }
        catch (CloudApiException ex)
            when (ex.Code is "invalid_api_key" or "unauthorized" or "forbidden")
        {
            return new AgentToolResult<CheckSetupDetails>
            {
                Content =
                [
                    new TextContent(
                        "⚠️ 云端设定检查不可用（设定检查），未执行分析。请使用 :cloud login 登录后重试。本地创作功能不受影响。"
                    ),
                ],
                IsError = true,
            };
        }
        catch (CloudApiException ex)
        {
            var detail = CloudErrorMapper.IsBusinessRejection(ex.Code) ? $"：{ex.Message}" : "";
            return new AgentToolResult<CheckSetupDetails>
            {
                Content =
                [
                    new TextContent(
                        $"⚠️ 云端设定检查不可用（设定检查），未执行分析（{ex.Code}{detail}）。本地创作功能不受影响。"
                    ),
                ],
                IsError = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AgentToolResult<CheckSetupDetails>
            {
                Content =
                [
                    new TextContent(
                        "⚠️ 云端设定检查不可用（设定检查），未执行分析。详情已写入日志。本地创作功能不受影响。"
                    ),
                ],
                IsError = true,
            };
        }
    }

    private async Task<HarnessResult> ExecuteWithDepthAsync(
        Novel novel,
        SetupCheckInput setup,
        string depth,
        CancellationToken ct
    )
    {
        if (cloudHarness is null)
            throw new CloudApiException("unavailable", "云端未配置");

        var request = new HarnessRequest
        {
            NovelId = novel.Id,
            Capability = Capability,
            Depth = depth,
            Locale = "zh-CN",
            Chapters = [],
            Context = new AnalysisContextInput { Setup = setup, NovelTitle = novel.Title },
        };
        // 内容寻址幂等键：设定变更后重查必得新结果（CHANGE_10 §3.1 content-hash 缓存键语义）
        var serialized = JsonSerializer.Serialize(setup, ClientJsonDefaults.Cloud);
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)))[..12]
            .ToLowerInvariant();
        return await cloudHarness.ExecuteAsync(request, $"setup-check-{novel.Id}-{hash}", ct);
    }

    private static AgentToolResult<CheckSetupDetails> MapResult(HarnessResult result)
    {
        var element = result.Result as JsonElement? ?? default;
        var details = ParseResult(element);
        return new AgentToolResult<CheckSetupDetails>
        {
            Content = [new TextContent(BuildReportText(details))],
            Details = details,
        };
    }

    private static CheckSetupDetails ParseResult(JsonElement element)
    {
        var findings = new List<SetupFindingEntry>();
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("findings", out var findingsJson)
            && findingsJson.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var f in findingsJson.EnumerateArray())
            {
                findings.Add(
                    new SetupFindingEntry
                    {
                        Id = GetString(f, "id"),
                        Verdict = GetString(f, "verdict"),
                        Layer = GetString(f, "layer"),
                        Description = GetString(f, "description"),
                        Evidence = GetString(f, "evidence"),
                        FixCandidates = GetStringList(f, "fix-candidates"),
                        InvolvedElementIds = GetStringList(f, "involved-element-ids"),
                        Suggestion = GetString(f, "suggestion"),
                    }
                );
            }
        }

        var outline = new List<string>();
        var outlineJson = default(JsonElement);
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("outline-metrics", out outlineJson)
            && outlineJson.ValueKind == JsonValueKind.Object
        )
        {
            outline.Add($"弧线形状：{GetString(outlineJson, "arc-shape")}");
            var gapDensity = GetDouble(outlineJson, "gap-density");
            if (gapDensity > 0)
                outline.Add($"缺口密度：{gapDensity:F2}（每章开放缺口数）");
            var dormancyTable = GetDormancyTable(outlineJson);
            if (dormancyTable is not null)
                outline.Add($"休眠区间：{dormancyTable}");
            var conv2 = GetString(outlineJson, "convergence");
            if (!string.IsNullOrWhiteSpace(conv2))
                outline.Add($"收束度：{conv2}");
            var hookCov = GetDouble(outlineJson, "hook-coverage");
            if (hookCov > 0)
                outline.Add($"钩子覆盖：{hookCov:P0}");
            var peak = GetInt(outlineJson, "peak-gap-chapter");
            if (peak > 0)
                outline.Add($"缺口峰值章：ch_{peak}");
            if (
                outlineJson.TryGetProperty("warnings", out var warnings)
                && warnings.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var w in warnings.EnumerateArray())
                {
                    if (w.ValueKind == JsonValueKind.String)
                        outline.Add($"⚠ {w.GetString()}");
                }
            }
        }

        // summary 计数头
        var conflicts = 0;
        var anomalies = 0;
        var undetermined = 0;
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("summary", out var summaryJson)
            && summaryJson.ValueKind == JsonValueKind.Object
        )
        {
            conflicts = GetInt(summaryJson, "conflicts");
            anomalies = GetInt(summaryJson, "anomalies");
            undetermined = GetInt(summaryJson, "undetermined");
        }
        var structureNotes = BuildStructureNotes(outlineJson);

        // Phase 4 新增键（additive）：设定图来源摘要 + 分卷大纲指标。
        // 旧服务端 / quick 档 → 两键缺席 → 解析为 null/空表，展示层自动隐藏（零破坏）。
        SetupGraphInfo? graph = null;
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("graph", out var graphJson)
            && graphJson.ValueKind == JsonValueKind.Object
        )
        {
            graph = new SetupGraphInfo(
                GetString(graphJson, "source"),
                GetInt(graphJson, "node-count"),
                GetInt(graphJson, "edge-count")
            );
        }

        var volumes = new List<VolumeOutlineEntry>();
        if (
            outlineJson.ValueKind == JsonValueKind.Object
            && outlineJson.TryGetProperty("volumes", out var volumesJson)
            && volumesJson.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var v in volumesJson.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object)
                    continue;
                volumes.Add(
                    new VolumeOutlineEntry
                    {
                        VolumeId = GetString(v, "volume-id"),
                        Name = GetString(v, "name"),
                        ChapterCount = GetInt(v, "chapter-count"),
                        BeatCount = GetInt(v, "beat-count"),
                        TensionMean = GetDouble(v, "tension-mean"),
                        TensionMax = GetDouble(v, "tension-max"),
                        TensionMin = GetDouble(v, "tension-min"),
                        ArcShape = GetString(v, "arc-shape"),
                        HookCoverage = GetDouble(v, "hook-coverage"),
                        FlatLine = GetBool(v, "flat-line"),
                        PeakCount = GetInt(v, "peak-count"),
                    }
                );
            }
        }

        // v3 · 锚点覆盖率（harness "能不能按设定写" 总闸仪表）

        return new CheckSetupDetails
        {
            Findings = findings,
            OutlineSummary = outline,
            L0Only =
                element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("l0-only", out var l0)
                && l0.ValueKind == JsonValueKind.True,
            Conflicts = conflicts,
            Anomalies = anomalies,
            Undetermined = undetermined,
            StructureNotes = structureNotes,
            Graph = graph,
            Volumes = volumes,
        };
    }

    private static string BuildReportText(CheckSetupDetails details)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 设定检查报告");
        sb.AppendLine();
        if (details.L0Only)
        {
            sb.AppendLine(
                "> 基础档（quick）：仅执行基础结构检查（L0）。提高分析深度（standard/deep）可获得时间线、关系与大纲趣味性分析。"
            );
            sb.AppendLine();
        }
        if (details.Findings.Count == 0)
        {
            sb.AppendLine("**未发现设定问题**（结构自洽，悬空引用/自环/环为 0）。");
        }
        else
        {
            foreach (var group in details.Findings.GroupBy(f => f.Verdict))
            {
                var icon = group.Key switch
                {
                    "conflict" => "🔴",
                    "anomaly" => "🟡",
                    "undetermined" => "🔵",
                    _ => "⚪",
                };
                sb.AppendLine($"**{icon} {VerdictLabel(group.Key)}（{group.Count()}）**");
                foreach (var f in group)
                {
                    sb.AppendLine($"- `{f.Id}` {f.Description}");
                    if (!string.IsNullOrWhiteSpace(f.Suggestion))
                        sb.AppendLine($"  - 建议：{f.Suggestion}");
                }
                sb.AppendLine();
            }
        }
        if (details.OutlineSummary.Count > 0)
        {
            sb.AppendLine("**大纲趣味性**");
            foreach (var line in details.OutlineSummary)
                sb.AppendLine($"- {line}");
        }
        if (details.Graph is { } graph)
        {
            sb.AppendLine("**设定图**");
            sb.AppendLine($"- 来源：{graph.Describe()}");
        }
        if (details.Volumes.Count > 0)
        {
            sb.AppendLine("**分卷大纲**");
            foreach (var volume in details.Volumes)
                sb.AppendLine($"- {volume.Describe()}");
        }
        return sb.ToString();
    }

    private static string VerdictLabel(string verdict) =>
        verdict switch
        {
            "conflict" => "矛盾",
            "anomaly" => "异常",
            "undetermined" => "未定/澄清",
            _ => verdict,
        };

    private static string GetString(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return "";
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int GetInt(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return 0;
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static double GetDouble(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return 0;
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
    }

    private static bool GetBool(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static List<string>? GetStringList(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
        return list.Count > 0 ? list : null;
    }

    private static string? GetDormancyTable(JsonElement outlineJson)
    {
        if (
            !outlineJson.TryGetProperty("dormancy", out var d)
            || d.ValueKind != JsonValueKind.Object
        )
            return null;
        var entries = new List<string>();
        foreach (var prop in d.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
                entries.Add($"{prop.Name} {prop.Value.GetInt32()} 章");
        }
        return entries.Count == 0 ? null : string.Join("；", entries);
    }

    private static string? BuildStructureNotes(JsonElement outlineJson)
    {
        if (outlineJson.ValueKind != JsonValueKind.Object)
            return null;
        var notes = new List<string>();
        var conv = GetString(outlineJson, "convergence");
        if (!string.IsNullOrWhiteSpace(conv))
            notes.Add(conv);
        if (GetBool(outlineJson, "flat-line"))
            notes.Add("⚠ 存在平线段");
        var pk = GetInt(outlineJson, "peak-count");
        if (pk > 1)
            notes.Add($"多峰 {pk} 个");
        var hk = GetDouble(outlineJson, "hook-coverage");
        if (hk > 0)
            notes.Add($"钩子覆盖 {hk:P0}");
        var pL90 = GetDouble(outlineJson, "payoff-latency-p90");
        if (pL90 > 0)
            notes.Add($"回收延迟P90 {pL90:F1} 章");
        return notes.Count == 0 ? null : string.Join(" · ", notes);
    }
}

public sealed record CheckSetupParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}
