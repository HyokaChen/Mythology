using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Prompts;
using Core.Shared;
using Core.Tools;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Vendors.Anthropic;
using LlmTornado.Code;

namespace Plugins.WritingTools;

/// <summary>
/// 增强位置 — 定位到具体段落的问题点。
/// </summary>
public sealed record EnhancementLocation
{
    /// <summary>章节 ID</summary>
    public string ChapterId { get; init; } = "";

    /// <summary>段落索引</summary>
    public int ParagraphIndex { get; init; }

    /// <summary>原始文本</summary>
    public string OriginalText { get; init; } = "";

    /// <summary>前文上下文（前一段）</summary>
    public string BeforeContext { get; init; } = "";

    /// <summary>后文上下文（后一段）</summary>
    public string AfterContext { get; init; } = "";

    /// <summary>维度名称</summary>
    public string Dimension { get; init; } = "";

    /// <summary>问题描述</summary>
    public string IssueDescription { get; init; } = "";

    /// <summary>章节标题</summary>
    public string ChapterTitle { get; init; } = "";

    /// <summary>章节序号</summary>
    public int ChapterOrder { get; init; }
}

/// <summary>
/// 增强候选 — LLM 生成的改写变体。
/// </summary>
public sealed record EnhancementCandidate
{
    /// <summary>候选 ID</summary>
    public string Id { get; init; } = "";

    /// <summary>标签（保守版/适度版）</summary>
    public string Label { get; init; } = "";

    /// <summary>改写内容</summary>
    public string Content { get; init; } = "";

    /// <summary>说明</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// 增强引擎 — 4 阶段闭环：诊断→定位→生成→应用。
/// CHANGE_07：诊断全部走云端（IFictionHarnessClient），本地保留定位/生成/写回。
/// 云端不可用时返回可读错误，不伪造分数，不阻塞本地创作。
/// </summary>
public sealed class EnhancementEngine
{
    private readonly INovelStore _store;
    private readonly Agent _agent;
    private readonly EnhancementSnapshot _snapshot;
    private readonly IFictionHarnessClient? _cloudHarness;

    /// <summary>单章最大自动增强数量（安全边界）</summary>
    public const int MaxAutoEnhancementsPerChapter = 3;

    public EnhancementEngine(
        INovelStore store,
        Agent agent,
        IFictionHarnessClient? cloudHarness = null
    )
        : this(store, agent, new EnhancementSnapshot(), cloudHarness) { }

    /// <summary>
    /// CHANGE_07：可注入云端 Harness 客户端（必填）。
    /// 诊断全部走云端，无本地回退。云端不可用时返回可读错误。
    /// </summary>
    public EnhancementEngine(
        INovelStore store,
        Agent agent,
        EnhancementSnapshot snapshot,
        IFictionHarnessClient? cloudHarness = null
    )
    {
        _store = store;
        _agent = agent;
        _snapshot = snapshot;
        _cloudHarness = cloudHarness;
    }

