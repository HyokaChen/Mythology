using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.WritingTools.Infrastructure;

namespace Plugins.WritingTools;

/// <summary>
/// 复合诊断工具 — 一次调用替代多个分析器，按深度选择维度，返回统一报告。
/// CHANGE_07：本地诊断算法已删除，全部通过 IFictionHarnessClient 调云端。
/// 云端不可用时返回可读错误，不伪造分数，不阻塞本地创作。
/// </summary>
public sealed class DiagnoseWritingTool(
    INovelStore store,
    Agent? agent = null,
    IFictionHarnessClient? cloudHarness = null
) : AgentTool<DiagnoseWritingParams, DiagnosticReport>
{
    public override string Name => "diagnose_writing";
    public override string Label => "写作诊断";
    public override string Description =>
        "复合诊断写作质量：按深度(quick/standard/deep)分析多维度(代入感/爽感/冲突/节奏/描写/对话/去AI味)，返回段落级问题定位和加权总分";
    public override ToolCategory Category => ToolCategory.EntityRead;

    /// <summary>
    /// 云端 Harness 客户端 — 诊断全部走云端，无本地回退。
    /// </summary>
    private IFictionHarnessClient? CloudHarness { get; } = cloudHarness;

    public override async Task<AgentToolResult<DiagnosticReport>> ExecuteAsync(
        string toolCallId,
        DiagnoseWritingParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<DiagnosticReport>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var novelId = args.NovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            var novels = await store.GetAllNovels(ct);
            novelId = novels.FirstOrDefault()?.Id;
            if (novelId is null)
                return new AgentToolResult<DiagnosticReport>
                {
                    Content = [new TextContent("未找到小说项目，请先创建或加载小说")],
                    IsError = true,
                };
        }

        var novel = await store.GetNovel(novelId, ct);
        if (novel is null)
            return new AgentToolResult<DiagnosticReport>
            {
                Content = [new TextContent($"小说项目 {novelId} 不存在")],
                IsError = true,
            };

        var chapters = await store.GetChapters(novelId, ct);
        ThrowIfCancelled(ct);

        if (chapters.Count == 0)
            return new AgentToolResult<DiagnosticReport>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };

        var targetChapters = (
            args.ChapterIds is { Count: > 0 }
                ? chapters.Where(c => args.ChapterIds.Contains(c.Id))
                : chapters
        )
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .ToList();

        if (targetChapters.Count == 0)
            return new AgentToolResult<DiagnosticReport>
            {
                Content = [new TextContent("未找到指定章节")],
                IsError = true,
            };

        var profile = WritingProfile.FromNovel(novel);
        var depth = args.Depth switch
        {
            "quick" => DiagnosticDepth.Quick,
            "deep" => DiagnosticDepth.Deep,
            _ => DiagnosticDepth.Standard,
        };

        // 云端不可用 → 返回可读错误，不伪造分数
        if (CloudHarness is null)
        {
            return new AgentToolResult<DiagnosticReport>
            {
                Content =
                [
                    new TextContent(
                        "⚠️ 云端诊断不可用：未配置云端连接。请使用 `:cloud login` 登录后重试。本地创作功能不受影响。"
                    ),
                ],
                IsError = true,
            };
        }

        var mergedText = string.Join("\n\n", targetChapters.Select(c => c.Content));
        ThrowIfCancelled(ct);

        // LLM 语义提取（本地基础能力，§5.5）：只上传云端引擎消费的交互事件
        // （story.check 网络层 N1/N2 交互图）；其余提取字段本地无消费方，不落 wire。
        SemanticExtractionInput? semanticExtraction = null;
        if (agent is not null && depth != DiagnosticDepth.Quick)
        {
            try
            {
                var llmResult = await LlmExtractionBridge.ExtractAsync(
                    agent,
                    mergedText,
                    novel.Characters,
                    ct
                );
                semanticExtraction =
                    llmResult.Interactions.Count > 0
                        ? new SemanticExtractionInput(
                            llmResult
                                .Interactions.Select(i => new CharacterInteractionInput(
                                    i.From,
                                    i.To,
                                    i.Type,
                                    i.ParagraphIndex
                                ))
                                .ToList()
                        )
                        : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            { /* LLM 不可用 → 静默降级，云端有 jieba 兜底 */
            }
        }

        // 云端诊断
        var report = await CloudDiagnoseAsync(
            targetChapters,
            novel,
            profile,
            depth,
            novelId,
            semanticExtraction,
            ct
        );

        var reportText = BuildReportText(report, depth, targetChapters.Count);
        return new AgentToolResult<DiagnosticReport>
        {
            Content = [new TextContent(reportText)],
            Details = report,
        };
    }

    /// <summary>
    /// 云端诊断 — 通过 IFictionHarnessClient 提交合并章节，映射为本地 DiagnosticReport。
    /// 云端不可用/未授权时返回可读错误，不抛出，不伪造分数，不阻塞本地创作。
    /// </summary>
    private async Task<DiagnosticReport> CloudDiagnoseAsync(
        List<Chapter> chapters,
        Novel novel,
        WritingProfile profile,
        DiagnosticDepth depth,
        string novelId,
        SemanticExtractionInput? semanticExtraction,
        CancellationToken ct
    )
    {
        try
        {
            var request = new HarnessRequest
            {
                Capability = "fiction.story.check",
                Depth = depth switch
                {
                    DiagnosticDepth.Quick => "quick",
                    DiagnosticDepth.Deep => "deep",
                    _ => "standard",
                },
                Locale = "zh-CN",
                Chapters = chapters
                    .Select(
                        (c, index) =>
                            new ChapterInput(
                                c.Id,
                                c.Order,
                                c.Title,
                                index == 0 ? "target" : "context",
                                c.Content
                            )
                    )
                    .ToList(),
                Context = HarnessContextBuilder.Build(novel, profile.Genre),
                Profile = WritingProfileInput.FromProfile(profile),
                SemanticExtraction = semanticExtraction,
            };

            var result = await CloudHarness!.ExecuteAsync(
                request,
                $"diagnose-{novelId}-{chapters[0].Id}",
                ct
            );

            return result.Result is not JsonElement element
                ? CloudUnavailableReport(depth)
                : StoryCheckResultMapper.Map(element, depth);
        }
        catch (CloudApiException ex)
        {
            // 业务拒绝（套餐/契约/参数/载荷）不是连接故障：直接呈现服务端 detail
            // （如"深度超出当前套餐"），避免误报「云端不可用」。
            // 超时同理：连接是通的，给可读的超时文案（60s 单请求 / 2 分钟整体等待上限）。
            var detail =
                ex.Code == "result_timeout" || CloudErrorMapper.IsBusinessRejection(ex.Code)
                    ? ex.Message
                    : null;
            var state = CloudErrorMapper.ToConnectionState(ex.Code);
            return CloudErrorReport(depth, state, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CloudUnavailableReport(depth);
        }
    }

    /// <summary>
    /// 云端不可用时的空报告 — 不伪造分数，明确标注未执行分析。
    /// </summary>
    private static DiagnosticReport CloudUnavailableReport(DiagnosticDepth depth) =>
        new()
        {
            Depth = depth,
            OverallScore = 0,
            DimensionScores = new Dictionary<string, double>(),
            Issues =
            [
                new DiagnosticIssue
                {
                    Dimension = "云端",
                    Severity = Severity.High,
                    Description = "云端不可用，未执行分析。本地创作功能不受影响。",
                },
            ],
            Strengths = [],
        };

    /// <summary>
    /// 云端错误报告 — 按连接状态返回可读提示；业务拒绝时用服务端 detail 覆盖。
    /// </summary>
    private static DiagnosticReport CloudErrorReport(
        DiagnosticDepth depth,
        CloudConnectionState state,
        string? detail = null
    )
    {
        var message =
            detail
            ?? state switch
            {
                CloudConnectionState.Authenticating =>
                    "云端未登录，请使用 :cloud login 登录后重试。",
                CloudConnectionState.RateLimited => "云端请求频率受限，请稍后重试。",
                CloudConnectionState.QuotaExceeded => "云端额度已用尽，请升级套餐或等待额度刷新。",
                _ => "云端不可用，未执行分析。本地创作功能不受影响。",
            };

        return new DiagnosticReport
        {
            Depth = depth,
            OverallScore = 0,
            DimensionScores = new Dictionary<string, double>(),
            Issues =
            [
                new DiagnosticIssue
                {
                    Dimension = "云端",
                    Severity = Severity.High,
                    Description = message,
                },
            ],
            Strengths = [],
        };
    }

    private static string BuildReportText(
        DiagnosticReport report,
        DiagnosticDepth depth,
        int chapterCount
    )
    {
        var sb = new StringBuilder();
        var depthLabel = depth switch
        {
            DiagnosticDepth.Quick => "快速",
            DiagnosticDepth.Standard => "标准",
            DiagnosticDepth.Deep => "深度",
            _ => "标准",
        };

        sb.AppendLine("## 🔍 写作诊断报告");
        sb.AppendLine();
        sb.AppendLine($"**诊断深度**: {depthLabel} | **分析章节**: {chapterCount} 章");

        // 云端不可用时直接显示错误
        if (report is { OverallScore: 0, DimensionScores.Count: 0, Issues.Count: > 0 })
        {
            var cloudIssue = report.Issues.FirstOrDefault(i => i.Dimension == "云端");
            if (cloudIssue is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"**⚠️ {cloudIssue.Description}**");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("> 本地创作功能（编辑/续写/检索）不受影响。");
                return sb.ToString();
            }
        }

        sb.AppendLine($"**综合评分**: **{report.OverallScore:F1}** / 100");
        sb.AppendLine();

        // 维度评分表
        sb.AppendLine("### 维度评分");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 分数 | 状态 | 阈值 |");
        sb.AppendLine("|------|------|------|------|");

        foreach (var (dim, score) in report.DimensionScores)
        {
            var emoji = ScoreEmoji(score);
            sb.AppendLine($"| {dim} | {score:F1} | {emoji} | - |");
        }
        sb.AppendLine();

        // 章节特征（云端承载，公共报告不含 Features 字段）

        // 优势
        if (report.Strengths.Count > 0)
        {
            sb.AppendLine("### ✅ 亮点");
            foreach (var s in report.Strengths)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        // 问题
        if (report.Issues.Count > 0)
        {
            sb.AppendLine("### ⚠️ 问题");
            foreach (var issue in report.Issues)
            {
                var emoji = issue.Severity switch
                {
                    Severity.High => "🔴",
                    Severity.Medium => "🟡",
                    _ => "🟢",
                };
                var dimLabel = issue.Dimension;
                var location =
                    issue.ParagraphIndex >= 0 ? $" (第{issue.ParagraphIndex + 1}段)" : "";
                sb.AppendLine($"{emoji} **[{dimLabel}]{location}** {issue.Description}");
                if (!string.IsNullOrEmpty(issue.Evidence))
                    sb.AppendLine($"  证据: {issue.Evidence}");
                if (!string.IsNullOrEmpty(issue.Suggestion))
                    sb.AppendLine($"  建议: {issue.Suggestion}");
                if (!string.IsNullOrEmpty(issue.ProblematicText))
                    sb.AppendLine($"  原文: 「{issue.ProblematicText}」");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine($"> 诊断深度: {depthLabel}。使用 enhance_* 工具可针对具体维度进行增强。");
        return sb.ToString();
    }

    private static string ScoreEmoji(double score) =>
        score switch
        {
            >= 80 => "🟢 优秀",
            >= 60 => "🟡 良好",
            >= 40 => "🟠 一般",
            _ => "🔴 较差",
        };
}

public sealed record DiagnoseWritingParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }

    [Description("诊断深度: quick(3维度快速), standard(7维度标准), deep(13维度深度)")]
    public string Depth { get; init; } = "standard";
}
