using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 幻觉扫描（fiction.hallucination.scan）— 反漂移审计：已生成正文的实体断言 vs 作者设定图。
/// 层1 自洽性熵（仪表信号）+ 层2 grounding（主判据）；findings id 前缀 HS.*。
///
/// 关键约定：章节引用一律规范化为 ch_{Order}（与 SetupDigestBuilder 同一 B2 约定）——
/// 云端 ChapterRefResolver 只认 ch_N/N/第N章 形式；直接使用 12 位十六进制 GUID 会使
/// 出场模式约束与所有权时窗比对全部失效（引用无法解析 → 跳过提取）。
/// </summary>
public sealed class ScanHallucinationTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : AgentTool<ScanHallucinationParams, HallucinationScanDetails>
{
    private const string Capability = "fiction.hallucination.scan";

    /// <summary>云端门禁：1..100 章（超出取最近 100 章）。</summary>
    private const int MaxChapters = 100;

    /// <summary>云端 payload 上限（MaxPayloadBytes/4 ≈ 300k 字）留安全裕量；超出的早期章节裁剪。</summary>
    private const int MaxContentChars = 250_000;

    public override string Name => "scan_hallucination";
    public override string Label => "幻觉扫描";
    public override string Description =>
        "反漂移审计：检查正文对角色生死/所在地/持有物/组织/招式等的断言是否与设定图冲突，输出漂移风险与可执行发现";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override string[] Aliases => ["幻觉扫描", "反漂移审计", "设定漂移检查", "幻觉检测"];
    public override string[] ExampleQueries =>
        [
            "检查正文有没有写出设定之外的内容",
            "角色是不是被写死了",
            "扫描设定漂移",
            "有没有前后矛盾的设定",
        ];

    public override async Task<AgentToolResult<HallucinationScanDetails>> ExecuteAsync(
        string toolCallId,
        ScanHallucinationParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<HallucinationScanDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novel = await store.GetNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return ErrorResult($"未找到小说（novel_id={args.NovelId}）");

        var allChapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var selected = (
            args.ChapterIds is { Count: > 0 }
                ? allChapters.Where(c => args.ChapterIds.Contains(c.Id))
                : allChapters
        )
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Order)
            .ToList();
        if (selected.Count == 0)
            return ErrorResult("没有可扫描的正文章节（至少需要一章有正文内容）");

        // 云端门禁（1..100 章 + 内容上限）→ 取最近窗口（最新章节最可能发生漂移）。
        var window = new List<Chapter>(Math.Min(selected.Count, MaxChapters));
        var totalChars = 0;
        for (var i = selected.Count - 1; i >= 0 && window.Count < MaxChapters; i--)
        {
            var content = selected[i].Content;
            if (window.Count > 0 && totalChars + content.Length > MaxContentChars)
                break; // 单章超限时仍扫该章（窗口为空不 break，保证至少一章）
            totalChars += content.Length;
            window.Add(selected[i]);
        }
        window.Reverse();

        var characters = await store.GetCharacters(args.NovelId, ct);
        var factions = await store.GetFactions(args.NovelId, ct);
        var relationships = await store.GetRelationshipsByNovel(args.NovelId, ct);
        var scenes = await store.GetScenesByNovel(args.NovelId, ct);
        var plotlines = await store.GetPlotlines(args.NovelId, ct);
        var timelineEvents = await store.GetTimelineEvents(args.NovelId, ct);
        var locations = await store.GetLocations(args.NovelId, ct);
        var items = await store.GetItems(args.NovelId, ct);
        var worldRules = await store.GetWorldRules(args.NovelId, ct);
        var appearances = await store.GetAppearances(args.NovelId, ct);
        var stateChanges = (await store.GetEntityStateChanges(args.NovelId, ct))
            .Where(c => c.Lifecycle == ChangeLifecycle.Committed)
            .ToList();
        ThrowIfCancelled(ct);