    /// <summary>
    /// 阶段 1: 诊断 — 对指定维度进行诊断。
    /// 全部走云端（fiction.story.check），云端不可用时返回空报告+可读错误。
    /// </summary>
    public async Task<DiagnosticReport> DiagnoseAsync(
        string novelId,
        string chapterId,
        string dimension,
        CancellationToken ct
    )
    {
        var novel = await _store.GetNovel(novelId, ct);
        var chapters = (await _store.GetChapters(novelId, ct))
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .ToList();

        if (_cloudHarness is null)
        {
            return new DiagnosticReport
            {
                Depth = DiagnosticDepth.Standard,
                OverallScore = 0,
                Issues =
                [
                    new DiagnosticIssue
                    {
                        Dimension = "云端",
                        Description = "云端诊断不可用：未配置 API Key",
                        Severity = Severity.High,
                    },
                ],
            };
        }
        var chapter = chapters.FirstOrDefault(c => c.Id == chapterId);

        if (novel is null || chapter is null)
            return new DiagnosticReport();

        var profile = WritingProfile.FromNovel(novel);
        var request = new HarnessRequest
        {
            Capability = "fiction.story.check",
            Depth = "standard",
            Locale = "zh-CN",
            Chapters =
            [
                new ChapterInput(
                    chapter.Id,
                    chapter.Order,
                    chapter.Title,
                    "target",
                    chapter.Content
                ),
            ],
            Context = HarnessContextBuilder.Build(novel, profile.Genre),
            Profile = WritingProfileInput.FromProfile(profile),
        };

        try
        {
            var result = await _cloudHarness.ExecuteAsync(request, $"diagnose-{chapter.Id}", ct);
            if (result.Result is not JsonElement element)
                return new DiagnosticReport
                {
                    Depth = DiagnosticDepth.Standard,
                    OverallScore = 0,
                    Issues = [],
                };

            var report = StoryCheckResultMapper.Map(element, DiagnosticDepth.Standard);
            return FilterToDimension(report, dimension);
        }
        catch (CloudApiException ex)
        {
            // 云端不可用 — 返回可读错误报告，不抛异常
            return new DiagnosticReport
            {
                Depth = DiagnosticDepth.Standard,
                OverallScore = 0,
                Issues =
                [
                    new DiagnosticIssue
                    {
                        Dimension = "云端",
                        Description = $"云端诊断不可用：{ex.Message}",
                        Severity = Severity.High,
                    },
                ],
            };
        }
        catch (HttpRequestException ex)
        {
            return new DiagnosticReport
            {
                Depth = DiagnosticDepth.Standard,
                OverallScore = 0,
                Issues =
                [
                    new DiagnosticIssue
                    {
                        Dimension = "云端",
                        Description = $"网络错误：{ex.Message}",
                        Severity = Severity.High,
                    },
                ],
            };
        }
    }

    private static DiagnosticReport FilterToDimension(DiagnosticReport report, string dimension)
    {
        var dimScore = report.DimensionScores.GetValueOrDefault(dimension, 0);
        var dimIssues = report.Issues.Where(i => i.Dimension == dimension).ToList();
        var dimStrengths = report.Strengths.ToList();

        return report with
        {
            DimensionScores = new Dictionary<string, double> { [dimension] = dimScore },
            Issues = dimIssues,
            Strengths = dimStrengths,
        };
    }

    /// <summary>
    /// 阶段 2: 定位 — 从诊断报告中提取增强位置。
    /// </summary>
    public List<EnhancementLocation> Locate(DiagnosticReport report, string chapterContent)
    {
        var paragraphs = TextHelper.SplitParagraphs(chapterContent);
        var locations = new List<EnhancementLocation>();

        foreach (
            var issue in report.Issues.Where(i => i.Severity is Severity.High or Severity.Medium)
        )
        {
            var pIdx =
                issue.ParagraphIndex >= 0 && issue.ParagraphIndex < paragraphs.Count
                    ? issue.ParagraphIndex
                    : 0;

            var originalText = paragraphs[pIdx];
            var beforeContext = pIdx > 0 ? paragraphs[pIdx - 1] : "";
            var afterContext = pIdx < paragraphs.Count - 1 ? paragraphs[pIdx + 1] : "";

            locations.Add(
                new EnhancementLocation
                {
                    ChapterId = issue.ChapterId,
                    ParagraphIndex = pIdx,
                    OriginalText = originalText,
                    BeforeContext = beforeContext,
                    AfterContext = afterContext,
                    Dimension = issue.Dimension,
                    IssueDescription = issue.Description,
                }
            );
        }

        // 最多 3 个位置（避免过度增强）
        return locations.Take(3).ToList();
    }

