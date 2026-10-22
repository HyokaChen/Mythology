using Core.Agent;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Token 估算测试（方案 A+B）：
/// A — 中文感知粗估（CJK 1 token/字，ASCII 4 字符/token），修正 chars/4 对中文的 4 倍低估。
/// B — 锚点优先（真实 usage 锚点 + 尾部估算），对齐 pi / Claude Code 混合策略。
/// </summary>
public sealed class TokenEstimationTests
{
    // ── A：中文感知估算精度 ──

    [Fact]
    public void EstimateTextTokens_PureCjk_OneTokenPerChar()
    {
        // P2：新增的单段文本估算入口（EstimateSingleMessageTokens 内部复用）
        Assert.Equal(100, ConversationLoop.EstimateTextTokens(new string('中', 100)));
        Assert.Equal(25, ConversationLoop.EstimateTextTokens(new string('a', 100)));
        Assert.Equal(0, ConversationLoop.EstimateTextTokens(null));
        Assert.Equal(0, ConversationLoop.EstimateTextTokens(""));
    }

    [Fact]
    public void PureCjk_CountsOneTokenPerChar()
    {
        // 100 个中文字符 ≈ 100 token（chars/4 会低估为 25）
        var text = new string('中', 100);
        var msg = new ChatMessage { Role = ChatMessageRoles.User, Content = text };

        Assert.Equal(100, ConversationLoop.EstimateSingleMessageTokens(msg));
    }

    [Fact]
    public void PureAscii_CountsFourCharsPerToken()
    {
        var msg = new ChatMessage { Role = ChatMessageRoles.User, Content = new string('a', 100) };

        Assert.Equal(25, ConversationLoop.EstimateSingleMessageTokens(msg));
    }

    [Fact]
    public void Mixed_CombinesCjkAndAscii()
    {
        var msg = new ChatMessage
        {
            Role = ChatMessageRoles.User,
            Content = new string('中', 50) + new string('a', 100),
        };

        // 50 CJK + 25 ASCII = 75
        Assert.Equal(75, ConversationLoop.EstimateSingleMessageTokens(msg));
    }

    [Fact]
    public void Whitespace_IsNotCounted()
    {
        var msg = new ChatMessage { Role = ChatMessageRoles.User, Content = "  中 中 \n\t  " };

        Assert.Equal(2, ConversationLoop.EstimateSingleMessageTokens(msg));
    }

    [Fact]
    public void ReasoningContent_IsCounted()
    {
        var msg = new ChatMessage
        {
            Role = ChatMessageRoles.Assistant,
            Content = "答案",
            ReasoningContent = new string('思', 30),
        };

        Assert.Equal(32, ConversationLoop.EstimateSingleMessageTokens(msg));
    }

    [Fact]
    public void ToolCallArguments_AreCounted()
    {
        var msg = new ChatMessage
        {
            Role = ChatMessageRoles.Assistant,
            Content = "",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_1",
                    Type = "function",
                    FunctionCall = new FunctionCall
                    {
                        Name = "recall_character",
                        Arguments = """{"name":"林晚"}""",
                    },
                },
            ],
        };

        // JSON 参数计入：{"name":"林晚"} 为 CJK/ASCII 混合，精确值由实现保证 > 0
        Assert.True(ConversationLoop.EstimateSingleMessageTokens(msg) > 0);
    }

    // ── B：锚点优先（真实 usage 锚点 + 尾部估算） ──

    [Fact]
    public void WithAnchor_UsesAnchorPlusTrailing()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatMessageRoles.User, Content = new string('中', 100) }, // 锚点之前：被精确覆盖
            new()
            {
                Role = ChatMessageRoles.Assistant,
                Content = "响应",
                Tokens = 500, // 锚点：该轮完整上下文 = 500
            },
            new() { Role = ChatMessageRoles.User, Content = new string('中', 50) }, // 尾部：估算 50
        };

        Assert.Equal(500 + 50, ConversationLoop.EstimateMessagesTokens(messages));
    }

    [Fact]
    public void MultipleAnchors_UsesLatestOne()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatMessageRoles.Assistant,
                Content = "旧",
                Tokens = 200, // 旧锚点：不应使用
            },
            new() { Role = ChatMessageRoles.User, Content = new string('中', 30) },
            new()
            {
                Role = ChatMessageRoles.Assistant,
                Content = "新",
                Tokens = 800, // 最新锚点
            },
            new() { Role = ChatMessageRoles.User, Content = new string('中', 10) },
        };

        Assert.Equal(800 + 10, ConversationLoop.EstimateMessagesTokens(messages));
    }

    [Fact]
    public void WithoutAnchor_EstimatesEverything()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatMessageRoles.User, Content = new string('中', 100) },
            new() { Role = ChatMessageRoles.Assistant, Content = new string('a', 40) },
        };

        Assert.Equal(100 + 10, ConversationLoop.EstimateMessagesTokens(messages));
    }

    [Fact]
    public void EmptyList_ReturnsZero()
    {
        Assert.Equal(0, ConversationLoop.EstimateMessagesTokens([]));
    }
}