        // 设定图（含 E5a 演化数据：出场模式约束 + 声明状态演化豁免由云端消费）。
        // 章节参数用全量章节：引用规范化（ch_{Order}）需要全部章序，扫描窗口只裁剪正文。
        var setup = SetupDigestBuilder.Build(
            novel,
            characters,
            factions,
            relationships,
            scenes,
            plotlines,
            allChapters,
            timelineEvents,
            locations,
            items,
            worldRules,
            appearances,
            stateChanges
        );

        try
        {
            var result = await ExecuteWithDepthAsync(novel, setup, window, "standard", ct);
            return MapResult(result, selected.Count);
        }
        catch (CloudApiException ex)
            when (ex.Code == "invalid_analysis_input" && ex.Message.Contains("Depth"))
        {
            // 服务端拒绝该深度（422 invalid_analysis_input，按能力声明）→ 优雅降级重试一次
            var result = await ExecuteWithDepthAsync(novel, setup, window, "quick", ct);
            return MapResult(result, selected.Count);
        }
        catch (CloudApiException ex)
            when (ex.Code is "invalid_api_key" or "unauthorized" or "forbidden")
        {
            return ErrorResult(
                "⚠️ 云端幻觉扫描不可用（幻觉扫描），未执行分析。请使用 :cloud login 登录后重试。本地创作功能不受影响。"
            );
        }
        catch (CloudApiException ex)
        {
            var detail = CloudErrorMapper.IsBusinessRejection(ex.Code) ? $"：{ex.Message}" : "";
            return ErrorResult(
                $"⚠️ 云端幻觉扫描不可用（幻觉扫描），未执行分析（{ex.Code}{detail}）。本地创作功能不受影响。"
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ErrorResult(
                "⚠️ 云端幻觉扫描不可用（幻觉扫描），未执行分析。详情已写入日志。本地创作功能不受影响。"
            );
        }
    }