    /// <summary>
    /// 阶段 3: 生成 — 对每个位置生成 2 个变体（保守版+适度版）。
    /// </summary>
    public async Task<List<EnhancementCandidate>> GenerateAsync(
        EnhancementLocation location,
        WritingProfile profile,
        CancellationToken ct
    )
    {
        var api = _agent.TornadoApi;
        if (api is null)
            return [];

        var state = _agent.State;
        var modelName = state.ModelName;
        var systemPrompt = state.SystemPrompt;

        var prompt = EnhancementPrompt.Build(
            location.Dimension,
            location.OriginalText,
            location.BeforeContext,
            location.AfterContext,
            location.IssueDescription,
            profile
        );

        var conversation = CreateEnhancementConversation(api, modelName, systemPrompt);
        conversation.AddUserMessage(prompt);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CloudTimings.AuxiliaryLlmTimeout);
            var sb = new StringBuilder();
            await foreach (
                var chunk in conversation.StreamResponseEnumerable(token: timeoutCts.Token)
            )
                sb.Append(chunk);
            var response = sb.ToString();
            return string.IsNullOrWhiteSpace(response)
                ? []
                : ParseVariants(response, location.Dimension);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return []; // 超时：无候选，调用方已有空列表路径
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 阶段 4: 应用 — 将增强候选转换为 ExploreDetails 供 TUI 选择。
    /// </summary>
    public AgentToolResult<ExploreDetails> Apply(
        List<EnhancementCandidate> candidates,
        EnhancementLocation location,
        string novelId
    )
    {
        var variants = candidates
            .Select(c => new VariantResult
            {
                Id = c.Id,
                Label = c.Label,
                Content = c.Content,
                Description = c.Description,
            })
            .ToList();

        var applyTarget = new VariantApplyTarget
        {
            NovelId = novelId,
            ChapterId = location.ChapterId,
            Title = location.ChapterTitle,
            Order = location.ChapterOrder,
        };

        var summary = new StringBuilder();
        summary.AppendLine($"## {location.Dimension}增强");
        summary.AppendLine();
        summary.AppendLine($"**问题**: {location.IssueDescription}");
        summary.AppendLine($"**位置**: 第{location.ParagraphIndex + 1}段");
        summary.AppendLine();
        summary.AppendLine("已生成以下增强变体，请选择：");
        summary.AppendLine();

        for (var i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            var preview = v.Content.Truncate(150);
            summary.AppendLine($"**{i + 1}. {v.Label}**");
            summary.AppendLine(preview);
            summary.AppendLine();
        }

        return new AgentToolResult<ExploreDetails>
        {
            Content = [new TextContent(summary.ToString())],
            Details = new ExploreDetails
            {
                VariantSetId = Guid.NewGuid().ToString("N")[..12],
                ExploreType = "enhancement",
                Prompt = location.IssueDescription,
                Variants = variants,
                ApplyTarget = applyTarget,
            },
            Terminate = true,
        };
    }

    /// <summary>
    /// 全自动增强路径 — 诊断→定位→生成→自动选择→直接写入。
    /// 返回增强摘要，不暂停 Loop。
    /// </summary>
    public async Task<string> AutoEnhanceAsync(
        string novelId,
        string chapterId,
        string dimension,
        WritingProfile profile,
        CancellationToken ct
    )
    {
        var chapters = await _store.GetChapters(novelId, ct);
        var chapter = chapters.FirstOrDefault(c => c.Id == chapterId);
        if (chapter is null)
            return "章节不存在";

        var originalContent = chapter.Content;

        // 阶段 1: 诊断
        var report = await DiagnoseAsync(novelId, chapterId, dimension, ct);

        // 云端不可用时直接返回错误
        var cloudIssue = report.Issues.FirstOrDefault(i => i.Dimension == "云端");
        if (cloudIssue is not null)
            return $"⚠️ {cloudIssue.Description}";

        if (report.Issues.Count == 0)
            return $"{dimension}评分良好，无需增强";

        // 阶段 2: 定位
        var locations = Locate(report, originalContent);
        if (locations.Count == 0)
            return $"{dimension}无高严重度问题";

        var targetLocations = locations.Take(MaxAutoEnhancementsPerChapter).ToList();

        _snapshot.Save(novelId, chapterId, originalContent);

        var enhanced = new List<string>();

        foreach (var location in targetLocations)
        {
            ct.ThrowIfCancellationRequested();

            var candidates = await GenerateAsync(location, profile, ct);
            if (candidates.Count == 0)
                continue;

            var selected = AutoSelectBest.Select(candidates, profile, location.Dimension);
            if (selected is null)
                continue;

            var paragraphs = TextHelper.SplitParagraphs(originalContent);
            if (location.ParagraphIndex >= 0 && location.ParagraphIndex < paragraphs.Count)
            {
                paragraphs[location.ParagraphIndex] = selected.Content;
                originalContent = string.Join("\n\n", paragraphs);
                enhanced.Add($"第{location.ParagraphIndex + 1}段（{selected.Label}）");
            }
        }

        if (enhanced.Count == 0)
        {
            return dimension == DimensionKeys.Humanizer
                ? "检测到机器化写作风险，但叙述/台词类风险需人工确认。仅协作残留可自动清理。"
                : "自动增强未产生有效改写";
        }

        chapter.Content = originalContent;
        await _store.SaveChapter(chapter, ct);

        var prefix = dimension == DimensionKeys.Humanizer ? "🤖 已清理协作残留" : "已自动增强";
        return $"{prefix} {enhanced.Count} 处：{string.Join("、", enhanced)}。可通过 Steer \"撤回\" 还原。";
    }

