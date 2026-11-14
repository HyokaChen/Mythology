using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 状态栏上下文仪表：API 实测是采样快照，只有在实时上下文未显著变化时才能复用。
/// compact 后 live 大幅下降，旧 PromptTokens 必须作废，否则仪表会钉在压缩前的 278K。
/// </summary>
public sealed class ContextGaugeFreshnessTests
{
    [Theory]
    [InlineData(1000, 1000, true)] // 未变化
    [InlineData(900, 1000, true)] // 轻微下降（测量误差）
    [InlineData(700, 1000, false)] // compact/裁剪：旧实测已过期
    [InlineData(22_980, 183_700, false)] // 实际 compact 后：仪表必须改用新上下文，不能复用旧 PromptTokens
    [InlineData(1200, 1000, false)] // 继续增长：旧实测已过期
    [InlineData(0, 0, false)] // 无采样
    public void IsContextMeasureFresh_RejectsStaleSamples(
        long live,
        long measuredLive,
        bool expected
    )
    {
        Assert.Equal(expected, MainWindowViewModel.IsContextMeasureFresh(live, measuredLive));
    }
}
