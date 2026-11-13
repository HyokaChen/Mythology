using System.ComponentModel;
using System.Security.Cryptography;
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
        "复合诊断写作质量：按深度(quick/standard/deep)分析多维度(代入感/爽感/冲突/节奏/描写/对话/去AI味)，返回段落级问题定位和加权总分；mode=deai 时只返回云端去AI味 1-3 条完整报告/复测（默认自动审查，无需手动调用）";
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

        // 手动去 AI 味：quick + humanizer focus，只审计一个目标章；
        // 默认自动路径由 WritingFlowHook/HumanizerAuditService 承担，这里只做完整报告/复测。
        if (string.Equals(args.Mode, "deai", StringComparison.OrdinalIgnoreCase))
        {
            // 未指定 ChapterIds 时，默认审计最近一章（更接近“当前写作章”）。
            var targetChapter = targetChapters[^1];
            var deAiReport = CloudHarness is null
                ? BuildDeAiFallbackReport("未配置云端连接；本次未执行云端去AI味检测。")
                : await CloudDeAiAsync(targetChapter, novelId, ct);
            deAiReport = AttachDeAiAudit(deAiReport, args.MaxFindings, args.FullReport);

            var deAiText = BuildDeAiReportText(
                deAiReport,
                targetChapter,
                args.MaxFindings,
                args.FullReport
            );
            return new AgentToolResult<DiagnosticReport>
            {
                Content = [new TextContent(deAiText)],
                Details = deAiReport,
            };
        }

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
    /// 手动去 AI 味审计：只提交目标章，quick + humanizer focus（云端 focused quick = 2 units）。
    /// 远端失败不伪造 findings；返回带通用自检清单的降级报告。
    /// </summary>
    private async Task<DiagnosticReport> CloudDeAiAsync(
        Chapter chapter,
        string novelId,
        CancellationToken ct
    )
    {
        try
        {
            var request = new HarnessRequest
            {
                Capability = "fiction.story.check",
                Depth = "quick",
                Locale = "zh-CN",
                NovelId = novelId,
                Chapters =
                [
                    new ChapterInput(
                        chapter.Id,
                        chapter.Order,
                        chapter.Title,
                        "target",
                        chapter.Content ?? ""
                    ),
                ],
                Options = new AnalysisOptionsInput(AnalyzerFocus: ["humanizer"]),
            };

            var result = await CloudHarness!.ExecuteAsync(
                request,
                $"deai-{novelId}-{chapter.Id}-{HashContent(chapter.Content ?? "")}",
                ct
            );

            if (result.Result is not JsonElement element)
                return BuildDeAiFallbackReport("云端未返回可解析的去AI味结果。");

            return StoryCheckResultMapper.Map(element, DiagnosticDepth.Quick);
        }
        catch (CloudApiException ex)
        {
            return BuildDeAiFallbackReport(
                CloudErrorMapper.IsBusinessRejection(ex.Code) || ex.Code == "result_timeout"
                    ? $"云端去AI味检测未完成：{ex.Message}"
                    : "云端去AI味检测不可用。"
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BuildDeAiFallbackReport("云端去AI味检测不可用。");
        }
    }

    /// <summary>把云端 humanizer 问题整理为 1–3 条（FullReport 时全部）报告视图。</summary>
    private static DiagnosticReport AttachDeAiAudit(
        DiagnosticReport report,
        int maxFindings,
        bool fullReport
    )
    {
        if (report.DeAi is not null)
            return report;

        var all = report.Issues.Where(issue => issue.Dimension == DimensionKeys.Humanizer).ToList();

        // 旧服务端/畸形响应没有 humanizer 维度：绝不把「没返回」伪装成「未发现问题」。
        if (
            !report.DimensionScores.TryGetValue(DimensionKeys.Humanizer, out var humanizerScore)
            || (humanizerScore < 100 && all.Count == 0)
        )
            return BuildDeAiFallbackReport("云端未返回去AI味维度；本次未完成检测。");

        var actionable = all.Where(IsActionableIssue).ToList();
        var ordered = actionable
            .OrderByDescending(issue => SeverityRank(issue.Severity))
            .ThenByDescending(issue => issue.ImpactScore)
            .ToList();

        var limit = Math.Clamp(maxFindings, 1, 10);
        List<DiagnosticIssue> selected;
        if (fullReport)
        {
            // 完整报告也展开 X/H/C；默认只给可执行项，避免把「不改」项混进修复建议。
            selected = all.OrderByDescending(issue => SeverityRank(issue.Severity))
                .ThenByDescending(issue => issue.ImpactScore)
                .ToList();
        }
        else
        {
            // 同类合并：每种 sub-type 只留影响最大的一条，避免 3 条都在说同一个模板。
            selected = ordered
                .GroupBy(issue =>
                    string.IsNullOrWhiteSpace(issue.SubType) ? issue.Description : issue.SubType
                )
                .Select(group => group.First())
                .Take(limit)
                .ToList();
        }

        var findings = selected
            .Select(issue => new DeAiFinding
            {
                SubType = issue.SubType ?? "",
                Level = issue.Level ?? "",
                Severity = issue.Severity,
                Description = issue.Description,
                Evidence = issue.Evidence,
                Suggestion = issue.Suggestion,
                ParagraphIndex = issue.ParagraphIndex,
                ImpactScore = issue.ImpactScore,
            })
            .ToList();

        var score = report.DimensionScores.TryGetValue(DimensionKeys.Humanizer, out var value)
            ? value
            : 0;
        return report with
        {
            DeAi = new DeAiAudit
            {
                Findings = findings,
                SuppressedCount = fullReport ? 0 : Math.Max(0, actionable.Count - findings.Count),
                NoChangeCount = fullReport ? 0 : Math.Max(0, all.Count - actionable.Count),
                FullReport = fullReport,
                CloudAuditAvailable = true,
                RiskLevel = RiskLevel(score, actionable),
                Notes = ["这是云端规则审计，不是 AI 作者判定；比喻/声音需人审。"],
            },
        };
    }

    /// <summary>云端不可用时的去 AI 味降级报告：只给通用自检，不伪造具体 findings。</summary>
    private static DiagnosticReport BuildDeAiFallbackReport(string note) =>
        new()
        {
            Depth = DiagnosticDepth.Quick,
            DeAi = new DeAiAudit
            {
                CloudAuditAvailable = false,
                RiskLevel = "unknown",
                Notes =
                [
                    note,
                    "云端去AI味校验未完成：本次未执行云端检测，以下只是通用自检，不是具体检测结果：",
                    "1. 有没有否定式对举（不是……而是……）或结论宣告式冒号（核心是：）？",
                    "2. 有没有口头禅式开场、翻译腔（对于……而言、在……的过程中）？",
                    "3. 同一人物是否在少年/青年/银发男子之间轮换称呼？",
                    "4. 有没有连续同骨架句、密集顿号、段间过度衔接或高度同构三连？",
                    "5. 有没有为了「参差」故意拆句？",
                ],
            },
        };

    private static string BuildDeAiReportText(
        DiagnosticReport report,
        Chapter chapter,
        int maxFindings,
        bool fullReport
    )
    {
        var audit = report.DeAi ?? new DeAiAudit();
        var sb = new StringBuilder();
        sb.AppendLine("## 去 AI 味检测（云端 humanizer）");
        sb.AppendLine();
        sb.AppendLine(
            $"**审计章节**: {chapter.Title}（第 {chapter.Order} 章，{chapter.Content?.Length ?? 0} 字）"
        );
        sb.AppendLine();

        if (!audit.CloudAuditAvailable)
        {
            sb.AppendLine($"**⚠️ {audit.Notes.FirstOrDefault() ?? "云端去AI味检测不可用。"}**");
            sb.AppendLine();
            foreach (var note in audit.Notes.Skip(1))
                sb.AppendLine(note);
            sb.AppendLine();
            sb.AppendLine(
                "> 本地创作功能不受影响；恢复云端连接后可重新执行 `diagnose_writing(mode=\"deai\")`。"
            );
            return sb.ToString();
        }

        if (audit.Findings.Count == 0)
        {
            sb.AppendLine("未检测到高风险模板。");
            sb.AppendLine();
            sb.AppendLine($"> {audit.Notes.FirstOrDefault()}");
            return sb.ToString();
        }

        sb.AppendLine(
            $"检测到 {audit.Findings.Count + audit.SuppressedCount} 处高信号问题，按影响先给 {audit.Findings.Count} 处。"
        );
        sb.AppendLine();

        var limit = fullReport ? audit.Findings.Count : Math.Clamp(maxFindings, 1, 10);
        var shown = audit.Findings.Take(limit).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var finding = shown[i];
            var location =
                finding.ParagraphIndex >= 0 ? $"（第{finding.ParagraphIndex + 1}段）" : "";
            var level = string.IsNullOrWhiteSpace(finding.Level)
                ? ""
                : $"[{LevelLabel(finding.Level)}] ";
            sb.AppendLine($"{i + 1}. **{level}{finding.Description}**{location}");
            if (!string.IsNullOrWhiteSpace(finding.Evidence))
                sb.AppendLine($"   原文：「{finding.Evidence}」");
            if (!string.IsNullOrWhiteSpace(finding.Suggestion))
                sb.AppendLine($"   建议：{finding.Suggestion}");
            sb.AppendLine();
        }

        if (audit.SuppressedCount > 0)
            sb.AppendLine($"另有 {audit.SuppressedCount} 处可执行问题未展开（同类或低优先级）。");
        if (audit.NoChangeCount > 0)
            sb.AppendLine(
                $"另有 {audit.NoChangeCount} 处为只报告/指纹/人审/已否决，不自动改，"
                    + "避免把正常写法当 AI 味清理。"
            );
        if (audit.SuppressedCount > 0 || audit.NoChangeCount > 0)
            sb.AppendLine();
        sb.AppendLine($"> {audit.Notes.FirstOrDefault()}");
        return sb.ToString();
    }

    private static bool IsActionableIssue(DiagnosticIssue issue) =>
        (string.IsNullOrEmpty(issue.Level) || issue.Level is "A" or "B" or "F")
        && issue.SubType is not ("dash_density_high" or "semicolon_density_high");

    private static string LevelLabel(string level) =>
        level switch
        {
            "A" => "机械改",
            "B" => "建议改",
            "C" => "只报告",
            "F" => "指纹",
            "H" => "人审",
            "X" => "不改",
            _ => "",
        };

    private static int SeverityRank(string severity) =>
        severity switch
        {
            Severity.High => 3,
            Severity.Medium => 2,
            _ => 1,
        };

    private static string RiskLevel(double score, IReadOnlyList<DiagnosticIssue> issues)
    {
        if (issues.Any(issue => issue.Severity == Severity.High) || score < 70)
            return "high";
        if (issues.Any(issue => issue.Severity == Severity.Medium) || score < 90)
            return "medium";
        if (issues.Count > 0)
            return "low";
        return "none";
    }

    private static string HashContent(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

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
        var message = detail
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

    [Description(
        "模式: quality(综合诊断), deai(去AI味完整报告/复测；默认自动审查，不需要手动调用)"
    )]
    public string Mode { get; init; } = "quality";

    [Description("去AI味报告最多展示几条（默认 3；FullReport=true 时忽略）")]
    public int MaxFindings { get; init; } = 3;

    [Description("去AI味是否展开全部问题（默认 false，只给 1-3 条可执行修复）")]
    public bool FullReport { get; init; }
}
