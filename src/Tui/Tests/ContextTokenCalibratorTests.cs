using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ContextTokenCalibrator 测试：字符估算 → API 实测口径的投影。
/// - Project：完整 prompt 口径（含固定开销），compact 前检查用；
/// - ProjectConversation / ToConversationTokens：会话上下文口径（0 起算，不含固定开销），状态栏展示用。
/// </summary>
public class ContextTokenCalibratorTests
{
    /// <summary>未校准时，Project 原样返回估算值（首轮无实测数据的兜底）。</summary>
    [Fact]
    public void Project_WithoutCalibration_ReturnsEstimateAsIs()
    {
        var calibrator = new ContextTokenCalibrator();

        Assert.False(calibrator.HasCalibration);
        Assert.Equal(12_345, calibrator.Project(12_345));
    }

    /// <summary>
    /// 拟合真值模型 measured = 5_000(固定开销) + 1.3 × est：
    /// 校准后 Project 应同时补上固定开销与 chars/token 修正，逼近实测口径。
    /// </summary>
    [Fact]
    public void Project_AfterCalibration_MatchesApiMeasuredScale()
    {
        var calibrator = new ContextTokenCalibrator();

        // 生成 8 个采样点：est 从 2K 到 16K，measured = 5000 + 1.3 × est
        for (var i = 0; i < 8; i++)
        {
            var est = 2_000.0 + i * 2_000.0;
            var measured = 5_000 + 1.3 * est;
            calibrator.AddSample(est, measured);
        }

        Assert.True(calibrator.HasCalibration);
        // 投影一个新估算：10K 内容 ≈ 5000 + 1.3×10000 = 18K
        var projected = calibrator.Project(10_000);
        Assert.InRange(projected, 17_500, 18_500);
    }

    /// <summary>
    /// compact 场景：上下文从 40K 缩到 8K（估算口径），
    /// 投影后应 ≈ 固定开销 + 1.3×8K ≈ 15.4K，而不是低估的 8K。
    /// </summary>
    [Fact]
    public void Project_AfterCompact_StillIncludesFixedOverhead()
    {
        var calibrator = new ContextTokenCalibrator();

        // 采样点来自 compact 前的会话（est 12K~44K，measured = 5000 + 1.3×est）
        for (var i = 0; i < 6; i++)
        {
            var est = 12_000.0 + i * 4_000.0;
            calibrator.AddSample(est, 5_000 + 1.3 * est);
        }

        // compact 后：估算只有 8K，投影必须 ≈ 5000 + 1.3×8000 ≈ 15.4K
        var projected = calibrator.Project(8_000);
        Assert.InRange(projected, 14_900, 15_900);
        Assert.True(projected > 8_000, "投影必须大于原始估算（含固定开销），否则口径仍然低估");
    }

    /// <summary>采样点跨度不足（<500 tokens）时斜率不可靠，保留未校准兜底。</summary>
    [Fact]
    public void AddSample_ClusteredPoints_StaysUncalibrated()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(10_000, 12_000);
        calibrator.AddSample(10_100, 12_150);
        calibrator.AddSample(10_200, 12_300);

