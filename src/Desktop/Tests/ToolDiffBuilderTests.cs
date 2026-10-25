using Core.Shared;
using Core.Tools;
using Desktop.Models;
using Desktop.Services;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// Desktop 结构化 diff 必须与 TUI 共用同一个 DiffComputer（DiffPlex Myers），
/// 包括计数、上下文折叠和多 hunk 分隔。
/// </summary>
public sealed class ToolDiffBuilderTests
{
    private sealed record TestDiffDetails(string? Path, EditHunk[]? Hunks) : IDiffDetails;

    [Fact]
    public void Build_UsesDiffComputerCountsAndPreservesDeleteInsertLines()
    {
        const string oldText = "line1\n旧句子\nline3";
        const string newText = "line1\n新句子\nline3";
        var details = new TestDiffDetails(
            "chapters/ch001.md",
            [new EditHunk { OldText = oldText, NewText = newText }]
        );

        var diff = ToolDiffBuilder.Build(details);
        Assert.NotNull(diff);

        var expected = DiffComputer.Compute(oldText, newText);
        var (added, removed) = DiffComputer.CountChanges(expected);
        Assert.Equal(added, diff!.Added);
        Assert.Equal(removed, diff.Removed);
        Assert.Equal("ch001.md · +1 -1", diff.Summary);
        Assert.Contains(diff.Lines, line => line is { IsDelete: true, Text: "旧句子" });
        Assert.Contains(diff.Lines, line => line is { IsInsert: true, Text: "新句子" });
        Assert.Equal("2", diff.Lines.Single(line => line.IsDelete).LineNumber);
    }

    [Fact]
    public void DiffLineView_ContextUsesOldLineNumberLikeTui()
    {
        var context = DiffLineView.From(new DiffLine(DiffLineType.Context, 7, 9, "same"));

        Assert.Equal("7", context.LineNumber);
    }

    [Fact]
    public void Build_MultipleHunks_MatchesTuiSeparatorAndFolding()
    {
        var hunks = new[]
        {
            new EditHunk { OldText = "a\nold1\nb", NewText = "a\nnew1\nb" },
            new EditHunk { OldText = "c\nold2\nd", NewText = "c\nnew2\nd" },
        };
        var diff = ToolDiffBuilder.Build(new TestDiffDetails("chapter.md", hunks));
        Assert.NotNull(diff);

        var all = new List<DiffLine>();
        foreach (var hunk in hunks)
        {
            if (all.Count > 0)
                all.Add(new DiffLine(DiffLineType.Context, null, null, "···"));
            all.AddRange(DiffComputer.Compute(hunk.OldText, hunk.NewText));
        }
        var (added, removed) = DiffComputer.CountChanges(all);
        var folded = DiffComputer.FoldContext(all, ToolDiffBuilder.ContextLines);

        Assert.Equal(added, diff!.Added);
        Assert.Equal(removed, diff.Removed);
        Assert.Equal(folded.Count, diff.Lines.Count);
        Assert.Equal(folded.Select(line => line.Text), diff.Lines.Select(line => line.Text));
        Assert.Contains(diff.Lines, line => line is { IsContext: true, Text: "···" });
    }

    [Fact]
    public void BuildFromEditArguments_RebuildsHistoryDiff()
    {
        const string args =
            """{"path":"chapters/ch001.md","edits":[{"oldText":"旧句子","newText":"新句子"}]}""";

        var diff = ToolDiffBuilder.BuildFromEditArguments(args);

        Assert.NotNull(diff);
        Assert.Equal("ch001.md · +1 -1", diff!.Summary);
        Assert.Contains(diff.Lines, line => line is { IsDelete: true, Text: "旧句子" });
        Assert.Contains(diff.Lines, line => line is { IsInsert: true, Text: "新句子" });
    }

    [Theory]
    [InlineData("已应用 2/2 个编辑到 chapter.md", true)]
    [InlineData("已应用 1/2 个编辑到 chapter.md", false)]
    [InlineData("未应用任何编辑到 chapter.md", false)]
    [InlineData("--- a/chapter.md\n+++ b/chapter.md", true)]
    [InlineData(null, true)]
    public void CanRebuildEditDiff_OnlyTrustsFullyAppliedOrUnknownResults(
        string? result,
        bool expected
    )
    {
        Assert.Equal(expected, ToolDiffBuilder.CanRebuildEditDiff(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"path":"chapter.md"}""")]
    [InlineData("""{"path":"chapter.md","edits":[]}""")]
    [InlineData("""{"path":"chapter.md","edits":[{"oldText":"x"}]}""")]
    public void BuildFromEditArguments_MissingOrInvalid_ReturnsNull(string? args)
    {
        Assert.Null(ToolDiffBuilder.BuildFromEditArguments(args));
    }

    [Fact]
    public void Build_NoHunks_ReturnsNull()
    {
        Assert.Null(ToolDiffBuilder.Build(new TestDiffDetails("chapter.md", null)));
        Assert.Null(ToolDiffBuilder.Build(new TestDiffDetails("chapter.md", [])));
    }
}
