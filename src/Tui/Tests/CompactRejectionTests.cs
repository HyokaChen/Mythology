using Tui;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 压缩被拒绝的两种语义必须分开（<c>CompactResult</c> 拆分）：
/// B4 防重入（已有压缩进行中）文案是「压缩进行中、可重试」；
/// 模式互斥（接续模式）文案必须指向换页机制，且不能说成「压缩进行中」。
/// 混词的后果：未来新增触发源漏判模式时，用户会看到完全错误的提示。
/// </summary>
public sealed class CompactRejectionTests
{
    [Fact]
    public void DescribeRejection_WrongMode_PointsAtWindowRoll_NotAtInProgress()
    {
        var (toast, compactEnd) = TuiApp.DescribeRejection(TuiApp.CompactResult.RejectedWrongMode);

        Assert.Contains("接续模式", toast);
        Assert.Contains("新页", toast);
        Assert.DoesNotContain("压缩进行中", toast);
        Assert.Contains("接续模式", compactEnd);
        Assert.DoesNotContain("已有压缩进行中", compactEnd);
    }

    [Fact]
    public void DescribeRejection_InProgress_KeepsRetryableWording()
    {
        var (toast, compactEnd) = TuiApp.DescribeRejection(TuiApp.CompactResult.RejectedInProgress);

        Assert.Contains("压缩进行中", toast);
        Assert.DoesNotContain("接续模式", toast);
        Assert.Contains("已有压缩进行中", compactEnd);
    }

    /// <summary>两种拒绝的文案必须可区分——这是拆分的最低限度保证。</summary>
    [Fact]
    public void DescribeRejection_TwoRejections_AreDistinguishable()
    {
        var inProgress = TuiApp.DescribeRejection(TuiApp.CompactResult.RejectedInProgress);
        var wrongMode = TuiApp.DescribeRejection(TuiApp.CompactResult.RejectedWrongMode);

        Assert.NotEqual(inProgress.Toast, wrongMode.Toast);
        Assert.NotEqual(inProgress.CompactEnd, wrongMode.CompactEnd);
    }
}
