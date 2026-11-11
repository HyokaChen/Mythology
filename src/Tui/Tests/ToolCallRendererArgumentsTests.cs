using Core.Tools;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 工具卡参数详情：头部保留语义摘要，完整参数放进可折叠 JSON 区；
/// 小参数展开，长参数折叠，结果卡和历史恢复都必须带上参数详情。
/// </summary>
public sealed class ToolCallRendererArgumentsTests
{
    private static readonly PanelColors Dark = PanelThemes.Dark;

    [Fact]
    public void RunningToolCard_ShowsExpandedArgumentDetail()
    {
        const string args =
            """{"path":"chapters/ch001.md","edits":[{"oldText":"旧句子","newText":"新句子"}]}""";

        var item = ToolCallRenderer.BuildToolCallItem(
            "edit",
            "ch001.md",
            "action=update",
            isRunning: true,
            isError: false,
            Dark,
            argsJson: args
        );

        var detail = GetArgumentDetail(item);

        Assert.True(detail.IsExpanded);
        var markup = Assert.IsType<Markup>(detail.Content);
        Assert.Contains("\"path\"", markup.Text);
        Assert.Contains("chapters/ch001.md", markup.Text);
        Assert.Contains("\"edits\"", markup.Text);
    }

    [Fact]
    public void LongArgumentDetail_DefaultsToCollapsed()
    {
        var args = $$"""{"path":"chapter.md","text":"{{new string('x', 300)}}"}""";

        var item = ToolCallRenderer.BuildToolCallItem(
            "write_chapter",
            "chapter.md",
            "",
            isRunning: false,
            isError: false,
            Dark,
            argsJson: args
        );

        var detail = GetArgumentDetail(item);
        Assert.False(detail.IsExpanded);
    }

    [Fact]
    public void ResultCard_RetainsArgumentDetail()
    {
        const string args = """{"name":"林月如","description":"刚烈·侠义"}""";

        var item = ToolCallRenderer.BuildToolCallResultItem(
            ToolCategory.EntityCrud,
            "Character",
            "manage_character",
            "林月如",
            "action=create",
            isError: false,
            "✅ 已创建角色 林月如",
            Dark,
            argsJson: args
        );

        var detail = GetArgumentDetail(item);
        var markup = Assert.IsType<Markup>(detail.Content);
        Assert.Contains("\"name\"", markup.Text);
        Assert.Contains("林月如", markup.Text);
    }

    [Fact]
    public void InvalidArguments_AreShownAsRawDetail()
    {
        const string args = """{"path":"chapter.md", broken""";

        var item = ToolCallRenderer.BuildToolCallItem(
            "edit",
            "chapter.md",
            "",
            isRunning: false,
            isError: false,
            Dark,
            argsJson: args
        );

        var detail = GetArgumentDetail(item);
        var markup = Assert.IsType<Markup>(detail.Content);
        Assert.Contains("broken", markup.Text);
    }

    private static Collapsible GetArgumentDetail(DocumentFlowItem item)
    {
        var doc = Assert.IsType<FlowDocument>(item.Content);
        return Assert.IsType<Collapsible>(doc.GetBlock(1).CreateVisual());
    }
}
