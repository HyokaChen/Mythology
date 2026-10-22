using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 全书只读检索：跨章找「这个设定在哪提过」——命中正文/细纲/标题，
/// 一条章节最多一条结果，片段带上下文，空关键字不返回全量。
/// </summary>
public sealed class NovelTextSearchTests
{
    private static List<Chapter> Chapters() =>
        [
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Summary = "发现密信",
                Content = "雨落下来，他把密信折好放进怀里。密信上只有一行字。",
            },
            new()
            {
                Id = "c2",
                NovelId = "n1",
                Title = "进城",
                Order = 2,
                Content = "城门在他面前打开，守卫打量着他。",
            },
            new()
            {
                Id = "c3",
                NovelId = "n1",
                Title = "密信之谜",
                Order = 3,
                Content = "无人知晓那封信的来历。",
            },
        ];

    [Fact]
    public void Search_MatchesContentAndSummary_WithCounts()
    {
        var hits = NovelTextSearch.Search(Chapters(), "密信");

        // c1 正文 2 处 + 细纲 1 处 = 3；c3 只命中标题 1 处
        Assert.Equal(["c1", "c3"], hits.Select(h => h.Chapter.Id));
        Assert.Equal(3, hits[0].MatchCount);
        Assert.Equal(ChapterSearchField.Content, hits[0].Field);
        Assert.Contains("密信", hits[0].Snippet);
        Assert.Equal(ChapterSearchField.Title, hits[1].Field);
        Assert.Equal("标题", NovelTextSearch.FieldLabel(hits[1].Field));
    }

    [Fact]
    public void Search_SnippetHasContextAndStaysSingleLine()
    {
        var longContent = new string('字', 100) + "目标关键字" + new string('句', 100) + "\n第二行";
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "长章",
                Order = 1,
                Content = longContent,
            },
        };

        var hit = Assert.Single(NovelTextSearch.Search(chapters, "目标关键字"));

        Assert.StartsWith("…", hit.Snippet);
        Assert.EndsWith("…", hit.Snippet);
        Assert.DoesNotContain("\n", hit.Snippet);
        Assert.True(hit.Snippet.Length < 120, $"片段过长: {hit.Snippet.Length}");
    }

    [Fact]
    public void Search_IgnoresCase_AndReturnsEmptyForBlankQuery()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "The Letter",
                Order = 1,
                Content = "A SECRET letter.",
            },
        };

        Assert.Single(NovelTextSearch.Search(chapters, "secret"));
        Assert.Empty(NovelTextSearch.Search(chapters, "  "));
        Assert.Empty(NovelTextSearch.Search(chapters, null));
        Assert.Empty(NovelTextSearch.Search([], "secret"));
    }

    [Fact]
    public void Search_CapsHits_AndOrdersByMatchCount()
    {
        var chapters = Enumerable
            .Range(1, 50)
            .Select(i => new Chapter
            {
                Id = $"c{i}",
                NovelId = "n1",
                Title = $"第{i}节",
                Order = i,
                // 序号越大命中越多：验证「命中次数降序」优先于序号
                Content = string.Concat(Enumerable.Repeat("关键字", i % 5 + 1)),
            })
            .ToList();

        var hits = NovelTextSearch.Search(chapters, "关键字", maxHits: 10);

        Assert.Equal(10, hits.Count);
        Assert.True(hits[0].MatchCount >= hits[^1].MatchCount);
    }
}