    /// <summary>
    /// 撤回自动增强 — 恢复到快照版本。
    /// </summary>
    public async Task<string> RollbackAsync(string novelId, string chapterId)
    {
        var chapters = await _store.GetChapters(novelId);
        var chapter = chapters.FirstOrDefault(c => c.Id == chapterId);
        if (chapter is null)
            return "章节不存在";

        var content = _snapshot.Rollback(novelId, chapterId);
        if (content is null)
            return "无快照可撤回";

        chapter.Content = content;
        await _store.SaveChapter(chapter);
        return "已撤回增强，恢复到原始版本";
    }

    // ── 辅助方法 ──

    private static Conversation CreateEnhancementConversation(
        TornadoApi api,
        string modelName,
        string systemPrompt
    )
    {
        var request = new ChatRequest
        {
            Model = modelName,
            ReasoningFormat = ChatReasoningFormats.Parsed,
            ReasoningEffort = ChatReasoningEfforts.Medium,
            VendorExtensions = new ChatRequestVendorExtensions(
                new ChatRequestVendorAnthropicExtensions
                {
                    Thinking = new AnthropicThinkingSettings
                    {
                        BudgetTokens = 4096,
                        Enabled = true,
                    },
                }
            ),
        };

        var conversation = api.Chat.CreateConversation(request);
        conversation.AddSystemMessage(
            string.IsNullOrEmpty(systemPrompt)
                ? "你是一个小说写作增强助手。请根据指定维度的问题，生成改写变体。"
                : systemPrompt
        );

        return conversation;
    }

    /// <summary>
    /// 解析 LLM 输出为保守版和适度版两个变体。
    /// </summary>
    private static List<EnhancementCandidate> ParseVariants(string response, string dimension)
    {
        var candidates = new List<EnhancementCandidate>();

        const string conservativeMarker = "【保守版】";
        const string moderateMarker = "【适度版】";

        var conservativeIdx = response.IndexOf(conservativeMarker, StringComparison.Ordinal);
        var moderateIdx = response.IndexOf(moderateMarker, StringComparison.Ordinal);

        if (conservativeIdx >= 0 && moderateIdx >= 0)
        {
            var conservativeContent =
                moderateIdx > conservativeIdx
                    ? response[(conservativeIdx + conservativeMarker.Length)..moderateIdx].Trim()
                    : response[(conservativeIdx + conservativeMarker.Length)..].Trim();

            var moderateContent =
                moderateIdx > conservativeIdx
                    ? response[(moderateIdx + moderateMarker.Length)..].Trim()
                    : response[(moderateIdx + moderateMarker.Length)..conservativeIdx].Trim();

            candidates.Add(
                new EnhancementCandidate
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Label = "保守版",
                    Content = conservativeContent,
                    Description = $"最小改动，仅修复{dimension}问题",
                }
            );
            candidates.Add(
                new EnhancementCandidate
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Label = "适度版",
                    Content = moderateContent,
                    Description = $"适度增强{dimension}，提升整体效果",
                }
            );
        }
        else
        {
            candidates.Add(
                new EnhancementCandidate
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Label = "增强版",
                    Content = response.Trim(),
                    Description = $"{dimension}增强",
                }
            );
        }

        return candidates;
    }
}
