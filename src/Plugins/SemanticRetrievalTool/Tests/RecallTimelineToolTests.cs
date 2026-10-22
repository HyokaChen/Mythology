using Core.Data;
using Core.Shared;
using Core.Vector;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

/// <summary>
/// recall_timeline 测试：章节范围过滤 + 词法优先（ConsistencyCheck profile）。
/// </summary>
public class RecallTimelineToolTests : SemanticRetrievalToolTestBase
{
    private readonly FakeNovelStore _novelStore = new();
    private readonly RecallTimelineTool _tool;

    public RecallTimelineToolTests()
    {
        _tool = new RecallTimelineTool(Kernel, new PluginStore(_novelStore));
    }

    [Fact]
    public async Task ExecuteAsync_NoNovelId_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallTimelineParams { NovelId = "" },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_ChapterRangeFiltersEvents()
    {
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = "novel1",
                Order = 2,
            }
        );
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch15",
                NovelId = "novel1",
                Order = 15,
            }
        );
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt1",
                NovelId = "novel1",
                Title = "林晚拜师",
                Description = "入门仪式",
                ChapterId = "ch2",
            }
        );
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt2",
                NovelId = "novel1",
                Title = "决战青云山",
                Description = "最终对决",
                ChapterId = "ch15",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallTimelineParams
            {
                NovelId = "novel1",
                ChapterMin = 1,
                ChapterMax = 5,
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("拜师", text);
        Assert.DoesNotContain("决战", text);
    }

    [Fact]
    public async Task ExecuteAsync_NoQuery_ReturnsAllEventsInOrder()
    {
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = "novel1",
                Order = 2,
            }
        );
        _novelStore.AddChapter(
            new Chapter
            {
                Id = "ch15",
                NovelId = "novel1",
                Order = 15,
            }
        );
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt1",
                NovelId = "novel1",
                Title = "林晚拜师",
                ChapterId = "ch2",
            }
        );
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt2",
                NovelId = "novel1",
                Title = "决战青云山",
                ChapterId = "ch15",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallTimelineParams { NovelId = "novel1" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("林晚拜师", text);
        Assert.Contains("决战青云山", text);
        // 按章节排序：拜师（第2章）在前
        Assert.True(
            text.IndexOf("拜师", StringComparison.Ordinal)
                < text.IndexOf("决战", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ExecuteAsync_CharacterNameFiltersLexically()
    {
        // 角色名查询走 hybrid 检索路径（query 非空），索引事件文档
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt1",
                NovelId = "novel1",
                Title = "林晚失踪",
            }
        );
        _novelStore.AddTimelineEvent(
            new TimelineEvent
            {
                Id = "evt2",
                NovelId = "novel1",
                Title = "沈一入宫",
            }
        );
        await IndexDocumentAsync(
            "evt1",
            DocumentKind.TimelineEvent,
            "事件: 林晚失踪",
            "novel1",
            "evt1",
            new Dictionary<string, object?> { ["chapter-order"] = 3 }
        );
        await IndexDocumentAsync(
            "evt2",
            DocumentKind.TimelineEvent,
            "事件: 沈一入宫",
            "novel1",
            "evt2",
            new Dictionary<string, object?> { ["chapter-order"] = 4 }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallTimelineParams { NovelId = "novel1", CharacterName = "林晚" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("林晚失踪", text);
        Assert.DoesNotContain("沈一入宫", text);
    }

    [Fact]
    public async Task ExecuteAsync_NoMatches_ReturnsNotFound()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallTimelineParams { NovelId = "novel1", Query = "不存在的关键词" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("未找到", GetText(result));
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
