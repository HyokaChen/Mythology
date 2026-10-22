using System.Text.Json;
using Core.Data;
using Core.Notes;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using Plugins.NovelTools.Tools;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace NovelTools.Tests;

/// <summary>
/// 笔记链路烟测（§Verification 8）：capture_note → manage_note list → 会话自动捕获候选
/// → manage_note confirm → forecast_plot 的「笔记待确认」随之消失。
/// </summary>
public sealed class NoteChainSmokeTests
{
    private static FakeNovelStore CreateStore()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "链路书",
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
        return store;
    }

    private static string Text(AgentToolResult result) =>
        string.Concat(result.Content.OfType<TextContent>().Select(c => c.Text));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task CaptureScanConfirm_ForecastStopsAsking()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        // 1) 工具显式记一笔（已确认，带「第2章」锚点）
        var capture = await new CaptureNoteTool(store).ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","body":"第2章 让北境使者递出半块玉"}"""),
            ct
        );
        Assert.False(capture.IsError);

        // 2) 列表能看到它
        var list = await new ManageNoteTool(store).ExecuteAsync(
            "call",
            Json("""{"novel-id":"n1","action":"list"}"""),
            ct
        );
        Assert.Contains("第2章 让北境使者递出半块玉", Text(list), StringComparison.Ordinal);

        // 3) 会话里的「记一下」→ 收件箱候选
        var added = await new NoteCaptureScanner(store).ScanAsync(
            "n1",
            "sess-smoke",
            [new LlmChatMessage(ChatMessageRoles.User, "记一下：决战前夜让北境下整夜的雨")],
            0,
            capturedChapterOrder: 1,
            ct
        );
        Assert.Equal(1, added);

        // 4) 候选先不参与推演
        var forecastTool = new ForecastPlotTool(store, null, null, null);
        var before = await forecastTool.ExecuteAsync("call", Json("""{"novel-id":"n1"}"""), ct);
        Assert.Contains("笔记待确认：1 条", Text(before), StringComparison.Ordinal);
        Assert.Equal(1, Assert.IsType<ForecastPlotDetails>(before.Details).NoteCount);

        // 5) 确认候选
        var inbox = (await store.GetNotes("n1", ct)).Single(n => n.Status == NoteStatus.Inbox);
        var confirm = await new ManageNoteTool(store).ExecuteAsync(
            "call",
            Json($$"""{"novel-id":"n1","action":"confirm","note-id":"{{inbox.Id}}"}"""),
            ct
        );
        Assert.False(confirm.IsError);

        // 6) 推演块不再提示待确认
        var after = await forecastTool.ExecuteAsync("call", Json("""{"novel-id":"n1"}"""), ct);
        Assert.DoesNotContain("笔记待确认", Text(after), StringComparison.Ordinal);
        Assert.Contains(
            "《第2章 让北境使者递出半块玉》第2章",
            Text(after),
            StringComparison.Ordinal
        );
    }
}
