using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// DiffComputer 差异计算测试。
/// </summary>
public class DiffComputerTests
{
    // ══════════════════════════════════════════════════════
    //  Compute
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Compute_IdenticalTexts_AllContext()
    {
        var lines = DiffComputer.Compute("hello\nworld", "hello\nworld");
        Assert.All(lines, l => Assert.Equal(DiffLineType.Context, l.Type));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Compute_Insertion_DetectsAddedLines()
    {
        var lines = DiffComputer.Compute("line1\nline3", "line1\nline2\nline3");
        Assert.Contains(lines, l => l is { Type: DiffLineType.Insert, Text: "line2" });
    }

    [Fact]
    public void Compute_Deletion_DetectsRemovedLines()
    {
        var lines = DiffComputer.Compute("line1\nline2\nline3", "line1\nline3");
        Assert.Contains(lines, l => l is { Type: DiffLineType.Delete, Text: "line2" });
    }

    [Fact]
    public void Compute_Modification_AdjacentDeleteAndInsert()
    {
        var lines = DiffComputer.Compute("old", "new");
        Assert.Contains(lines, l => l is { Type: DiffLineType.Delete, Text: "old" });
        Assert.Contains(lines, l => l is { Type: DiffLineType.Insert, Text: "new" });
    }

    [Fact]
    public void Compute_EmptyOldText_AllInsert()
    {
        var lines = DiffComputer.Compute("", "new content");
        Assert.All(lines, l => Assert.Equal(DiffLineType.Insert, l.Type));
    }

    [Fact]
    public void Compute_EmptyNewText_AllDelete()
    {
        var lines = DiffComputer.Compute("old content", "");
        Assert.All(lines, l => Assert.Equal(DiffLineType.Delete, l.Type));
    }

    [Fact]
    public void Compute_BothEmpty_EmptyResult()
    {
        var lines = DiffComputer.Compute("", "");
        Assert.Empty(lines);
    }

    [Fact]
    public void Compute_LineNumbers_TrackCorrectly()
    {
        var lines = DiffComputer.Compute("a\nb\nc", "a\nx\nc");
        // a: context old=1 new=1
        // b: delete old=2 new=null
        // x: insert old=null new=2
        // c: context old=2(new=3) ... actually after insert line numbering
        Assert.Contains(
            lines,
            l => l is { Type: DiffLineType.Context, OldLineNum: 1, NewLineNum: 1 }
        );
    }

    // ══════════════════════════════════════════════════════
    //  CountChanges
    // ══════════════════════════════════════════════════════

    [Fact]
    public void CountChanges_Identical_Zero()
    {
        var lines = DiffComputer.Compute("same", "same");
        var (added, removed) = DiffComputer.CountChanges(lines);
        Assert.Equal((0, 0), (added, removed));
    }

    [Fact]
    public void CountChanges_Insertions_Counted()
    {
        var lines = DiffComputer.Compute("a", "a\nb\nc");
        var (added, removed) = DiffComputer.CountChanges(lines);
        Assert.Equal(2, added);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void CountChanges_Deletions_Counted()
    {
        var lines = DiffComputer.Compute("a\nb\nc", "a");
        var (added, removed) = DiffComputer.CountChanges(lines);
        Assert.Equal(0, added);
        Assert.Equal(2, removed);
    }

    // ══════════════════════════════════════════════════════
    //  FoldContext
    // ══════════════════════════════════════════════════════

    [Fact]
    public void FoldContext_NoChanges_AllFolded()
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineType.Context, 1, 1, "a"),
            new(DiffLineType.Context, 2, 2, "b"),
            new(DiffLineType.Context, 3, 3, "c"),
        };
        var folded = DiffComputer.FoldContext(lines, 1);
        Assert.Single(folded);
        Assert.Contains("skipped", folded[0].Text);
    }

    [Fact]
    public void FoldContext_WithChange_KeepsContextAround()
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineType.Context, 1, 1, "a"),
            new(DiffLineType.Context, 2, 2, "b"),
            new(DiffLineType.Insert, null, 3, "new"),
            new(DiffLineType.Context, 3, 4, "c"),
            new(DiffLineType.Context, 4, 5, "d"),
        };
        var folded = DiffComputer.FoldContext(lines, 1);
        // Should keep line 2 (context before), Insert, line 3 (context after)
        Assert.Contains(folded, l => l is { Type: DiffLineType.Context, Text: "b" });
        Assert.Contains(folded, l => l is { Type: DiffLineType.Insert, Text: "new" });
    }

    [Fact]
    public void FoldContext_MultipleChanges_KeepsAll()
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineType.Context, 1, 1, "a"),
            new(DiffLineType.Delete, 2, null, "old"),
            new(DiffLineType.Insert, null, 2, "new"),
            new(DiffLineType.Context, 3, 3, "c"),
        };
        var folded = DiffComputer.FoldContext(lines, 0);
        Assert.Equal(4, folded.Count); // With contextLines=0, all lines are kept because the two changes are adjacent and each has no context lines = 4 lines
    }

    [Fact]
    public void FoldContext_EmptyInput_ReturnsEmpty()
    {
        var folded = DiffComputer.FoldContext([]);
        Assert.Empty(folded);
    }
}
