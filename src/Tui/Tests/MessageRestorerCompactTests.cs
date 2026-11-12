using Core.Data;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using Xunit;
using ChatMessage = LlmTornado.Chat.ChatMessage;
using CoreChatMessage = Core.Data.ChatMessage;

namespace Tui.Tests;

/// <summary>
/// compact 摘要历史恢复必须走短系统行：整段摘要如果落入 Unknown 的
/// interrupt 分支，会变成 DocumentFlow 里一个超大文本块，滚动经过时
/// 极易在分片边界失去锚点。
/// </summary>
public sealed class MessageRestorerCompactTests
{
    [Fact]
    public void DisplayMapping_KeepsCompactPreviewShort_ButContextMappingKeepsFullSummary()
    {
        var full =
            "[上下文摘要 b1]\n## 压缩概览\n压缩前 1234 条消息，保留最近 20 条。\n"
            + new string('文', 4000);
        // 旧会话可能把 compact 记为 User；显示层必须统一成系统行，上下文层保留原角色/全文。
        var compact = new CoreChatMessage
        {
            Role = MessageRole.User,
            EventType = SessionEventType.Compact,
            Content = full,
        };

        var display = TuiApp.MapDbMessageToChatMessage(compact, forDisplay: true);
        var context = TuiApp.MapDbMessageToChatMessage(compact, forDisplay: false);

        Assert.Equal(ChatMessageRoles.Unknown, display.Role);
        Assert.StartsWith(SessionInternalMessages.CompactSummaryPrefix, display.Content);
        Assert.True(display.Content!.Length < 200, $"预览仍过长：{display.Content.Length}");
        Assert.Contains("压缩概览", display.Content, StringComparison.Ordinal);
        Assert.Equal(ChatMessageRoles.User, context.Role);
        Assert.Equal(full, context.Content);
    }

    [Fact]
    public void CompactPreview_IsRestoredAsOneShortSystemBlock_NotInterrupt()
    {
        var preview = SessionInternalMessages.FormatCompactSummaryPreview(
            "[上下文摘要 b1]\n## 压缩概览\n" + new string('文', 4000)
        );
        var flow = new DocumentFlow();
        var restorer = new MessageRestorer(flow, new ToolRegistry(), PanelThemes.Dark);

        restorer.Restore([new ChatMessage { Role = ChatMessageRoles.Unknown, Content = preview }]);

        var item = Assert.Single(flow.Items);
        var doc = Assert.IsType<FlowDocument>(item.Content);
        var block = Assert.IsType<TextBlock>(doc.GetBlock(0).CreateVisual());
        Assert.StartsWith(SessionInternalMessages.CompactSummaryPrefix, block.Text);
        Assert.True(block.Text.Length < 200, $"compact 气泡仍包含整段摘要：{block.Text.Length}");
    }

    [Fact]
    public void RuntimeCompactAsUserMessage_IsStillRenderedAsShortSystemBlock()
    {
        // 主题重建直接从 _agent.State.Messages 恢复，compact 是 User 角色；
        // 不能在 User 分支铺出整段摘要或 "👤" 气泡。
        var full = "[上下文摘要 b1]\n## 压缩概览\n" + new string('文', 4000);
        var flow = new DocumentFlow();
        var restorer = new MessageRestorer(flow, new ToolRegistry(), PanelThemes.Dark);

        restorer.Restore([new ChatMessage { Role = ChatMessageRoles.User, Content = full }]);

        var item = Assert.Single(flow.Items);
        var doc = Assert.IsType<FlowDocument>(item.Content);
        var block = Assert.IsType<TextBlock>(doc.GetBlock(0).CreateVisual());
        Assert.StartsWith(SessionInternalMessages.CompactSummaryPrefix, block.Text);
        Assert.DoesNotContain("👤", block.Text, StringComparison.Ordinal);
        Assert.True(block.Text.Length < 200, $"compact 气泡仍包含整段摘要：{block.Text.Length}");
    }
}
