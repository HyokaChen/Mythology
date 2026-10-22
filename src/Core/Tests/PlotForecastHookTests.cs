using Core.Data;
using Core.Forecast;
using Core.Shared;
using Core.Tools;
using LlmTornado;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 远期推演钩子：触发工具后静默注入 <c>[远期推演]</c> 块；同状态指纹去重；错误/非触发/无小说不注入。
/// </summary>
public sealed class PlotForecastHookTests : IDisposable
{
    private const string NovelId = "hook-forecast";

    private readonly string _root;
    private readonly FileNovelStore _store;

    public PlotForecastHookTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"forecasthook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "novels"));
        Directory.CreateDirectory(Path.Combine(_root, "mythology"));
        _store = new FileNovelStore(
            Path.Combine(_root, "novels"),
            Path.Combine(_root, "mythology")
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    private static Core.Agent.Agent MakeAgent(string? novelId = NovelId)
    {
        var agent = new Core.Agent.Agent(
            new ToolRegistry(),
            new TornadoApi(LLmProviders.OpenAi, "test-key")
        );
        agent.CurrentNovelId = novelId;
        return agent;
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "钩子书" }, ct);
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Order = 1,
                Title = "第一章",
                Content = "正文",
            },
            ct
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = NovelId,
                Order = 2,
                Title = "第二章",
                Summary = "青铜钥匙归位",
            },
            ct
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "p1",
                NovelId = NovelId,
                Name = "青铜钥匙",
                PlannedRevealChapterId = "ch2",
            },
            ct
        );
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-1",
                NovelId = NovelId,
                Title = "半块玉",
                Body = "第2章 递出半块玉",
                Status = NoteStatus.Active,
            },
            ct
        );
    }

    private static ToolHookContext Context(string tool, bool isError = false) =>
        new()
        {
            ToolName = tool,
            Arguments = "{}",
            Result = new AgentToolResult
            {
                Content = [new TextContent(isError ? "失败" : "ok")],
                IsError = isError,
            },
        };

    [Fact]
    public async Task TriggerTool_InjectsForecastBlock()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var agent = MakeAgent();
        var hook = new PlotForecastHook(agent, _store);

        await hook.AfterToolCallAsync(Context("write_chapter"), ct);

        var queued = agent.InjectQueue.DequeueAllWithIds();
        var single = Assert.Single(queued);
        Assert.StartsWith(PlotForecastFormatter.Prefix, single.Message.Content);
        Assert.Contains("下一章：第2章", single.Message.Content, StringComparison.Ordinal);
        Assert.Contains("笔记线索：", single.Message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameState_DoesNotInjectTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var agent = MakeAgent();
        var hook = new PlotForecastHook(agent, _store);

        await hook.AfterToolCallAsync(Context("write_chapter"), ct);
        agent.InjectQueue.DequeueAllWithIds();
        await hook.AfterToolCallAsync(Context("write_chapter"), ct);

        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task NoteChange_ChangesFingerprint_AndInjectsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var agent = MakeAgent();
        var hook = new PlotForecastHook(agent, _store);

        await hook.AfterToolCallAsync(Context("write_chapter"), ct);
        agent.InjectQueue.DequeueAllWithIds();

        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-2",
                NovelId = NovelId,
                Title = "决战前夜",
                Body = "第2章 决战前夜下雨",
                Status = NoteStatus.Active,
            },
            ct
        );
        await hook.AfterToolCallAsync(Context("manage_note"), ct);

        var queued = agent.InjectQueue.DequeueAllWithIds();
        Assert.Contains(
            "决战前夜",
            Assert.Single(queued).Message.Content,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ErrorResult_NonTriggerTool_AndNoNovel_DoNotInject()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var errorAgent = MakeAgent();
        await new PlotForecastHook(errorAgent, _store).AfterToolCallAsync(
            Context("write_chapter", isError: true),
            ct
        );
        Assert.Empty(errorAgent.InjectQueue.DequeueAllWithIds());

        var otherAgent = MakeAgent();
        await new PlotForecastHook(otherAgent, _store).AfterToolCallAsync(
            Context("get_writing_stats"),
            ct
        );
        Assert.Empty(otherAgent.InjectQueue.DequeueAllWithIds());

        var noNovelAgent = MakeAgent(novelId: null);
        await new PlotForecastHook(noNovelAgent, _store).AfterToolCallAsync(
            Context("write_chapter"),
            ct
        );
        Assert.Empty(noNovelAgent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task NoPlanData_DoesNotInject()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "空书" }, ct);
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Order = 1,
                Title = "第一章",
                Content = "正文",
            },
            ct
        );
        var agent = MakeAgent();

        await new PlotForecastHook(agent, _store).AfterToolCallAsync(Context("write_chapter"), ct);

        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }
}
