using Core.Data;
using Core.Shared;
using Xunit;

namespace Core.Tests;

public class AskUserReplyFormatterTests
{
    [Fact]
    public void SessionInternalMessages_HidesNewAndLegacyAskUserReplies()
    {
        Assert.True(SessionInternalMessages.IsInjectedUserMessage("User answers:\n方向: 身世"));
        Assert.True(SessionInternalMessages.IsInjectedUserMessage("**用户回复:**\n方向: 身世"));
        Assert.False(SessionInternalMessages.IsInjectedUserMessage("普通作者输入"));
    }

    [Fact]
    public void Format_SingleSelect_WritesBareValue()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion
            {
                Header = "视角",
                Question = "用第几人称？",
                SelectedAnswers = ["第三人称"],
            },
        ]);

        Assert.Equal("User answers:\n视角: 第三人称", text);
    }

    [Fact]
    public void Format_MultiSelect_WrapsInBrackets()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion
            {
                Header = "方向",
                Question = "选择要保留的线索",
                MultiSelect = true,
                SelectedAnswers = ["身世", "旧案"],
            },
        ]);

        Assert.Equal("User answers:\n方向: [身世, 旧案]", text);
    }

    [Fact]
    public void Format_MultipleGroups_KeepsQuestionOrder()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion
            {
                Header = "方向",
                MultiSelect = true,
                SelectedAnswers = ["身世", "旧案"],
            },
            new PendingQuestion { Header = "视角", SelectedAnswers = ["第三人称"] },
        ]);

        Assert.Equal("User answers:\n方向: [身世, 旧案]\n视角: 第三人称", text);
    }

    [Fact]
    public void Format_CustomSingleAnswer_PreservesComma()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion { Header = "视角", SelectedAnswers = ["我叫「旧钟」，请这样称呼"] },
        ]);

        Assert.Equal("User answers:\n视角: 我叫「旧钟」，请这样称呼", text);
    }

    [Fact]
    public void Format_CancelledAnswer_StaysScalar()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion
            {
                Header = "方向",
                MultiSelect = true,
                SelectedAnswers = ["(已取消)"],
            },
        ]);

        Assert.Equal("User answers:\n方向: (已取消)", text);
    }

    [Fact]
    public void Format_UnansweredGroup_KeepsPosition()
    {
        var text = AskUserReplyFormatter.Format([
            new PendingQuestion { Header = "方向", MultiSelect = true },
            new PendingQuestion { Header = "视角", SelectedAnswers = ["第三人称"] },
        ]);

        Assert.Equal("User answers:\n方向: []\n视角: 第三人称", text);
    }

    [Theory]
    [InlineData("User answers:\n方向: 身世", true)]
    [InlineData("**用户回复:**\n方向: 身世", true)]
    [InlineData("[人工裁决] 就\"方向\"你选择了\"身世\"。", false)]
    [InlineData("普通作者输入", false)]
    public void IsReply_RecognizesNewAndLegacyFormats(string content, bool expected)
    {
        Assert.Equal(expected, AskUserReplyFormatter.IsReply(content));
    }

    [Fact]
    public void VariantXml_EscapesAttributesAndOmitsEmptyOnes()
    {
        var text = VariantXml.Wrap(
            "正文",
            variantId: "var_1",
            label: "结局\"A\"",
            type: "outline",
            target: null
        );

        Assert.Equal(
            "<variant id=\"var_1\" label=\"结局&quot;A&quot;\" type=\"outline\">\n正文\n</variant>",
            text
        );
    }
}
