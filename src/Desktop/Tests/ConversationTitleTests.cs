using Core.Data;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 头部会话标题回归锁：新建会话（尚无 MANIFEST）必须显示「未命名对话」，
/// 不能沿用上一个会话的标题；已有内容的会话在索引暂时读不到时保留原标题。
/// </summary>
public sealed class ConversationTitleTests
{
    [Fact]
    public void NewUnsavedConversation_ShowsUntitled()
    {
        Assert.Equal(
            MainWindowViewModel.UntitledConversationTitle,
            MainWindowViewModel.ResolveConversationTitle(null, hasMessages: false)
        );
    }

    [Fact]
    public void ExistingSession_ShowsItsTitle()
    {
        var session = new AgentSession { Title = "修正下 分卷[ID:0a009638c58e]" };

        Assert.Equal(
            session.Title,
            MainWindowViewModel.ResolveConversationTitle(session, hasMessages: false)
        );
    }

    [Fact]
    public void ExistingSessionWithoutTitle_ShowsUntitled()
    {
        var session = new AgentSession { Title = "" };

        Assert.Equal(
            MainWindowViewModel.UntitledConversationTitle,
            MainWindowViewModel.ResolveConversationTitle(session, hasMessages: true)
        );
    }

    [Fact]
    public void LoadedConversation_WithMissingIndex_KeepsCurrentTitle()
    {
        Assert.Null(MainWindowViewModel.ResolveConversationTitle(null, hasMessages: true));
    }
}
