using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 变体 refinement prompt 构造契约测试（对齐 TUI TuiApp.Input.cs SubmitInput 五段式输出）：
/// 剥离预填前缀、五段式拼装、空段省略。
/// </summary>
public sealed class RefinementPromptTests
{
    private static ConversationViewModel.PendingRefinement MakePending(
        string? explorePrompt = "用 explore 工具生成 2 个剧情变体",
        string? exploreType = "plot",
        string? targetTitle = null
    ) =>
        new(
            new Desktop.Controls.VariantReviewDecision
            {
                Action = Desktop.Controls.VariantReviewAction.Refine,
                VariantId = "v1",
                VariantLabel = "悲剧走向",
                VariantContent = "变体正文内容",
            },
            explorePrompt,
            exploreType,
            targetTitle is null
                ? null
                : new Core.Tools.VariantApplyTarget
                {
                    NovelId = "nv-1",
                    Title = targetTitle,
                    Order = 3,
                }
        );

    [Fact]
    public void WithPrefillPrefix_StripsPrefixAndIncludesAllSections()
    {
        var pending = MakePending();
        var prompt = ConversationViewModel.BuildRefinementPrompt(
            pending,
            "修改「悲剧走向」版本：让结局更克制"
        );

        // 前缀已剥离：用户修改要求段应只含剥离后的文本
        Assert.Contains("===== 用户修改要求 =====", prompt);
        Assert.Contains("让结局更克制", prompt);
        Assert.DoesNotContain("修改「悲剧走向」版本：", prompt);

        // 其余各段标题与内容
        Assert.Contains("用户从 explore 工具的结果中选择了变体「悲剧走向」", prompt);
        Assert.Contains("===== 原始探索指令 =====", prompt);
        Assert.Contains("用 explore 工具生成 2 个剧情变体", prompt);
        Assert.Contains("===== 探索类型 =====", prompt);
        Assert.Contains("plot", prompt);
        Assert.Contains("===== 选中的变体内容 =====", prompt);
        Assert.Contains(
            "<variant id=\"v1\" label=\"悲剧走向\" type=\"plot\">\n变体正文内容\n</variant>",
            prompt
        );
    }

    [Fact]
    public void WithoutPrefix_UsesTextVerbatim()
    {
        var prompt = ConversationViewModel.BuildRefinementPrompt(MakePending(), "结局再克制一点");
        Assert.Contains("===== 用户修改要求 =====", prompt);
        Assert.Contains("结局再克制一点", prompt);
    }

    [Fact]
    public void NullOptionalSections_AreOmitted()
    {
        var prompt = ConversationViewModel.BuildRefinementPrompt(
            MakePending(explorePrompt: null, exploreType: null, targetTitle: null),
            "结局再克制一点"
        );

        Assert.DoesNotContain("===== 原始探索指令 =====", prompt);
        Assert.DoesNotContain("===== 探索类型 =====", prompt);
        Assert.DoesNotContain("===== 应用目标 =====", prompt);
        Assert.Contains("===== 选中的变体内容 =====", prompt);
        Assert.Contains("===== 用户修改要求 =====", prompt);
    }

    [Fact]
    public void ApplyTargetIncluded_WhenPresent()
    {
        var prompt = ConversationViewModel.BuildRefinementPrompt(
            MakePending(targetTitle: "雪夜"),
            "结局再克制一点"
        );
        Assert.Contains("===== 应用目标 =====", prompt);
        Assert.Contains("目标章节: 雪夜 (序号 3)", prompt);
        Assert.Contains("target=\"第3章「雪夜」\"", prompt);
    }
}
