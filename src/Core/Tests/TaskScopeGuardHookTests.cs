using Core.Agent;
using Core.Data;
using Core.Tools;
using Xunit;
using AgentModel = Core.Agent.Agent;

namespace Core.Tests;

public sealed class TaskScopeGuardHookTests : IDisposable
{
    private const string NovelId = "n-hook";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"myth-scope-hook-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task BeforeToolCall_BlocksOutOfScopeWrite()
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
                Title = "Hook 测试",
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
            SessionId = "s-hook",
            CurrentNovelId = NovelId,
            ActiveTaskScope = new TaskScopeState
            {
                Mode = "revise",
                Label = "第2卷《夏夜的萤火》（第21-40章）",
                VolumeIds = ["v2"],
                ChapterOrders = Enumerable.Range(21, 20).ToList(),
                Instruction = "修正优化整个第二卷",
            },
        };
        var hook = new TaskScopeGuardHook(
            agent,
            novelStore,
            new FileSessionStore(storeDir, novelsRoot)
        );

        var result = await hook.BeforeToolCallAsync(
            new ToolHookContext
            {
                ToolName = "write_chapter",
                Arguments = $$"""{"novel-id":"{{NovelId}}","chapter-id":"54"}""",
            },
            CancellationToken.None
        );

        Assert.True(result.Block);
        Assert.Contains("第54章", result.Reason);
        Assert.Contains("第2卷《夏夜的萤火》", result.Reason);
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
