using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MythMark 品牌 logo 字符画测试（方案 A）。
/// 断言：数据表不变量（偶数像素行、定宽、仅合法字符）、画布固定尺寸、
/// 左右页颜色分布（左页鼠尾草绿 / 右页玫瑰粉，中缝墨色分隔）与青色路径存在。
/// 数据字段为 internal 测试缝（Tui.Tests 经 InternalsVisibleTo 访问）。
/// </summary>
public sealed class MythMarkTests
{
    [Fact]
    public void MarkPixels_Invariants_ValidArt()
    {
        Assert.True(MythMark.Height > 0, "至少 1 个字符行");
        Assert.Equal(MythMark.Height * 2, MythMark.MarkPixels.Length); // 偶数像素行

        foreach (var row in MythMark.MarkPixels)
        {
            Assert.Equal(MythMark.Width, row.Length); // 定宽
            Assert.All(
                row,
                ch => Assert.True(ch is '.' or (>= '0' and <= '3'), $"非法字符: '{ch}'")
            );
        }
    }

    [Fact]
    public void Build_ReturnsFixedSizeCanvas()
    {
        var canvas = MythMark.Build();

        Assert.Equal(MythMark.Width, canvas.MinWidth);
        Assert.Equal(MythMark.Width, canvas.MaxWidth);
        Assert.Equal(MythMark.Height, canvas.MinHeight);
        Assert.Equal(MythMark.Height, canvas.MaxHeight);
    }

    [Fact]
    public void MarkPixels_LeftPageSage_RightPageRose_WithInkSpine()
    {
        var rows = MythMark.MarkPixels;
        var (sageLeft, roseRight) = (0, 0);
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                if (c < MythMark.Width / 2 && row[c] == '1')
                    sageLeft++; // 左半页 → 鼠尾草绿
                if (c >= MythMark.Width / 2 && row[c] == '2')
                    roseRight++; // 右半页 → 玫瑰粉
            }
        }

        Assert.True(sageLeft >= 10, $"左页鼠尾草绿格数过少: {sageLeft}");
        Assert.True(roseRight >= 15, $"右页玫瑰粉格数过少: {roseRight}");

        // 存在墨色贯穿列（墨色占多数的列）：书页轮廓与书脊形成视觉分隔
        var inkCols = Enumerable
            .Range(0, MythMark.Width)
            .Count(c => rows.Count(row => row[c] == '0') > MythMark.Height / 2);
        Assert.True(inkCols >= 2, $"墨色贯穿列过少: {inkCols}");

        // 前进路径：青色（'3'）必须存在（小尺寸点缀下仅剩少量单元格）
        var tealCells = rows.Sum(row => row.Count(ch => ch == '3'));
        Assert.True(tealCells >= 2, $"青色路径格数过少: {tealCells}");
    }
}
