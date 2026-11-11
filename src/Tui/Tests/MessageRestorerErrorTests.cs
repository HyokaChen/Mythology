using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

public sealed class MessageRestorerErrorTests
{
    [Fact]
    public void ErrorEvent_IsRestoredAsReadableWarning_NotRawJson()
    {
        var flow = new DocumentFlow();
        var restorer = new MessageRestorer(flow, new ToolRegistry(), PanelThemes.Dark);
        restorer.Restore([
            new ChatMessage
            {
                Role = ChatMessageRoles.Unknown,
                Content =
                    """❌ 429 {"error":{"code":"","message":"You've reached the API rate limit for free users.","type":"AgnesAI_error"}}""",
            },
        ]);

        var item = Assert.Single(flow.Items);
        var doc = Assert.IsType<FlowDocument>(item.Content);
        var block = Assert.IsType<TextBlock>(doc.GetBlock(0).CreateVisual());
        Assert.Equal(
            "❗ 调用出错：HTTP 429 · You've reached the API rate limit for free users.",
            block.Text
        );
        Assert.DoesNotContain("{\"error\"", block.Text, StringComparison.Ordinal);
    }
}
