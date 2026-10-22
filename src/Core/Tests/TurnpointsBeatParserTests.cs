using Core.Cloud;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Turnpoints 确定性解析器测试（CHANGE_10 §2.7 矩阵源③）。
/// 解析失败 = 跳过（宁可不猜）；推断 beat 只喂云端 info/warning 级矩阵。
/// </summary>
public class TurnpointsBeatParserTests
{
    private static readonly HashSet<int> Valid = [1, 2, 5, 12, 30];

    [Fact]
    public void Parse_ChangeDocExample_Format()
    {
        // CHANGE_10 §2.7 示例格式（分号分隔）
        var beats = TurnpointsBeatParser.Parse(
            "第5章：真相揭露；第12章：盟友背叛；第30章：大战落幕",
            Valid
        );
        Assert.Equal(3, beats.Count);
        Assert.Equal(5, beats[0].ChapterOrder);
        Assert.Equal("reveal", beats[0].Type);
        Assert.Equal(12, beats[1].ChapterOrder);
        Assert.Equal("progress", beats[1].Type); // "盟友背叛" 无关键词 → progress
        Assert.Equal(30, beats[2].ChapterOrder);
    }

    [Fact]
    public void Parse_NewlineAndMixedSeparators()
    {
        var beats = TurnpointsBeatParser.Parse(
            "第1章：埋下伏笔\n第2章: 揭示真相；ch5. 呼应前文",
            Valid
        );
        Assert.Equal(3, beats.Count);
        Assert.Equal("plant", beats[0].Type);
        Assert.Equal("reveal", beats[1].Type);
        Assert.Equal("payoff", beats[2].Type);
    }

    [Fact]
    public void Parse_NoChapterPrefix_LinesSkipped()
    {
        // 宁可不猜：无章节前缀的条目（如 CHANGE_10 示例"林默死亡"）跳过
        var beats = TurnpointsBeatParser.Parse("第5章：真相揭露；林默死亡", Valid);
        var beat = Assert.Single(beats);
        Assert.Equal(5, beat.ChapterOrder);
    }

    [Fact]
    public void Parse_UnknownChapterOrder_Skipped()
    {
        var beats = TurnpointsBeatParser.Parse("第99章：真相揭露；第5章：揭示", Valid);
        var beat = Assert.Single(beats);
        Assert.Equal(5, beat.ChapterOrder);
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(TurnpointsBeatParser.Parse(null, Valid));
        Assert.Empty(TurnpointsBeatParser.Parse("", Valid));
        Assert.Empty(TurnpointsBeatParser.Parse("   ", Valid));
        // 无前缀纯文本 → 空
        Assert.Empty(TurnpointsBeatParser.Parse("主角觉醒；大结局", Valid));
    }

    [Fact]
    public void Parse_TensionDefaults_AlignedWithCloudSemantics()
    {
        var beats = TurnpointsBeatParser.Parse("第1章：埋下伏笔；第2章：高潮决战", Valid);
        Assert.Equal(0.2, beats[0].Tension); // plant
        Assert.Equal(1.0, beats[1].Tension); // critical
    }
}
