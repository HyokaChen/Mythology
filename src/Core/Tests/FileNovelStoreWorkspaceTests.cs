using Core.Data;
using Core.Shared;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// FileNovelStore 多目录（工作区）行为：读取 = 各目录并集，写入 = ActiveRoot，
/// 清单变化 = 索引失效。这是「统一项目列表」的数据层地基。
/// </summary>
public sealed class FileNovelStoreWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wsstore-{Guid.NewGuid():N}");
    private readonly string _mythologyDir;
    private readonly string _dirA;
    private readonly string _dirB;

    public FileNovelStoreWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        _mythologyDir = Path.Combine(_root, ".mythology");
        _dirA = MakeProjectDir("a");
        _dirB = MakeProjectDir("b");
    }

    private string MakeProjectDir(string name)
    {
        var path = Path.Combine(_root, name, MythologyPaths.NovelsSubDir);
        Directory.CreateDirectory(path);
        return path;
    }

    private NovelWorkspace Workspace(string? active = null)
    {
        var ws = NovelWorkspace.Single(active ?? _dirA);
        ws.Add(_dirB);
        return ws;
    }

    private static Novel Novel(string title) =>
        new()
        {
            Title = title,
            Genre = "东方玄幻",
            Description = "",
            WritingStyle = "",
        };

    [Fact]
    public async Task GetAllNovels_ReturnsUnionAcrossProjectDirectories()
    {
        var storeA = new FileNovelStore(Workspace(), _mythologyDir);
        await storeA.SaveNovel(Novel("甲书"));

        var storeB = new FileNovelStore(Workspace(_dirB), _mythologyDir);
        await storeB.SaveNovel(Novel("乙书"));

        var union = await storeA.GetAllNovels();

        Assert.Equal(2, union.Count);
        Assert.Contains("甲书", union.Select(n => n.Title));
        Assert.Contains("乙书", union.Select(n => n.Title));
    }

    [Fact]
    public async Task SaveNovel_NewNovel_LandsInActiveRoot_ExistingNovel_KeepsItsOwnRoot()
    {
        var workspace = Workspace(_dirB);
        var store = new FileNovelStore(workspace, _mythologyDir);
        var novel = Novel("落在B");
        await store.SaveNovel(novel);

        Assert.Equal(_dirB, Directory.GetParent(store.FindNovelDirectory(novel.Id)!)!.FullName);

        // 切活动目录不影响已有书的归属：更新仍写回原目录
        workspace.SetActive(_dirA);
        novel.Description = "改一笔";
        await store.SaveNovel(novel);

        Assert.Equal(_dirB, Directory.GetParent(store.FindNovelDirectory(novel.Id)!)!.FullName);
        var fresh = await store.GetNovel(novel.Id);
        Assert.Equal("改一笔", fresh!.Description);
    }

    [Fact]
    public async Task AddingDirectory_InvalidatesIndex_WithoutRecreatingStore()
    {
        var workspace = Workspace();
        var store = new FileNovelStore(workspace, _mythologyDir);
        await store.SaveNovel(Novel("甲书"));
        Assert.Single(await store.GetAllNovels());

        var dirC = MakeProjectDir("c");
        var otherStore = new FileNovelStore(NovelWorkspace.Single(dirC), _mythologyDir);
        await otherStore.SaveNovel(Novel("丙书"));

        workspace.Add(dirC); // 运行期添加目录 → 同一 store 实例立刻看见

        Assert.Equal(2, (await store.GetAllNovels()).Count);
    }

    [Fact]
    public async Task SameNovelIdInTwoRoots_ResolvesToActiveRoot()
    {
        var novel = Novel("同一本书");
        var inA = new FileNovelStore(Workspace(_dirA), _mythologyDir);
        await inA.SaveNovel(novel);
        // 手工复制这本书到 B（模拟同一项目目录被复制到另一处）
        var source = inA.FindNovelDirectory(novel.Id)!;
        var copy = Path.Combine(_dirB, Path.GetFileName(source));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), true);

        var store = new FileNovelStore(Workspace(_dirB), _mythologyDir);

        Assert.Equal(copy, store.FindNovelDirectory(novel.Id));
        Assert.Single(await store.GetAllNovels()); // 并集按 id 去重，列表不出现两遍
    }

    [Fact]
    public async Task SessionLookup_SpansAllRoots()
    {
        // 会话目录名按「小说标题」编码，标题缓存来自小说目录 → 必须跨全部已登记目录建索引，
        // 否则书在 B 目录时，会话会被当成无主会话看不见。
        var workspace = Workspace(_dirB);
        var store = new FileNovelStore(workspace, _mythologyDir);
        var novel = Novel("乙书");
        await store.SaveNovel(novel);

        var sessions = new FileSessionStore(workspace, _mythologyDir);
        await sessions.SaveSessionMessages(
            "s-1",
            novel.Id,
            [new LlmChatMessage(ChatMessageRoles.User, "开场白")]
        );

        var found = await sessions.GetSessions(novel.Id);
        Assert.Single(found);
        Assert.Equal("s-1", found[0].Id);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
