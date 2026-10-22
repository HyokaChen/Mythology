using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 导出小说工具 + 写作统计工具测试。
/// </summary>
public class ExportAndStatsToolTests
{
    private static FakeNovelStore CreateStoreWithData()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "测试小说",
                Genre = "奇幻",
                Description = "描述",
                WritingStyle = "简洁",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Title = "序章",
                Order = 1,
                Content = "序章内容。",
                Status = ChapterStatus.Published,
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = "n1",
                Title = "第一章",
                Order = 2,
                Content = "第一章内容。",
                Status = ChapterStatus.Draft,
            }
        );
        store.SaveCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "n1",
                Name = "林默",
                Role = CharacterRole.Protagonist,
            }
        );
        store.SaveLocation(
            new Location
            {
                Id = "l1",
                NovelId = "n1",
                Name = "青云峰",
                Type = LocationType.Wilderness,
            }
        );
        store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "青云宗",
                Type = FactionType.Sect,
            }
        );
        return store;
    }

    // ═══════════════════════════════════════════════════════
    //  ExportNovelTool
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task ExportNovelTool_MarkdownFormat_ReturnsContent()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1","format":"markdown"}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("测试小说", text);
        Assert.Contains("序章", text);
    }

    [Fact]
    public async Task ExportNovelTool_TxtFormat_ReturnsContent()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1","format":"txt"}""").RootElement;
        var result = await tool.ExecuteAsync("call2", args, CancellationToken.None);

        Assert.False(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("测试小说", text);
    }

    [Fact]
    public async Task ExportNovelTool_JsonFormat_ReturnsValidJson()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1","format":"json"}""").RootElement;
        var result = await tool.ExecuteAsync("call3", args, CancellationToken.None);

        Assert.False(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("测试小说", text);
    }

    [Fact]
    public async Task ExportNovelTool_MissingNovel_ReturnsError()
    {
        var store = new FakeNovelStore();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"nonexistent"}""").RootElement;
        var result = await tool.ExecuteAsync("call4", args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExportNovelTool_ChapterRange_Filters()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument
            .Parse("""{"novel-id":"n1","format":"txt","chapter-range":[1,1]}""")
            .RootElement;
        var result = await tool.ExecuteAsync("call5", args, CancellationToken.None);

        Assert.False(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("序章", text);
        Assert.DoesNotContain("第一章", text);
    }

    [Fact]
    public async Task ExportNovelTool_NoAppendix_WhenDisabled()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument
            .Parse("""{"novel-id":"n1","format":"markdown","include-appendix":false}""")
            .RootElement;
        var result = await tool.ExecuteAsync("call6", args, CancellationToken.None);

        Assert.False(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.DoesNotContain("附录", text);
    }

    [Fact]
    public async Task ExportNovelTool_Details_Populated()
    {
        var store = CreateStoreWithData();
        var tool = new ExportNovelTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1","format":"markdown"}""").RootElement;
        var result = await tool.ExecuteAsync("call7", args, CancellationToken.None);

        // 通过反射访问 Details，因为基类 AgentToolResult 不暴露 TypedDetails
        var detailsProp = result.GetType().GetProperty("Details");
        Assert.NotNull(detailsProp);
        var details = detailsProp.GetValue(result);
        Assert.NotNull(details);
    }

    // ═══════════════════════════════════════════════════════
    //  GetWritingStatsTool
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task GetWritingStatsTool_Overview_ReturnsStats()
    {
        var store = CreateStoreWithData();
        var tool = new GetWritingStatsTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1"}""").RootElement;
        var result = await tool.ExecuteAsync("s1", args, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task GetWritingStatsTool_MissingNovel_ReturnsError()
    {
        var store = new FakeNovelStore();
        var tool = new GetWritingStatsTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"nonexistent"}""").RootElement;
        var result = await tool.ExecuteAsync("s2", args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task GetWritingStatsTool_NullScope_DefaultsToOverview()
    {
        var store = CreateStoreWithData();
        var tool = new GetWritingStatsTool(store);

        var args = JsonDocument.Parse("""{"novel-id":"n1"}""").RootElement;
        var result = await tool.ExecuteAsync("s3", args, CancellationToken.None);

        Assert.False(result.IsError);
    }

    // ═══════════════════════════════════════════════════════
    //  GetWritingStatsTool — tactics 档（双速自进化账本）
    // ═══════════════════════════════════════════════════════

    [Fact]
    public async Task GetWritingStatsTool_TacticsScope_WithoutLedger_ReturnsNotEnabled()
    {
        var tool = new GetWritingStatsTool(new FakeNovelStore());

        // 未装配账本：不触达 store（novel 不存在也不报错），返回「未启用」。
        var args = JsonDocument
            .Parse("""{"novel-id":"nonexistent","scope":"tactics"}""")
            .RootElement;
        var result = await tool.ExecuteAsync("s4", args, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("未启用", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task GetWritingStatsTool_TacticsScope_WithLedger_ListsWinRateAndUses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StatsTactics_" + Guid.NewGuid().ToString("N"));
        var ledgerPath = Path.Combine(dir, "evolution", "ledger.jsonl");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            File.WriteAllText(
                ledgerPath,
                """
                {"kind":"learn","ts":1,"tactic":"tt-1234abcd","trigger":"F-2","genre":"都市爽文","prescription":"引入对抗","boundary":"边界"}
                {"kind":"inject","ts":2,"novel":"n1","chapter":1,"tactic":"tt-1234abcd","trigger":"F-2"}
                {"kind":"verdict","ts":3,"novel":"n1","chapter":2,"tactic":"tt-1234abcd","outcome":"pass"}
                """
            );
            AtomicFile.ForgetPath(ledgerPath);
            var tool = new GetWritingStatsTool(new FakeNovelStore(), new TacticLedger(dir));

            // tactics 档不触达 store：novel-id 不存在也不报错。
            var args = JsonDocument
                .Parse("""{"novel-id":"nonexistent","scope":"tactics"}""")
                .RootElement;
            var result = await tool.ExecuteAsync("s5", args, CancellationToken.None);

            Assert.False(result.IsError);
            var text = ((TextContent)result.Content[0]).Text;
            Assert.Contains("tt-1234abcd", text);
            Assert.Contains("F-2", text);
            Assert.Contains("胜率", text);
            Assert.Contains("67%", text); // (1+1)/(1+0+2) ≈ 0.667
            Assert.Contains("1 次", text);
            Assert.Contains("Active", text);
        }
        finally
        {
            AtomicFile.ForgetPath(ledgerPath);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetWritingStatsTool_TacticsScope_EmptyLedger_ReturnsNoTactics()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "StatsTacticsEmpty_" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var tool = new GetWritingStatsTool(new FakeNovelStore(), new TacticLedger(dir));

            var args = JsonDocument.Parse("""{"scope":"tactics"}""").RootElement;
            var result = await tool.ExecuteAsync("s6", args, CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("尚无历史验证战术", ((TextContent)result.Content[0]).Text);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