        Assert.False(calibrator.HasCalibration);
        Assert.Equal(10_000, calibrator.Project(10_000));
    }

    /// <summary>退化数据（实测不随估算增长）时保留未校准兜底。</summary>
    [Fact]
    public void AddSample_DegenerateData_StaysUncalibrated()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(2_000, 12_000);
        calibrator.AddSample(4_000, 12_000);
        calibrator.AddSample(6_000, 12_000);

        Assert.False(calibrator.HasCalibration);
    }

    /// <summary>非法采样（≤0）直接忽略。</summary>
    [Fact]
    public void AddSample_NonPositive_IsIgnored()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(0, 1_000);
        calibrator.AddSample(-5, 1_000);
        calibrator.AddSample(1_000, 0);

        Assert.False(calibrator.HasCalibration);
    }

    /// <summary>Reset 后回到未校准兜底（模型切换场景）。</summary>
    [Fact]
    public void Reset_ClearsCalibration()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(2_000, 5_000 + 1.3 * 2_000);
        calibrator.AddSample(6_000, 5_000 + 1.3 * 6_000);
        calibrator.AddSample(10_000, 5_000 + 1.3 * 10_000);
        Assert.True(calibrator.HasCalibration);

        calibrator.Reset();

        Assert.False(calibrator.HasCalibration);
        Assert.Equal(7_000, calibrator.Project(7_000));
    }

    /// <summary>
    /// 会话上下文口径：新会话 / 清空后 est = 0，显示必须为 0（pi/Claude Code 语义：从 0 起算）。
    /// </summary>
    [Fact]
    public void ProjectConversation_EmptyContext_ReturnsZero()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(2_000, 5_000 + 1.3 * 2_000);
        calibrator.AddSample(6_000, 5_000 + 1.3 * 6_000);
        calibrator.AddSample(10_000, 5_000 + 1.3 * 10_000);
        Assert.True(calibrator.HasCalibration);

        Assert.Equal(0, calibrator.ProjectConversation(0));
    }

    /// <summary>
    /// 会话上下文口径 = rate × 估算（不含固定开销）：
    /// 10K 内容 → 1.3×10K ≈ 13K，而不是完整 prompt 的 18K。
    /// </summary>
    [Fact]
    public void ProjectConversation_AfterCalibration_ExcludesFixedOverhead()
    {
        var calibrator = new ContextTokenCalibrator();
        for (var i = 0; i < 8; i++)
        {
            var est = 2_000.0 + i * 2_000.0;
            calibrator.AddSample(est, 5_000 + 1.3 * est);
        }
        Assert.True(calibrator.HasCalibration);

        var conversation = calibrator.ProjectConversation(10_000);
        Assert.InRange(conversation, 12_500, 13_500);
        // 完整口径 18K vs 会话口径 13K：固定开销不进入展示
        Assert.InRange(calibrator.Project(10_000), 17_500, 18_500);
    }

    /// <summary>未校准时，ProjectConversation 原样返回估算（首轮无实测的兜底）。</summary>
    [Fact]
    public void ProjectConversation_WithoutCalibration_ReturnsEstimate()
    {
        var calibrator = new ContextTokenCalibrator();
        Assert.Equal(8_000, calibrator.ProjectConversation(8_000));
    }

    /// <summary>
    /// LLM 调用中实时展示：实测值（完整 prompt）扣除固定开销 → 会话上下文。
    /// 实测 18K − 固定开销 5K = 13K。
    /// </summary>
    [Fact]
    public void ToConversationTokens_AfterCalibration_SubtractsOverhead()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(2_000, 5_000 + 1.3 * 2_000);
        calibrator.AddSample(6_000, 5_000 + 1.3 * 6_000);
        calibrator.AddSample(10_000, 5_000 + 1.3 * 10_000);
        Assert.True(calibrator.HasCalibration);

        var conversation = calibrator.ToConversationTokens(18_000);
        Assert.InRange(conversation, 12_500, 13_500);
    }

    /// <summary>未校准（无法扣除固定开销）时，ToConversationTokens 原样返回实测值。</summary>
    [Fact]
    public void ToConversationTokens_WithoutCalibration_ReturnsMeasured()
    {
        var calibrator = new ContextTokenCalibrator();
        Assert.Equal(18_000, calibrator.ToConversationTokens(18_000));
    }

    /// <summary>固定开销 = 拟合截距（系统提示 + 工具定义 + 小说上下文）。</summary>
    [Fact]
    public void OverheadTokens_AfterCalibration_EqualsFitIntercept()
    {
        var calibrator = new ContextTokenCalibrator();
        calibrator.AddSample(2_000, 5_000 + 1.3 * 2_000);
        calibrator.AddSample(6_000, 5_000 + 1.3 * 6_000);
        calibrator.AddSample(10_000, 5_000 + 1.3 * 10_000);

        Assert.True(calibrator.HasCalibration);
        Assert.InRange(calibrator.OverheadTokens, 4_500, 5_500);
    }

    /// <summary>未校准时固定开销未知，返回 0。</summary>
    [Fact]
    public void OverheadTokens_WithoutCalibration_ReturnsZero()
    {
        var calibrator = new ContextTokenCalibrator();
        Assert.Equal(0, calibrator.OverheadTokens);
    }

    /// <summary>投影结果必须 ≥ 0（回归拟合噪声可能产生负截距，需钳制）。</summary>
    [Fact]
    public void Project_NeverNegative()
    {
        var calibrator = new ContextTokenCalibrator();
        // 构造一条截距为负的退化线（实测整体低于估算——极端 tokenizer 场景）
        calibrator.AddSample(2_000, 1_000);
        calibrator.AddSample(4_000, 2_100);
        calibrator.AddSample(6_000, 3_200);

        if (calibrator.HasCalibration)
            Assert.True(calibrator.Project(100) >= 0);
    }
}
