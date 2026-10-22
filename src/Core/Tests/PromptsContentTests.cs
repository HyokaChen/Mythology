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
        // compact：建议用户执行 /compact
        Assert.Contains("阶段边界压缩", WritingProcessPrompt.Full);
        Assert.Contains("/compact", WritingProcessPrompt.Full);
        Assert.DoesNotContain("new_context", WritingProcessPrompt.Full);

        // 接续：直接调用 new_context 换页，且不得提到 /compact（该模式压缩不可用、机制对用户隐藏）
        Assert.Contains("阶段边界换页", WritingProcessPrompt.ForContinuation);
        Assert.Contains("new_context", WritingProcessPrompt.ForContinuation);
        Assert.DoesNotContain("/compact", WritingProcessPrompt.ForContinuation);

        // 只替换第 16 条：章节结构逐字段保留（变体不是重写，而是同一份规范换一条规则）
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

        Assert.Contains("/compact", compact);
        Assert.DoesNotContain("new_context", compact);
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
        Assert.Contains("上下文理解", result);
        Assert.Contains("创作任务分级", result);
        Assert.Contains("工具失败处理", result);
        Assert.Contains("创作边界", result);
        Assert.Contains("写作流程规范", result);
        Assert.Contains("交付前检查", result);
    }

    [Fact]
    public void PromptAssembly_Build_Exclude_RemovesSegment()
    {
        var result = PromptAssembly.Build(excludeKeys: ["tooling"]);

        Assert.DoesNotContain("工具调用", result);
        Assert.Contains("角色定义", result);
        Assert.Contains("写作流程规范", result);
    }

    [Fact]
    public void PromptAssembly_SegmentKeys_AreUniqueAndOrdered()
    {
        var keys = PromptAssembly.SegmentKeys;

        Assert.Equal(9, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal("role", keys[0]);
        Assert.Equal("humanizer", keys[^1]);
    }

    [Fact]
    public void PromptAssembly_EstimateTokenCount_UnderBudget()
    {
        // 骨架长度上限：约 14000 字符（汉字≈1 token），超限提示需要瘦身
        // 2026-08-12 由 12000 上调：借鉴 Tura balanced 新增反向思维/验收清单/回归检查/审阅姿态等机制段（约 +1150 字符）
        // 2026-08-30 由 13500 上调：HARNESS-CLOUD 云端诊断链路新增 §8.1 判断矩阵行/§16.3 信号分级/探索终止规则（现实测 13637 字符，+137 超旧限）
        Assert.True(PromptAssembly.EstimateTokenCount() < 14000);
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
