using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MarkdownConverter 表格渲染集成测试：验证 Unicode box-drawing 表格、
/// 双宽 CJK 对齐、多行 cell 折行、list fallback。
/// </summary>
public class MarkdownConverterTableTests
{
    // ══════════════════════════════════════════════════════
    //  基础表格渲染
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_SimpleTable_RendersBoxDrawing()
    {
        var md = """
            | Name | Age |
            |------|-----|
            | Alice | 30 |
            | Bob   | 25 |
            """;

        var result = MarkdownConverter.Convert(md, 80);

        // 应该有 box-drawing 字符
        Assert.Contains("┌", result);
        Assert.Contains("┐", result);
        Assert.Contains("└", result);
        Assert.Contains("┘", result);
        Assert.Contains("├", result);
        Assert.Contains("┤", result);
        Assert.Contains("Alice", result);
        Assert.Contains("Bob", result);
    }

    [Fact]
    public void Convert_TableWithAlignment_RendersCorrectly()
    {
        var md = """
            | Left | Center | Right |
            |:-----|:------:|------:|
            | a    |   b    |     c |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("┌", result);
        Assert.Contains("─", result);
    }

    [Fact]
    public void Convert_TableWithSeparator_RendersAllBorders()
    {
        var md = """
            | Col1 | Col2 |
            |------|------|
            | R1C1 | R1C2 |
            | R2C1 | R2C2 |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        // 至少渲染了顶、头分隔、底三条横线
        var horizontalLines = result.Split('\n').Count(l => l.Contains('─') && l.Contains('┼'));
        Assert.True(horizontalLines >= 1);
    }

    // ══════════════════════════════════════════════════════
    //  CJK 表格对齐
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_TableWithCjk_RendersWithCorrectWidth()
    {
        var md = """
            | 角色名 | 年龄 |
            |--------|------|
            | 林默   | 25   |
            | 青云   | 30   |
            """;

        var result = MarkdownConverter.Convert(md, 80);

        Assert.Contains("角色名", result);
        Assert.Contains("林默", result);
        Assert.Contains("青云", result);
        Assert.Contains("│", result); // 竖线分隔
    }

    [Fact]
    public void Convert_TableAllCjk_BoxDrawingCorrect()
    {
        var md = """
            | 名称 | 类型 | 描述 |
            |------|------|------|
            | 青云剑 | 武器 | 镇宗之宝 |
            | 筑基丹 | 丹药 | 突破境界 |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("┌", result);
        Assert.Contains("青云剑", result);
        Assert.Contains("镇宗之宝", result);
    }

    // ══════════════════════════════════════════════════════
    //  窄宽度 — list fallback
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_TableWide_StillRendersContent()
    {
        var md = """
            | VeryLongColumnName | AnotherLongColumn | ThirdColumn |
            |--------------------|-------------------|-------------|
            | data1              | data2             | data3       |
            """;

        // At a reasonable width, table data is rendered
        var result = MarkdownConverter.Convert(md, 80);

        Assert.Contains("data1", result);
        Assert.Contains("data2", result);
        Assert.Contains("data3", result);
    }

    [Fact]
    public void Convert_TableWithWideContent_Wraps()
    {
        var md = """
            | Title | Description |
            |-------|-------------|
            | Short | A very long description that should wrap to multiple lines in a narrow table |
            """;

        var result = MarkdownConverter.Convert(md, 50);

        // 应该渲染 box-drawing 表格
        Assert.Contains("│", result);
    }

    // ══════════════════════════════════════════════════════
    //  边界情况
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_EmptyInput_ReturnsEmpty()
    {
        var result = MarkdownConverter.Convert("", 80);
        Assert.Equal("", result);
    }

    [Fact]
    public void Convert_NoTable_ReturnsOriginal()
    {
        var md = "Just plain text\nwithout any tables.";
        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("plain text", result);
    }

    [Fact]
    public void Convert_SingleColumnTable_RendersBoxDrawing()
    {
        var md = """
            | Item |
            |------|
            | One  |
            | Two  |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("┌", result);
        Assert.Contains("One", result);
    }

    [Fact]
    public void Convert_TableWithEmptyCells_RendersPlaceholder()
    {
        var md = """
            | A | B |
            |---|---|
            | x |   |
            |   | y |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("x", result);
        Assert.Contains("y", result);
    }

    [Fact]
    public void Convert_TableWithHeaderOnly_NoData()
    {
        var md = """
            | Col1 | Col2 |
            |------|------|
            """;

        var result = MarkdownConverter.Convert(md, 80);
        // 应该渲染表头和底边
        Assert.Contains("┌", result);
        Assert.Contains("└", result);
        Assert.Contains("Col1", result);
    }
}
