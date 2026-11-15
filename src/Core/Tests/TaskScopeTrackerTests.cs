using Core.Agent;
using Core.Data;
using Core.Tools;
using Xunit;
using AgentModel = Core.Agent.Agent;

namespace Core.Tests;

public sealed class TaskScopeTrackerTests : IDisposable
{
    private const string NovelId = "n-tracker";
    private const string SessionId = "s-tracker";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"myth-scope-tracker-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task TaskLevelInstructionReplacesScope_OneOffDoesNot()
    {
        var (agent, novelStore, sessionStore) = await SeedAsync();

        await TaskScopeTracker.ObserveAuthorMessageAsync(
            agent,
            sessionStore,
            novelStore,
            "修正优化整个第二卷 分卷[ID:v2]:夏夜的萤火 的所有章节内容！！"
        );

        Assert.NotNull(agent.ActiveTaskScope);
        Assert.Contains("第2卷", agent.ActiveTaskScope!.Label);
        Assert.Contains(21, agent.ActiveTaskScope.ChapterOrders);
        Assert.Contains(40, agent.ActiveTaskScope.ChapterOrders);

        await TaskScopeTracker.ObserveAuthorMessageAsync(
            agent,
            sessionStore,
            novelStore,
            "把第54章第三段改一下"
        );

        Assert.NotNull(agent.ActiveTaskScope);
        Assert.Contains(21, agent.ActiveTaskScope!.ChapterOrders);
        Assert.Equal("c54", agent.TurnTargetChapterId);
        var persisted = await sessionStore.GetTaskScopeAsync(SessionId);
        Assert.NotNull(persisted);
        Assert.Contains(21, persisted!.ChapterOrders);
    }

    [Fact]
    public async Task CancelInstructionClearsPersistedScope()
    {
        var (agent, novelStore, sessionStore) = await SeedAsync();
        await TaskScopeTracker.ObserveAuthorMessageAsync(
            agent,
            sessionStore,
            novelStore,
            "修正优化整个第二卷 分卷[ID:v2]:夏夜的萤火 的所有章节内容！！"
        );
        Assert.NotNull(await sessionStore.GetTaskScopeAsync(SessionId));

        await TaskScopeTracker.ObserveAuthorMessageAsync(
            agent,
            sessionStore,
            novelStore,
            "取消卷二任务"
        );

        Assert.Null(agent.ActiveTaskScope);
        Assert.Null(await sessionStore.GetTaskScopeAsync(SessionId));
    }

    private async Task<(
        AgentModel Agent,
        FileNovelStore NovelStore,
        FileSessionStore SessionStore
    )> SeedAsync()
    {
        var novelsRoot = Path.Combine(_root, "novels");
        var storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(novelsRoot);
        Directory.CreateDirectory(storeDir);

        var novelStore = new FileNovelStore(novelsRoot, storeDir);
        await novelStore.SaveNovel(
            new Novel
            {
                Id = NovelId,
                NovelId = NovelId,
                Title = "范围测试",
                Genre = "玄幻",
                WritingStyle = "冷峻",
            }
        );
        await novelStore.SaveVolume(
            new Volume
            {
                Id = "v2",
                NovelId = NovelId,
                Name = "夏夜的萤火",
                Order = 2,
            }
        );
        await novelStore.SaveVolume(
            new Volume
            {
                Id = "v3",
                NovelId = NovelId,
                Name = "第三卷",
                Order = 3,
            }
        );
        for (var order = 21; order <= 40; order++)
        {
            await novelStore.SaveChapter(
                new Chapter
                {
                    Id = $"c{order}",
                    NovelId = NovelId,
                    Title = $"萤火{order}",
                    Order = order,
                    VolumeId = "v2",
                }
            );
        }
        await novelStore.SaveChapter(
            new Chapter
            {
                Id = "c54",
                NovelId = NovelId,
                Title = "恶意的瞥见",
                Order = 54,
                VolumeId = "v3",
            }
        );

        var agent = new AgentModel(new ToolRegistry())
        {
            SessionId = SessionId,
            CurrentNovelId = NovelId,
        };
        var sessionStore = new FileSessionStore(storeDir, novelsRoot);
        return (agent, novelStore, sessionStore);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}
