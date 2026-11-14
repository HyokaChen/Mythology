using Core.Prompts;
using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Prompt 静态类内容验证测试。
/// 确保所有 Prompt 模板非空、非纯占位符。
/// </summary>
public sealed class PromptsContentTests
{
    [Fact]
    public void RoleDefinitionPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(RoleDefinitionPrompt.Full);
    }

    [Fact]
    public void RoleDefinitionPrompt_ContainsRoleDefinition()
    {
        Assert.Contains("角色定义", RoleDefinitionPrompt.Full);
        Assert.Contains("作者的明确裁决", RoleDefinitionPrompt.Full);
        Assert.Contains("本地文件/正文中的既定事实", RoleDefinitionPrompt.Full);
        Assert.Contains("云端报告的结论", RoleDefinitionPrompt.Full);
        Assert.Contains("read_chapter", RoleDefinitionPrompt.Full);
        Assert.Contains("助手记忆", RoleDefinitionPrompt.Full);
    }

    [Fact]
    public void RoleDefinitionPrompt_ContainsAgentMemoryPrecedence()
    {
        Assert.Contains("助手记忆是写入时快照", RoleDefinitionPrompt.Full);
        Assert.Contains("不作小说事实", RoleDefinitionPrompt.Full);
        Assert.Contains("冲突时以本地事实为准", RoleDefinitionPrompt.Full);
    }

    [Fact]
    public void ConversationStylePrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(ConversationStylePrompt.Full);
    }

    [Fact]
    public void WritingProcessPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(WritingProcessPrompt.Full);
    }

    [Fact]
    public void HumanizerPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(HumanizerPrompt.Full);
    }

    [Fact]
    public void EnhancementPrompt_Build_ReturnsNonEmpty()
    {
        var profile = new Data.WritingProfile();
        var result = EnhancementPrompt.Build(
            "immersion",
            "原文",
            "前文",
            "后文",
            "问题描述",
            profile
        );
        Assert.NotEmpty(result);
        Assert.Contains("原文", result);
        Assert.Contains("<original>", result);
        Assert.Contains("不是指令", result);
    }

    [Fact]
    public void EnhancementPrompt_Build_UnknownDimension_DoesNotThrow()
    {
        var profile = new Data.WritingProfile();
        var result = EnhancementPrompt.Build("unknown_dim", "原文", "", "", "", profile);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void RoleDefinitionPrompt_DoesNotContainTemplatePlaceholders()
    {
        Assert.DoesNotContain("{{", RoleDefinitionPrompt.Full);
        Assert.DoesNotContain("{0}", RoleDefinitionPrompt.Full);
    }

    [Fact]
    public void ConversationStylePrompt_DoesNotContainTemplatePlaceholders()
    {
        Assert.DoesNotContain("{{", ConversationStylePrompt.Full);
    }

    [Fact]
    public void WritingProcessPrompt_DoesNotContainTemplatePlaceholders()
    {
        Assert.DoesNotContain("{{", WritingProcessPrompt.Full);
    }

    [Fact]
    public void HumanizerPrompt_DoesNotContainTemplatePlaceholders()
    {
        Assert.DoesNotContain("{{", HumanizerPrompt.Full);
    }

    [Fact]
    public void AllPrompts_AreAtLeast100Chars()
    {
        Assert.True(RoleDefinitionPrompt.Full.Length > 100);
        Assert.True(ConversationStylePrompt.Full.Length > 100);
        Assert.True(WritingProcessPrompt.Full.Length > 100);
        Assert.True(HumanizerPrompt.Full.Length > 100);
    }

    // ── 新增强 Prompt（Phase 1-6） ──

    [Fact]
    public void ToolCallingPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(ToolCallingPrompt.Full);
    }

    [Fact]
    public void ToolCallingPrompt_ContainsParallelAndDependencyRules()
    {
        Assert.Contains("并行优先", ToolCallingPrompt.Full);
        Assert.Contains("依赖串行", ToolCallingPrompt.Full);
    }

    [Fact]
    public void ContextUnderstandingPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(ContextUnderstandingPrompt.Full);
    }

    [Fact]
    public void ContextUnderstandingPrompt_ContainsExplorationOrder()
    {
        Assert.Contains("探索顺序", ContextUnderstandingPrompt.Full);
        Assert.Contains("假设禁止", ContextUnderstandingPrompt.Full);
    }

    [Fact]
    public void OutputZonesPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(OutputZonesPrompt.Full);
    }

    [Fact]
    public void OutputZonesPrompt_ContainsZones()
    {
        Assert.Contains("输出分区", OutputZonesPrompt.Full);
        Assert.Contains("内容区", OutputZonesPrompt.Full);
    }

    [Fact]
    public void PlanningLoopPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(PlanningLoopPrompt.Full);
    }

    [Fact]
    public void PlanningLoopPrompt_ContainsTaskTiersAndLoop()
    {
        Assert.Contains("任务分级", PlanningLoopPrompt.Full);
        Assert.Contains("创作循环五步", PlanningLoopPrompt.Full);
    }

    [Fact]
    public void ErrorHandlingPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(ErrorHandlingPrompt.Full);
    }

    [Fact]
    public void ErrorHandlingPrompt_ContainsFailurePaths()
    {
        Assert.Contains("工具失败处理", ErrorHandlingPrompt.Full);
        Assert.Contains("死循环防护", ErrorHandlingPrompt.Full);
    }

    [Fact]
    public void SafetyPolicyPrompt_Full_IsNotEmpty()
    {
        Assert.NotEmpty(SafetyPolicyPrompt.Full);
    }

    [Fact]
    public void SafetyPolicyPrompt_ContainsBoundaries()
    {
        Assert.Contains("创作边界", SafetyPolicyPrompt.Full);
        Assert.Contains("拒绝方式", SafetyPolicyPrompt.Full);
    }

    [Fact]
    public void AllNewPrompts_DoNotContainTemplatePlaceholders()
    {
        Assert.DoesNotContain("{{", ToolCallingPrompt.Full);
        Assert.DoesNotContain("{{", ContextUnderstandingPrompt.Full);
        Assert.DoesNotContain("{{", OutputZonesPrompt.Full);
        Assert.DoesNotContain("{{", PlanningLoopPrompt.Full);
        Assert.DoesNotContain("{{", ErrorHandlingPrompt.Full);
        Assert.DoesNotContain("{{", SafetyPolicyPrompt.Full);
        Assert.DoesNotContain("{0}", ToolCallingPrompt.Full);
        Assert.DoesNotContain("{0}", ContextUnderstandingPrompt.Full);
        Assert.DoesNotContain("{0}", OutputZonesPrompt.Full);
        Assert.DoesNotContain("{0}", PlanningLoopPrompt.Full);
        Assert.DoesNotContain("{0}", ErrorHandlingPrompt.Full);
        Assert.DoesNotContain("{0}", SafetyPolicyPrompt.Full);
    }

    // ── 模式互斥：接续模式的写作规范不得让模型建议 /compact ──

    [Fact]
    public void WritingProcessPrompt_ContinuationSwapsOnlyStageBoundaryRule()
    {
        // compact：上下文由系统自动处理，模型不得主动向用户建议 /compact（否则每轮都刷屏）
        Assert.Contains("阶段边界", WritingProcessPrompt.Full);
        Assert.Contains("系统自动处理", WritingProcessPrompt.Full);
        Assert.DoesNotContain("/compact", WritingProcessPrompt.Full);
        Assert.DoesNotContain("建议压缩", WritingProcessPrompt.Full);
        Assert.DoesNotContain("new_context", WritingProcessPrompt.Full);

        // 接续：直接调用 new_context 换页，且不得提到 /compact（该模式压缩不可用、机制对用户隐藏）
        Assert.Contains("阶段边界换页", WritingProcessPrompt.ForContinuation);
        Assert.Contains("new_context", WritingProcessPrompt.ForContinuation);
        Assert.DoesNotContain("/compact", WritingProcessPrompt.ForContinuation);

        // 只替换第 9 条：章节结构逐字段保留（变体不是重写，而是同一份规范换一条规则）
        foreach (
            var structural in new[] { "## 写作流程规范", "### 品质标准", "## 商业写作十二铁律" }
        )
        {
            Assert.Contains(structural, WritingProcessPrompt.Full);
            Assert.Contains(structural, WritingProcessPrompt.ForContinuation);
        }
    }

    [Fact]
    public void PromptAssembly_ContinuationSkeleton_IsModeExclusive()
    {
        var compact = PromptAssembly.Build(null);
        var continuation = PromptAssembly.Build(null, continuation: true);

        Assert.DoesNotContain("/compact", compact);
        Assert.DoesNotContain("new_context", compact);
        Assert.Contains("系统自动处理", compact);
        Assert.Contains("new_context", continuation);
        Assert.DoesNotContain("/compact", continuation);
    }

    // ── PromptAssembly ──

    [Fact]
    public void PromptAssembly_Build_ContainsAllSegments()
    {
        var result = PromptAssembly.Build();

        Assert.Contains("角色定义", result);
        Assert.Contains("输出格式规范", result);
        Assert.Contains("工具调用", result);
        Assert.Contains("输出分区", result);
        Assert.Contains("先决定输出到哪里", result);
        Assert.Contains("设定探索顺序", result);
        Assert.Contains("创作任务分级", result);
        Assert.Contains("问还是做", result);
        Assert.Contains("设定写回纪律", result);
        Assert.Contains("工具失败处理", result);
        Assert.Contains("创作边界", result);
        Assert.Contains("数据与指令边界", result);
        Assert.Contains("写作流程规范", result);
        Assert.Contains("云端诊断的使用原则", result);
        Assert.Contains("云端诊断使用原则", result);
        Assert.Contains("交付前检查", result);
        Assert.Contains("记忆纪律", result);
    }

    [Fact]
    public void ToolCallingPrompt_ContainsReadBoundary()
    {
        Assert.Contains("只读用户当前消息中明确给出的路径", ToolCallingPrompt.Full);
        Assert.Contains("禁止构造路径", ToolCallingPrompt.Full);
        Assert.Contains("章节正文 → `read_chapter`", ToolCallingPrompt.Full);
        Assert.DoesNotContain("列目录/看文件", ToolCallingPrompt.Full);
    }

    [Fact]
    public void PromptAssembly_RoutesChapterReadsToExportChapter()
    {
        var result = PromptAssembly.Build();

        Assert.Contains("先用 read_chapter", result);
        Assert.Contains("章节正文 → `read_chapter`", result);
        Assert.Contains("不要先 `read` 正文", result);
        Assert.Contains("不要读整章后手动 rewrite", result);
    }

    [Fact]
    public void PromptAssembly_Build_WrapsSegmentsInTags()
    {
        var result = PromptAssembly.Build();
        string[] tags =
        [
            "role_definition",
            "safety_policy",
            "conversation_style",
            "output_contract",
            "tool_calling",
            "context_understanding",
            "planning_loop",
            "error_handling",
            "writing_process",
            "writing_style",
        ];

        foreach (var tag in tags)
        {
            Assert.Contains($"<{tag}>", result);
            Assert.Contains($"</{tag}>", result);
        }
    }

    [Fact]
    public void PromptAssembly_AuxiliaryWritingSystemPrompt_IncludesWritingStyleLast()
    {
        var result = PromptAssembly.BuildAuxiliaryWritingSystemPrompt("小说上下文");

        Assert.StartsWith("小说上下文", result);
        Assert.Contains("<writing_style>", result);
        Assert.Contains("结构预防", result);
        Assert.Contains("翻案腔", result);
        Assert.True(
            result.IndexOf("<writing_style>", StringComparison.Ordinal)
                > result.IndexOf("小说上下文", StringComparison.Ordinal)
        );
        Assert.DoesNotContain("<role_definition>", result);
        Assert.DoesNotContain("HumanizerPrompt", result);

        var styleOnly = PromptAssembly.BuildAuxiliaryWritingSystemPrompt("");
        Assert.StartsWith("<writing_style>", styleOnly);
        Assert.Contains("结构预防", styleOnly);
    }

    [Fact]
    public void PromptAssembly_Build_Exclude_RemovesSegment()
    {
        var result = PromptAssembly.Build(excludeKeys: ["tooling"]);

        Assert.DoesNotContain("<tool_calling>", result);
        Assert.DoesNotContain("## 工具调用：并行优先", result);
        Assert.Contains("角色定义", result);
        Assert.Contains("写作流程规范", result);
    }

    [Fact]
    public void PromptAssembly_SegmentKeys_AreUniqueAndOrdered()
    {
        var keys = PromptAssembly.SegmentKeys;

        Assert.Equal(10, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal("role", keys[0]);
        Assert.Equal("safety", keys[1]);
        Assert.Equal("humanizer", keys[^1]);
    }

    [Fact]
    public void PromptAssembly_ContainsNoInternalDiagnosisJargon()
    {
        var prompt = PromptAssembly.Build();
        foreach (
            var forbidden in new[]
            {
                "status=",
                "repairIntent",
                "human_decision_required",
                "DECISION",
                "A-1",
                "F-1",
                "S-1",
                "story.check",
                "context.retrieve",
                "scene.plan",
                "humanizer",
                "prose_quality",
                "AutoEnhance",
                "units",
                "WritingProfile",
                "scope",
                "ConversationStylePrompt",
                "WritingProcessPrompt",
                "HumanizerPrompt",
                "commercial",
                "literary",
                "A-3",
                "A-4",
                "A-5",
                "beat",
                "Draft",
                "Published",
            }
        )
            Assert.DoesNotContain(forbidden, prompt);

        Assert.Contains("结果：通过", prompt);
        Assert.Contains("结果：需要修改", prompt);
        Assert.Contains("有风险", prompt);
        Assert.Contains("未通过", prompt);
        Assert.Contains("缺少创作目标", prompt);
        Assert.Contains("内容不足", prompt);
        Assert.Contains("需要作者决定", prompt);
        Assert.Contains("直接修改", prompt);
        Assert.Contains("只报告", prompt);
        Assert.Contains("保留", prompt);
        Assert.Contains("自动写作流程", prompt);
        Assert.Contains("批次结束", prompt);
        Assert.Contains("diagnose_writing", prompt);
    }

    [Fact]
    public void PromptAssembly_EstimateTokenCount_UnderBudget()
    {
        // 骨架长度上限：约 18000 字符（汉字≈1 token），超限提示需要瘦身
        // 2026-08-12 由 12000 上调：借鉴 Tura balanced 新增反向思维/验收清单/回归检查/审阅姿态等机制段（约 +1150 字符）
        // 2026-08-30 由 13500 上调：HARNESS-CLOUD 云端诊断链路新增 §8.1 判断矩阵行/§16.3 信号分级/探索终止规则（现实测 13637 字符，+137 超旧限）
        // 2026-11-10 由 14000 上调：新增输出契约/交付路由、数据边界、问还是做、人工裁决、
        // 云端经济性、诊断语义与报告呈现；同时压缩 emoji 与能力表。
        // 若实测仍超 18000，按顺序压缩：emoji 指南 → 能力表 → WritingProcessPrompt 的重复原则 →
        // HumanizerPrompt 词表；不得删除云端经济性/人工裁决/数据边界规则。
        var length = PromptAssembly.EstimateTokenCount();
        Assert.True(length < 18000, $"骨架字符量 {length} 超过 18000");
    }

    [Fact]
    public void ToolCallingPrompt_ContainsCloudEconomy()
    {
        Assert.Contains("云端诊断的使用原则", ToolCallingPrompt.Full);
        Assert.Contains("每章最多一次", ToolCallingPrompt.Full);
        Assert.Contains("最小上下文", ToolCallingPrompt.Full);
        Assert.Contains("最后一项是验收", ToolCallingPrompt.Full);
    }

    [Fact]
    public void WritingProcessPrompt_ContainsCloudDiagnosisPrinciples()
    {
        Assert.Contains("云端诊断使用原则", WritingProcessPrompt.Full);
        Assert.Contains("按章分批", WritingProcessPrompt.Full);
        Assert.Contains("同一章全部", WritingProcessPrompt.Full);
        Assert.Contains("不要每改一句", WritingProcessPrompt.Full);
        Assert.Contains("批次结束", WritingProcessPrompt.Full);
        Assert.Contains("diagnose_writing", WritingProcessPrompt.Full);
        Assert.Contains("不返工", WritingProcessPrompt.Full);
        Assert.Contains("问题/证据/建议", WritingProcessPrompt.Full);
        Assert.Contains("不向用户展示内部规则", WritingProcessPrompt.Full);
        Assert.DoesNotContain("未诊断即交付", WritingProcessPrompt.Full);
    }

    [Fact]
    public void WritingProcessPrompt_ContainsAutomaticHumanizerRules()
    {
        Assert.Contains("去 AI 味自动审查", WritingProcessPrompt.Full);
        Assert.Contains("去AI味", WritingProcessPrompt.Full);
        Assert.Contains("不等用户问", WritingProcessPrompt.Full);
        Assert.Contains("每章最多两次", WritingProcessPrompt.Full);
        Assert.Contains("云端去AI味校验未完成", WritingProcessPrompt.Full);
    }

    [Fact]
    public void EnhancementPrompt_HumanizerGuides_DoNotExposeInternalLevels()
    {
        var profile = new Data.WritingProfile();
        var personification = EnhancementPrompt.Build(
            DimensionKeys.Humanizer,
            "原文",
            "前文",
            "后文",
            "问题描述",
            profile,
            "personification"
        );
        var confirmed = EnhancementPrompt.Build(
            DimensionKeys.Humanizer,
            "原文",
            "前文",
            "后文",
            "问题描述",
            profile,
            "summary_substitution"
        );

        Assert.DoesNotContain("人审", personification);
        Assert.DoesNotContain("X 级", confirmed);
        Assert.Contains("需要作者判断", personification);
        Assert.Contains("已确认不是 AI 味", confirmed);
    }

    [Fact]
    public void HumanizerPrompt_ContainsStructuralPrinciples()
    {
        Assert.Contains("结构预防", HumanizerPrompt.Full);
        Assert.Contains("处理方式", HumanizerPrompt.Full);
        Assert.Contains("翻案腔", HumanizerPrompt.Full);
        Assert.Contains("不是 AI 味，永不改", HumanizerPrompt.Full);
        Assert.Contains("不制造参差", HumanizerPrompt.Full);
    }

    [Fact]
    public void PlanningLoopPrompt_ContainsHumanDecisionRules()
    {
        Assert.Contains("需要作者决定", PlanningLoopPrompt.Full);
        Assert.Contains("人工裁决超时", PlanningLoopPrompt.Full);
    }

    [Fact]
    public void PlanningLoopPrompt_ContainsHarnessGates()
    {
        Assert.Contains("自动写作流程", PlanningLoopPrompt.Full);
        Assert.Contains("开书", PlanningLoopPrompt.Full);
        Assert.Contains("不逐章确认", PlanningLoopPrompt.Full);
        Assert.Contains("不可逆操作", PlanningLoopPrompt.Full);
        Assert.Contains("审阅摘要", PlanningLoopPrompt.Full);
        Assert.DoesNotContain("G0", PlanningLoopPrompt.Full);
        Assert.DoesNotContain("Blocking", PlanningLoopPrompt.Full);
        Assert.DoesNotContain("Canon", PlanningLoopPrompt.Full);
    }

    [Fact]
    public void WritingProcessPrompt_ContainsFailurePolicy()
    {
        Assert.Contains("失败处理", WritingProcessPrompt.Full);
        Assert.Contains("必须修复", WritingProcessPrompt.Full);
        Assert.Contains("修两轮", WritingProcessPrompt.Full);
        Assert.Contains("提交标记", WritingProcessPrompt.Full);
        Assert.Contains("不伪造通过", WritingProcessPrompt.Full);
        Assert.DoesNotContain("Hard fail", WritingProcessPrompt.Full);
        Assert.DoesNotContain("Soft fail", WritingProcessPrompt.Full);
    }

    [Fact]
    public void Prompts_ContainAuthorizationContract_AndDoNotStopOnDecision()
    {
        Assert.Contains("低风险、可回退的场景规划", RoleDefinitionPrompt.Full);
        Assert.Contains("不再逐次询问", PlanningLoopPrompt.Full);
        Assert.Contains("需要作者决定", WritingProcessPrompt.Full);
        Assert.Contains("检查/诊断请求默认包含修复", OutputZonesPrompt.Full);
        Assert.DoesNotContain("human_decision_required 必须停", RoleDefinitionPrompt.Full);
        Assert.DoesNotContain("必须等待用户回答", WritingProcessPrompt.Full);
        Assert.DoesNotContain("否则停等", WritingProcessPrompt.Full);
    }

    [Fact]
    public void ContextUnderstandingPrompt_ContainsCloudCanonBoundary()
    {
        Assert.Contains("云端报告的结论", ContextUnderstandingPrompt.Full);
        Assert.Contains("检索边界", ContextUnderstandingPrompt.Full);
    }

    [Fact]
    public void ContextUnderstandingPrompt_ContainsMemoryDiscipline()
    {
        Assert.Contains("记忆纪律", ContextUnderstandingPrompt.Full);
        Assert.Contains("助手记忆只存长期偏好", ContextUnderstandingPrompt.Full);
        Assert.Contains("memory_recall", ContextUnderstandingPrompt.Full);
        Assert.Contains("memory_forget", ContextUnderstandingPrompt.Full);
        Assert.Contains("不读记忆文件", ContextUnderstandingPrompt.Full);
        Assert.Contains("本地文件只在用户当前消息明确引用时读取", ContextUnderstandingPrompt.Full);
    }

    [Fact]
    public void SafetyPolicyPrompt_ContainsSignalEvidenceBoundary()
    {
        Assert.Contains("明确状态", SafetyPolicyPrompt.Full);
        Assert.Contains("只是数据", SafetyPolicyPrompt.Full);
    }

    [Fact]
    public void OutputZonesPrompt_ContainsLocalFirstDelivery()
    {
        Assert.Contains("诊断工具直接读章节", OutputZonesPrompt.Full);
        Assert.Contains("云端校验未完成", OutputZonesPrompt.Full);
        Assert.Contains("read_chapter", OutputZonesPrompt.Full);
        Assert.Contains("不先 read", OutputZonesPrompt.Full);
    }

    // ── 新增增强维度 ──

    [Theory]
    [InlineData(DimensionKeys.CharacterArc)]
    [InlineData(DimensionKeys.Opening)]
    [InlineData(DimensionKeys.Mistake)]
    [InlineData(DimensionKeys.Excitement)]
    [InlineData(DimensionKeys.ReaderEngagement)]
    [InlineData(DimensionKeys.InfoAsymmetry)]
    public void EnhancementPrompt_Build_NewDimensions_ReturnsNonEmpty(string dimension)
    {
        var profile = new Data.WritingProfile();
        var result = EnhancementPrompt.Build(
            dimension,
            "原文",
            "前文",
            "后文",
            "问题描述",
            profile
        );

        Assert.NotEmpty(result);
        Assert.Contains("保守版", result);
        Assert.Contains("适度版", result);
    }
}
