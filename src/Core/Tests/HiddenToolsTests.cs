using Core.Agent;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 隐藏名单（接续工具对用户不可见）：Redact 脱敏、工具统计过滤、与工具名的对应关系。
/// 工具本身的注册门禁在插件测试里（Plugins.ContextContinuation.Tests）。
/// </summary>
public sealed class HiddenToolsTests
{
    [Fact]
    public void Redact_ReplacesHiddenToolNames()
    {
        var text = "3. history_read_item — 读历史原文\n4. read_file — 读取文件\nnew_context 可用";
        var redacted = HiddenTools.Redact(text);

        Assert.DoesNotContain("history_read_item", redacted);
        Assert.DoesNotContain("new_context", redacted);
        Assert.Contains("私有工具", redacted);
        Assert.Contains("read_file", redacted); // 非隐藏工具保持原样
    }

    [Fact]
    public void Redact_NullOrEmpty_Passthrough()
    {
        Assert.Equal("", HiddenTools.Redact(null));
        Assert.Equal("", HiddenTools.Redact(""));
    }

    [Fact]
    public void IsHidden_OnlyContinuationTools()
    {
        Assert.True(HiddenTools.IsHidden("new_context"));
        Assert.True(HiddenTools.IsHidden("history_search_contents"));
        Assert.True(HiddenTools.IsHidden("history_search_contents"));
        Assert.False(HiddenTools.IsHidden("read_file"));
        Assert.False(HiddenTools.IsHidden(null));
    }

    [Fact]
    public void ToolUsageStats_HidesContinuationTools()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall("read_file");
        stats.RecordCall("read_file");
        stats.RecordCall("history_read_item");
        stats.RecordCall("history_search_contents");

        var summary = stats.GetSummary();

        Assert.Contains("read_file", summary);
        Assert.DoesNotContain("history_read_item", summary);
        Assert.DoesNotContain("history_search_contents", summary);
    }
}
