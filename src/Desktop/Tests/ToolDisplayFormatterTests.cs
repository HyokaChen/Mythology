using System.Text.Json;
using Core.Tools;
using Desktop.Services;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 工具展示格式化：对话里只出现友好名称与语义摘要，原始 JSON 留在详情里。
/// </summary>
public sealed class ToolDisplayFormatterTests
{
    private sealed class StubTool(string name, string label) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => "";
        public override JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult());
    }

    private static ToolDisplayFormatter CreateFormatter()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("manage_character", "角色管理"), "test");
        return new ToolDisplayFormatter(registry);
    }

    [Fact]
    public void Label_UsesRegisteredChineseLabel_AndFallsBackToRawName()
    {
        var formatter = CreateFormatter();

        Assert.Equal("角色管理", formatter.Label("manage_character"));
        Assert.Equal("unknown_tool", formatter.Label("unknown_tool"));
        Assert.Equal("", formatter.Label(null));
    }

    [Fact]
    public void Label_DoesNotCacheMiss_BeforeToolRegistration()
    {
        var registry = new ToolRegistry();
        var formatter = new ToolDisplayFormatter(registry);

        Assert.Equal("late_tool", formatter.Label("late_tool"));

        registry.Register(new StubTool("late_tool", "后注册工具"), "test");

        Assert.Equal("后注册工具", formatter.Label("late_tool"));
    }

    [Theory]
    [InlineData(
        """{"novelId":"n1","path":"chapters/ch001.md","content":"正文"}""",
        "chapters/ch001.md"
    )]
    [InlineData("""{"query":"林月如","limit":5}""", "林月如")]
    [InlineData("""{"characters":[1,2,3]}""", "characters=3 项")]
    [InlineData("""{"action":"list"}""", "action=list")]
    public void Arguments_PrefersSemanticValue_WithoutJsonPunctuation(string json, string expected)
    {
        var summary = ToolDisplayFormatter.Arguments(json);

        Assert.Equal(expected, summary);
        Assert.DoesNotContain("{", summary);
        Assert.DoesNotContain("\"", summary);
    }

    [Theory]
    [InlineData("""{"success":true,"message":"已创建角色"}""", "已创建角色")]
    [InlineData("""{"success":true,"characters":[{"name":"甲"},{"name":"乙"}]}""", "2 项")]
    [InlineData("""{"success":true,"count":7}""", "7 项")]
    [InlineData("""{"success":true}""", "完成")]
    [InlineData("""{"success":false}""", "失败")]
    [InlineData("""{"error":{"message":"rate limited"}}""", "错误：rate limited")]
    [InlineData("第一章正文\n第二行不应出现在摘要里", "第一章正文")]
    public void Result_ProducesReadableSummary_WithoutRawJson(string raw, string expected)
    {
        var summary = ToolDisplayFormatter.Result(raw);

        Assert.Equal(expected, summary);
        Assert.DoesNotContain("{\"", summary);
    }

    [Theory]
    [InlineData(
        "--- a/chapter.md\n+++ b/chapter.md\n@@ -1,2 +1,2 @@\n-旧句子\n+新句子",
        "chapter.md · +1 -1"
    )]
    [InlineData(
        "```diff\n--- a/chapter.md\n+++ b/chapter.md\n@@ -1,2 +1,2 @@\n-旧句子\n+新句子\n```",
        "chapter.md · +1 -1"
    )]
    [InlineData("| 角色 | 年龄 |\n|:-----|-----:|\n| 林月如 | 18 |", "表格 · 角色 / 年龄")]
    [InlineData("```json\n{\"a\":1}\n```", "代码块（json） · 1 行")]
    public void Result_SummarizesMarkdownTableAndDiff_InsteadOfSyntaxNoise(
        string raw,
        string expected
    )
    {
        Assert.Equal(expected, ToolDisplayFormatter.Result(raw));
    }

    [Fact]
    public void Result_JsonContentWithDiff_ReusesDiffSummary()
    {
        var raw = JsonSerializer.Serialize(
            new
            {
                success = true,
                content = "--- a/chapter.md\n+++ b/chapter.md\n@@ -1,2 +1,2 @@\n-旧句子\n+新句子",
            }
        );

        Assert.Equal("chapter.md · +1 -1", ToolDisplayFormatter.Result(raw));
    }

    [Fact]
    public void Result_KeepsAskUserReplyBlock_ForFullWidthDisplay()
    {
        var raw = "User answers:\n方向: [都市修真]\n主角: 还没想好";

        Assert.Equal(raw, ToolDisplayFormatter.Result(raw));
    }

    [Fact]
    public void Detail_PreservesFullText_AndCapsPathologicalOutput()
    {
        Assert.Equal(
            "{\n  \"path\": \"a.md\"\n}",
            ToolDisplayFormatter.Detail("{\n  \"path\": \"a.md\"\n}")
        );

        var capped = ToolDisplayFormatter.Detail(new string('x', 100), max: 10);
        Assert.StartsWith("xxxxxxxxxx", capped, StringComparison.Ordinal);
        Assert.Contains("已截断", capped, StringComparison.Ordinal);
    }
}
