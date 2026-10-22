using System.Text.Json;
using Core.Data;
using Core.Forecast;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// forecast_plot：无小说 / 无纲要 / 正常推演（证据计数、笔记线索、开关关闭不检索）。
/// </summary>
public sealed class ForecastPlotToolTests
{
    private static FakeNovelStore CreateStore()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "推演工具书",
                Outline = "青铜钥匙开启北境之门",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Order = 1,
                Content = "正文",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = "n1",
                Order = 2,
                Title = "钥匙归位",
                Summary = "青铜钥匙归位",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c3",
                NovelId = "n1",
                Order = 3,
                Title = "使者",
                Summary = "北境使者抵达",
                CharacterIds = ["ch-envoy"],
            }
        );
        store.SavePlotline(
            new Plotline
            {
                Id = "p1",
                NovelId = "n1",
                Name = "青铜钥匙",
                Type = PlotlineType.Foreshadowing,
                PlannedRevealChapterId = "c2",
            }
        );
        store.SaveCharacter(
            new Character
            {
                Id = "ch-envoy",
                NovelId = "n1",
                Name = "北境使者",
            }
        );
        store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "半块玉的递法",
                Body = "第3章 递出半块玉",
                Status = NoteStatus.Active,
            }
        );
        store.SaveNote(
            new AuthorNote
            {
                NovelId = "n1",
                Title = "决战前夜下雨",
                Body = "决战前夜下雨",
                Status = NoteStatus.Inbox,
            }
        );
        return store;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Text(AgentToolResult result) =>
        string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));

    [Fact]
    public async Task MissingNovel_ReturnsError()
    {
        var tool = new ForecastPlotTool(new FakeNovelStore(), null, null, null);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"window":5}"""),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("没有打开的小说项目", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNovel_ReturnsError()
    {
        var tool = new ForecastPlotTool(new FakeNovelStore(), null, null, null);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"nope"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("找不到该小说", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOutline_ReturnsGuide()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(new Novel { Id = "n1", Title = "空书" });
        store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Order = 1,
                Content = "正文",
            }
        );
        var tool = new ForecastPlotTool(store, null, null, null);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(PlotForecastFormatter.NoPlanGuide, Text(result));
    }

    [Fact]
    public async Task Forecast_RendersBlockAndCounts()
    {
        var tool = new ForecastPlotTool(CreateStore(), null, null, null);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","window":3,"include-evidence":true}"""),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = Text(result);
        Assert.StartsWith("[远期推演]", text, StringComparison.Ordinal);
        Assert.Contains("到期义务：", text, StringComparison.Ordinal);
        Assert.Contains("笔记线索：", text, StringComparison.Ordinal);
        Assert.Contains("《半块玉的递法》第3章", text, StringComparison.Ordinal);
        Assert.Contains("笔记待确认：1 条", text, StringComparison.Ordinal);

        var details = Assert.IsType<ForecastPlotDetails>(result.Details);
        Assert.Equal(2, details.TargetOrder);
        Assert.Equal(1, details.DueCount);
        Assert.Equal(1, details.NoteCount);
        Assert.Equal(0, details.EvidenceCount); // 无内核 → 无证据
    }

    [Fact]
    public async Task IncludeEvidenceFalse_SkipsRetrievalAndNotes()
    {
        var tool = new ForecastPlotTool(CreateStore(), null, null, null);

        var result = await tool.ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","include-evidence":false}"""),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var details = Assert.IsType<ForecastPlotDetails>(result.Details);
        Assert.Equal(0, details.NoteCount);
        Assert.Equal(0, details.EvidenceCount);
        Assert.DoesNotContain("笔记线索：", Text(result), StringComparison.Ordinal);
    }
}