    private async Task<HarnessResult> ExecuteWithDepthAsync(
        Novel novel,
        SetupCheckInput setup,
        IReadOnlyList<Chapter> window,
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
            Chapters = window
                .Select(c => new ChapterInput(
                    c.Order > 0 ? $"ch_{c.Order}" : c.Id, // 云端 ChapterRefResolver 只认 ch_N/第N章
                    c.Order,
                    c.Title,
                    "main",
                    c.Content
                ))
                .ToList(),
            Context = new AnalysisContextInput { Setup = setup, NovelTitle = novel.Title },
        };
        // 内容寻址幂等键：正文或设定变更后重扫必得新结果（同 CheckSetupTool 的 content-hash 语义）
        var serialized = JsonSerializer.Serialize(request, ClientJsonDefaults.Cloud);
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)))[..12]
            .ToLowerInvariant();
        return await cloudHarness.ExecuteAsync(
            request,
            $"hallucination-scan-{novel.Id}-{hash}",
            ct
        );
    }

    private static AgentToolResult<HallucinationScanDetails> MapResult(
        HarnessResult result,
        int availableChapters
    )
    {
        var element = result.Result as JsonElement? ?? default;
        var details = ParseResult(element);
        return new AgentToolResult<HallucinationScanDetails>
        {
            Content = [new TextContent(BuildReportText(details, availableChapters))],
            Details = details,
        };
    }

    private static HallucinationScanDetails ParseResult(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new HallucinationScanDetails();

        var details = new HallucinationScanDetails();
        if (element.TryGetProperty("scan", out var scan) && scan.ValueKind == JsonValueKind.Object)
        {
            details = details with
            {
                RDriftScore = GetDouble(scan, "r-drift-score"),
                RiskLevel = GetString(scan, "risk-level"),
                ChaptersScanned = GetInt(scan, "chapters-scanned"),
                AttributesChecked = GetInt(scan, "attributes-checked"),
                EntitiesCovered = GetInt(scan, "entities-covered"),
            };

            if (
                scan.TryGetProperty("summary", out var summary)
                && summary.ValueKind == JsonValueKind.Object
            )
            {
                details = details with
                {
                    Conflicts = GetInt(summary, "conflicts"),
                    Undetermined = GetInt(summary, "undetermined"),
                    HighRisk = GetInt(summary, "high-risk"),
                };
            }

            if (
                scan.TryGetProperty("attribute-groups", out var groups)
                && groups.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var g in groups.EnumerateArray())
                {
                    details.AttributeGroups.Add(
                        new HallucinationAttributeGroup
                        {
                            EntityId = GetString(g, "entity-id"),
                            Attribute = GetString(g, "attribute"),
                            RDrift = GetDouble(g, "r-drift"),
                            ObservationCount = GetInt(g, "observation-count"),
                            DistinctValues = GetStringList(g, "distinct-values") ?? [],
                            DominantValue = GetString(g, "dominant-value"),
                        }
                    );
                }
            }
        }

        if (
            element.TryGetProperty("findings", out var findings)
            && findings.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var f in findings.EnumerateArray())
            {
                details.Findings.Add(
                    new HallucinationFindingEntry
                    {
                        Id = GetString(f, "id"),
                        Verdict = GetString(f, "verdict"),
                        Layer = GetString(f, "layer"),
                        Description = GetString(f, "description"),
                        Evidence = GetString(f, "evidence"),
                        Suggestion = GetString(f, "suggestion"),
                        InvolvedElementIds = GetStringList(f, "involved-element-ids"),
                    }
                );
            }
        }

        return details;
    }

    private static string BuildReportText(HallucinationScanDetails details, int availableChapters)
    {
        var sb = new StringBuilder();
        sb.Append("幻觉扫描完成：风险 ")
            .Append(LevelLabel(details.RiskLevel))
            .Append($"（漂移分 {details.RDriftScore:F3}）")
            .Append(
                $"；扫描 {details.ChaptersScanned} 章 / {details.AttributesChecked} 组断言 / {details.EntitiesCovered} 个实体。"
            );
        if (details.ChaptersScanned < availableChapters)
            sb.Append($"（正文超出云端上限，仅取最近 {details.ChaptersScanned} 章）");
        sb.AppendLine();
        sb.AppendLine(
            $"发现：矛盾 {details.Conflicts} · 待定 {details.Undetermined} · 高风险组 {details.HighRisk}。"
        );
        if (details.Findings.Count == 0)
        {
            sb.AppendLine("未发现漂移/矛盾（正文断言与设定一致）。");
        }
        else
        {
            sb.AppendLine("发现明细：");
            foreach (var f in details.Findings.Take(20))
            {
                sb.AppendLine($"- [{f.Verdict}] {f.Id}：{f.Description}");
                if (!string.IsNullOrWhiteSpace(f.Evidence))
                    sb.AppendLine($"  证据：{f.Evidence}");
                if (!string.IsNullOrWhiteSpace(f.Suggestion))
                    sb.AppendLine($"  建议：{f.Suggestion}");
            }
            if (details.Findings.Count > 20)
                sb.AppendLine($"…另有 {details.Findings.Count - 20} 条，详见结构化详情。");
        }
        if (details.AttributeGroups.Count > 0)
        {
            sb.AppendLine(
                "属性组："
                    + string.Join(
                        "；",
                        details.AttributeGroups.Select(g =>
                            $"{g.Attribute}({g.RDrift:F2}，{g.ObservationCount} 观测"
                            + (
                                string.IsNullOrWhiteSpace(g.DominantValue)
                                    ? ")"
                                    : $"，主导 {g.DominantValue})"
                            )
                        )
                    )
            );
        }
        return sb.ToString();
    }

    private static string LevelLabel(string riskLevel) =>
        riskLevel switch
        {
            "high" => "高",
            "medium" => "中",
            "low" => "低",
            _ => riskLevel,
        };

    private static AgentToolResult<HallucinationScanDetails> ErrorResult(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };

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
}

public sealed record ScanHallucinationParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要扫描的章节ID列表，留空则扫描全部章节（超出云端上限时取最近 100 章）")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}
