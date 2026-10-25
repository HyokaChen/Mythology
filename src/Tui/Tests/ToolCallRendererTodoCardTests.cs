using System.Text;
using Core.Tools;
using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ToolCallRenderer 的 todo_write 摘要渲染测试。
/// 验证 list 结果被渲染为一行摘要（📋 任务 x/y · 当前任务），
/// 非 list 动作回退为纯文本（不含摘要前缀）。
/// </summary>
public sealed class ToolCallRendererTodoCardTests
{
    private static readonly PanelColors Dark = PanelThemes.Dark;

    [Fact]
    public void TodoListResult_RendersTaskSummary()
    {
        var argsJson = """{"action":"list"}""";
        var result = "⬜ #1: 建世界观\n🔄 #2: 建核心角色 (creating characters)\n✅ #3: 定情节主线\n";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "todo_write",
            "管理任务",
            "list",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("📋 任务", content);
        Assert.Contains("1/3", content); // 完成 1 / 共 3
        Assert.Contains("建核心角色", content); // 当前 in_progress 任务
    }

    [Fact]
    public void TodoCreateAction_FallsBackToPlainText()
    {
        var argsJson = """{"action":"create","subject":"建世界观"}""";
        var result = "✅ 任务 #1 已创建: 建世界观";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "todo_write",
            "管理任务",
            "create",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        // 非 list 动作不渲染任务摘要前缀
        Assert.DoesNotContain("📋 任务", content);
    }

    [Fact]
    public void EmptyTodoList_RendersEmptySummary()
    {
        var argsJson = """{"action":"list"}""";
        var result = "(无任务)";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "todo_write",
            "管理任务",
            "list",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("暂无任务", content);
    }

    [Fact]
    public void AskUserNewReply_RendersSelectedAndUnselectedOptions()
    {
        var argsJson =
            """{"questions":[{"header":"方向","question":"选择要保留的线索","options":["身世","师门","旧案"],"multiSelect":true}]}""";
        var result = "User answers:\n方向: [身世, 旧案]";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "ask_user",
            "询问用户",
            "",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("选择要保留的线索", content);
        Assert.Contains("● 身世", content);
        Assert.Contains("● 旧案", content);
        Assert.Contains("○ 师门", content);
    }

    [Fact]
    public void AskUserLegacyReply_StillRendersQaCard()
    {
        var argsJson =
            """{"questions":[{"header":"方向","question":"选择要保留的线索","options":["身世","师门","旧案"],"multi_select":true}]}""";
        var result = "用户回复:\n  [方向] 选择要保留的线索\n    → 身世, 旧案";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "ask_user",
            "询问用户",
            "",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("● 身世", content);
        Assert.Contains("○ 师门", content);
    }

    [Fact]
    public void AskUserLegacyAsciiArrow_RendersSelectedOption()
    {
        var argsJson =
            """{"questions":[{"header":"方向","question":"选择要保留的线索","options":["身世","师门","旧案"],"multiSelect":true}]}""";
        var result = "用户回复:\n  [方向] 选择要保留的线索\n    -> 身世";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "ask_user",
            "询问用户",
            "",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("● 身世", content);
        Assert.Contains("○ 师门", content);
    }

    [Fact]
    public void AskUserCancelled_ShowsStatusWithoutCustomLabel()
    {
        var argsJson =
            """{"questions":[{"header":"方向","question":"选择要保留的线索","options":["身世","旧案"]}]}""";
        var result = "User answers:\n方向: (已取消)";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "ask_user",
            "询问用户",
            "",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("→ (已取消)", content);
        Assert.DoesNotContain("(自定义)", content);
    }

    [Fact]
    public void AskUserCustomAnswer_StaysMarkedCustom()
    {
        var argsJson =
            """{"questions":[{"header":"视角","question":"用第几人称？","options":["第一人称","第三人称"]}]}""";
        var result = "User answers:\n视角: 我叫「旧钟」，请这样称呼";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.Generic,
            null,
            "ask_user",
            "询问用户",
            "",
            isError: false,
            result,
            Dark,
            argsJson: argsJson
        );

        var content = ExtractText(item.Content);
        Assert.Contains("我叫「旧钟」，请这样称呼", content);
        Assert.Contains("(自定义)", content);
        Assert.DoesNotContain("→ (已取消)", content);
    }

    /// <summary>递归提取 FlowDocument 全部块中的纯文本（Markup/Paragraph/ContentVisual）。</summary>
    private static string ExtractText(IDocumentFlowContent content)
    {
        var sb = new StringBuilder();
        if (content is FlowDocument doc)
        {
            for (var i = 0; i < doc.BlockCount; i++)
                sb.Append(ExtractVisualText(doc.GetBlock(i).CreateVisual()));
        }
        else if (content is Visual v)
        {
            sb.Append(ExtractVisualText(v));
        }
        return sb.ToString();
    }

    private static string ExtractVisualText(Visual visual)
    {
        var sb = new StringBuilder();
        switch (visual)
        {
            case Markup markup:
                sb.Append(markup.Text);
                break;
            case Paragraph paragraph:
                sb.Append(paragraph.Text);
                break;
            case ContentVisual { Content: not null } contentVisual:
                sb.Append(ExtractVisualText(contentVisual.Content));
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    sb.Append(ExtractVisualText(child));
                break;
        }
        return sb.ToString();
    }
}
