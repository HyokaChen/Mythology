using Core.Infrastructure;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// CircuitBreaker 断路器测试。
/// 验证重复调用检测、高频调用触发、参数差异性判断逻辑。
/// </summary>
public class CircuitBreakerTests
{
    [Fact]
    /// <summary>空历史记录不应触发断路器。</summary>
    public void Check_ReturnsNullForEmptyHistory()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        Assert.Null(CircuitBreaker.Check(history));
    }

    [Fact]
    /// <summary>低于阈值时不应触发断路器。</summary>
    public void Check_ReturnsNullForBelowThreshold()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", "{}"));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", "{}"));

        // Threshold is 3, only 2 identical calls
        Assert.Null(CircuitBreaker.Check(history));
    }

    [Fact]
    /// <summary>连续相同调用达到阈值时应触发断路器。</summary>
    public void Check_TriggersOnConsecutiveIdenticalCalls()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        var record = new ToolDispatcher.ToolCallRecord("stuck_tool", """{"arg":"val"}""");
        history.Add(record);
        history.Add(record);
        history.Add(record);

        var result = CircuitBreaker.Check(history);
        Assert.NotNull(result);
        Assert.Equal("stuck_tool", result.ToolName);
        Assert.Equal(3, result.RepeatCount);
    }

    [Fact]
    /// <summary>同一轮次内的并行调用不应触发断路器。</summary>
    public void Check_DoesNotTriggerForSameTurnParallelBurst()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        // 同一 Turn 内 5 个并行调用（不同参数）→ 不应触发高频检测
        var turnId = 1;
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":1}""", turnId));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":2}""", turnId));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":3}""", turnId));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":4}""", turnId));
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", turnId));

        var result = CircuitBreaker.Check(history);
        Assert.Null(result);
    }

    [Fact]
    /// <summary>跨轮次的高频调用 + 参数有重复 — 高频检测已移除，不应触发。</summary>
    public void Check_DoesNotTriggerOnHighFrequencyWithRepeatedArgs()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":1}""", 1));
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 2));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":2}""", 3));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":2}""", 4));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":3}""", 5));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":4}""", 6));
        history.Add(new ToolDispatcher.ToolCallRecord("other2", "{}", 7));
        history.Add(new ToolDispatcher.ToolCallRecord("repeated", """{"a":5}""", 8));

        var result = CircuitBreaker.Check(history);
        // 高频检测已移除，最近 3 次调用不完全相同，不应触发
        Assert.Null(result);
    }

    [Fact]
    /// <summary>跨轮次的高频调用 + 参数全部不同 → 不应触发（批量操作）。</summary>
    public void Check_DoesNotTriggerForBatchOperationWithDistinctArgs()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(20);
        // 5 个不同 Turn 调用 manage_plotline，参数全部不同 → 批量创建，不应触发
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "manage_plotline",
                """{"action":"create","name":"主线A"}""",
                1
            )
        );
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "manage_plotline",
                """{"action":"create","name":"支线B"}""",
                2
            )
        );
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "manage_plotline",
                """{"action":"create","name":"伏笔C"}""",
                3
            )
        );
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "manage_plotline",
                """{"action":"create","name":"暗线D"}""",
                4
            )
        );
        history.Add(
            new ToolDispatcher.ToolCallRecord(
                "manage_plotline",
                """{"action":"create","name":"支线E"}""",
                5
            )
        );
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 6));
        history.Add(new ToolDispatcher.ToolCallRecord("other2", "{}", 7));
        history.Add(new ToolDispatcher.ToolCallRecord("other3", "{}", 8));

        var result = CircuitBreaker.Check(history);
        Assert.Null(result);
    }

    [Fact]
    /// <summary>跨轮次的高频调用 + 参数全部不同 — 高频检测已移除，不应触发。</summary>
    public void Check_DoesNotTriggerOnHighFrequencyFallback()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(20);
        // 7 个不同 Turn 调用同一工具，参数全部不同 → 超过兜底阈值 7，触发
        for (var i = 1; i <= 7; i++)
        {
            history.Add(new ToolDispatcher.ToolCallRecord("runaway_tool", $"{{\"page\":{i}}}", i));
        }
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 8));

        var result = CircuitBreaker.Check(history);
        // 高频兜底已移除，参数全部不同且非连续完全相同，不应触发
        Assert.Null(result);
    }

    [Fact]
    /// <summary>不同工具的调用不应触发断路器。</summary>
    public void Check_DoesNotTriggerForDiverseTools()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(10);
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", "{}"));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_b", "{}"));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_c", "{}"));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_d", "{}"));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_e", "{}"));

        Assert.Null(CircuitBreaker.Check(history));
    }

    [Fact]
    /// <summary>高频但低于阈值（4次不同参数）→ 不触发。</summary>
    public void Check_DoesNotTriggerForBelowHighFrequencyThreshold()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(20);
        // 4 次不同 Turn 调用同一工具，参数不同 → 低于 HighFrequencyThreshold=5，不触发
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", """{"x":1}""", 1));
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 2));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", """{"x":2}""", 3));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", """{"x":3}""", 4));
        history.Add(new ToolDispatcher.ToolCallRecord("tool_a", """{"x":4}""", 5));
        history.Add(new ToolDispatcher.ToolCallRecord("other2", "{}", 6));
        history.Add(new ToolDispatcher.ToolCallRecord("other3", "{}", 7));
        history.Add(new ToolDispatcher.ToolCallRecord("other4", "{}", 8));

        var result = CircuitBreaker.Check(history);
        Assert.Null(result);
    }

    [Fact]
    /// <summary>高频恰好达到阈值（5次）+ 参数全部不同 + 未超兜底 → 不触发。</summary>
    public void Check_DoesNotTriggerForExactThresholdWithDistinctArgs()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(20);
        // 5 次不同 Turn，参数全不同 → 批量操作，不触发
        for (var i = 1; i <= 5; i++)
        {
            history.Add(
                new ToolDispatcher.ToolCallRecord(
                    "manage_scene",
                    $"{{\"action\":\"create\",\"title\":\"场景{i}\"}}",
                    i
                )
            );
        }
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 6));
        history.Add(new ToolDispatcher.ToolCallRecord("other2", "{}", 7));
        history.Add(new ToolDispatcher.ToolCallRecord("other3", "{}", 8));

        var result = CircuitBreaker.Check(history);
        Assert.Null(result);
    }

    [Fact]
    /// <summary>跨轮次的高频 + 相同参数，但最近3次不同 — 新逻辑只看最近3次，不应触发。</summary>
    public void Check_DoesNotTriggerWhenRecent3Differ()
    {
        var history = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(20);
        history.Add(new ToolDispatcher.ToolCallRecord("edit_text", """{"old":"一段文本"}""", 1));
        history.Add(new ToolDispatcher.ToolCallRecord("edit_text", """{"old":"一段文本"}""", 2));
        history.Add(new ToolDispatcher.ToolCallRecord("edit_text", """{"old":"一段文本"}""", 3));
        history.Add(new ToolDispatcher.ToolCallRecord("edit_text", """{"old":"另一段文本"}""", 4));
        history.Add(new ToolDispatcher.ToolCallRecord("edit_text", """{"old":"一段文本"}""", 5));
        history.Add(new ToolDispatcher.ToolCallRecord("other", "{}", 6));
        history.Add(new ToolDispatcher.ToolCallRecord("other2", "{}", 7));
        history.Add(new ToolDispatcher.ToolCallRecord("other3", "{}", 8));

        var result = CircuitBreaker.Check(history);
        // 最近 3 次调用 (other, other2, other3) 不完全相同，不应触发
        Assert.Null(result);
    }
}
