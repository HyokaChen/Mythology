using Core.Data;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudNovelSyncService 合并规则引擎测试（stub client + 临时 novelsRoot/mythologyDir）。
/// 覆盖计划 B6 场景：①首次全量 push ②远端更新本地未改 → pull ③双方都改 → 冲突副本
/// ④本地删 → 远端删除传播 ⑤远端删 → 本地删除传播 ⑥pullOnly 不 push
/// ⑦state 损坏按空处理 ⑧目录结构保真（pull 落盘逐文件一致）⑨base64 二进制往返
/// ⑩忽略清单 ⑪重复 key 降级 ⑫重命名三路判定（远端跟随/本地 push/双方都改本地为主）
/// ⑬崩溃注入（push 后 state 未存 → 幂等重放不产生冲突副本）。
/// </summary>
public sealed class CloudNovelSyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"cloud-sync-test-{Guid.NewGuid():N}"
    );
    private readonly string _novelsRoot;
    private readonly string _mythologyDir;
    private readonly StubSyncClient _client = new();

    public CloudNovelSyncServiceTests()
    {
        _novelsRoot = Path.Combine(_root, "novels");
        _mythologyDir = Path.Combine(_root, "mythology");
        Directory.CreateDirectory(_novelsRoot);
        Directory.CreateDirectory(_mythologyDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private CloudNovelSyncService CreateService(NovelWorkspace? workspace = null) =>
        new(
            _client,
            new CloudSyncStateStore(_mythologyDir),
            workspace ?? NovelWorkspace.Single(_novelsRoot),
            _mythologyDir
        );

    private const string NovelId = "nv-001";

    private string CreateNovelFile(string relativePath, string content, bool base64 = false)
    {
        var path = Path.Combine(
            _novelsRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (base64)
        {
            File.WriteAllBytes(path, Convert.FromBase64String(content));
        }
        else
        {
            File.WriteAllText(path, content);
        }
        return path;
    }

    private static SyncDocumentMetaDto Meta(
        string type,
        string entityId,
        int revision,
        string sha,
        string relativePath
    ) => new(type, entityId, revision, sha, relativePath, DateTimeOffset.UtcNow);

    private static SyncDocumentDto Full(
        string type,
        string entityId,
        int revision,
        string sha,
        string relativePath,
        string content,
        string encoding = "utf8"
    ) =>
        new(
            type,
            entityId,
            revision,
            sha,
            "标题",
            relativePath,
            encoding,
            content,
            DateTimeOffset.UtcNow
        );

    private static readonly string ChapterContent = "---\nid: ch1\ntitle: 第一章\n---\n内容";

    private static string ShaOf(string content) =>
        Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content)
                )
            )
            .ToLowerInvariant();

    // ── ① 首次 sync（远端空）→ 全部 push、state 落盘 ──

    // ── 项目目录清单（workspace）语义：根未确定拒绝同步 / 跨目录落点 ──

    [Fact]
    public async Task Sync_WithUnspecifiedRoot_IsRefused()
    {
        // 猜出的空目录 = 根未指定（Desktop 打包后 cwd 不可控的真实风险）→ 宁可拒绝，不落到猜出的目录
        var workspace = new NovelWorkspace(_mythologyDir, [_novelsRoot], _novelsRoot);
        Assert.False(workspace.HasSpecifiedRoot);
        var service = CreateService(workspace);

        var exception = await Assert.ThrowsAnyAsync<CloudApiException>(() =>
            service.SyncAsync(NovelId)
        );
        Assert.Equal("novels_root_undetermined", exception.Code);
    }

    [Fact]
    public async Task SyncAll_WithUnspecifiedRoot_IsRefused()
    {
        var workspace = new NovelWorkspace(_mythologyDir);
        var service = CreateService(workspace);

        var exception = await Assert.ThrowsAnyAsync<CloudApiException>(() =>
            service.SyncAllAsync()
        );
        Assert.Equal("novels_root_undetermined", exception.Code);
    }

    [Fact]
    public async Task Sync_ResolvesTheNovelOwnRoot_NotTheActiveRoot()
    {
        // 书在 B 目录，活动目录是 A → 同步必须读 B，而不是把 B 的书当成「本地全缺失」
        var rootB = Path.Combine(_root, "novels-b");
        Directory.CreateDirectory(rootB);
        var workspace = NovelWorkspace.Single(_novelsRoot); // A = active
        workspace.Add(rootB);
        var novelDir = Path.Combine(rootB, NovelId);
        Directory.CreateDirectory(Path.Combine(novelDir, "chapters"));
        File.WriteAllText(Path.Combine(novelDir, "novel.yaml"), $"id: {NovelId}\ntitle: 在B的书\n");
        File.WriteAllText(Path.Combine(novelDir, "chapters", "ch001.md"), "在 B 目录的正文");

        var service = CreateService(workspace);
        var report = await service.SyncAsync(NovelId);

        Assert.Equal(2, report.Pushed); // novel.yaml + ch001.md —— 从 B 找到本地文档并 push
        Assert.Equal(0, report.Conflicts);
    }

    [Fact]
    public void DiscoverNovels_AcrossRoots_UnifiedAndDeduped()
    {
        // 同一本书复制到两个目录 → 统一列表只出现一次；另一本书照常出现
        var rootB = Path.Combine(_root, "novels-b");
        Directory.CreateDirectory(rootB);
        CreateNovelFile($"{NovelId}/novel.yaml", $"id: {NovelId}\ntitle: 甲\n");
        var copied = Path.Combine(rootB, NovelId);
        Directory.CreateDirectory(copied);
        File.WriteAllText(Path.Combine(copied, "novel.yaml"), $"id: {NovelId}\ntitle: 甲\n");
        CreateNovelFile("nv-002/novel.yaml", "id: nv-002\ntitle: 乙\n");

        var novels = SyncDocumentEnumerator.DiscoverNovels([_novelsRoot, rootB]);

        Assert.Equal(2, novels.Count);
    }

    [Fact]
    public async Task FirstSync_PushesAllFiles_AndSavesState()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\ntitle: 我的小说\n");
        CreateNovelFile("我的小说/chapters/ch001.md", "---\nid: ch1\ntitle: 第一章\n---\n正文");
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(2, report.Pushed);
        Assert.Equal(0, report.Conflicts);
        var state = await new CloudSyncStateStore(_mythologyDir).LoadAsync(NovelId);
        Assert.Equal(2, state.Documents.Count);
        Assert.Contains("novel:self", state.Documents.Keys);
        Assert.Equal(1, state.Documents["novel:self"].Revision);
    }

    // ── ② 远端有更新且本地未改 → pull 覆盖 ──

    [Fact]
    public async Task RemoteChanged_LocalUnchanged_PullOverwrites()
    {
        var remoteContent = "远端改过的正文";
        CreateNovelFile(
            "我的小说/chapters/ch001.md",
            "---\nid: ch1\ntitle: 第一章\n---\n本地旧正文"
        );
        _client.RemoteDocuments["novel.yaml"] = Full(
            "novel",
            "self",
            1,
            ShaOf("id: nv-001\ntitle: 我的小说\n"),
            "我的小说/novel.yaml",
            "id: nv-001\ntitle: 我的小说\n"
        );
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            2,
            ShaOf(remoteContent),
            "我的小说/chapters/ch001.md",
            remoteContent
        );
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(2, report.Pulled);
        Assert.Equal(
            "远端改过的正文",
            File.ReadAllText(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"))
        );
    }

    // ── ③ 本地改 + 远端同文档已变 → 冲突副本、本地保留 ──

    [Fact]
    public async Task BothChanged_WritesConflictCopy_KeepsLocal()
    {
        var localContent = "本地新稿";
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile(
            "我的小说/chapters/ch001.md",
            "---\nid: ch1\ntitle: 第一章\n---\n" + localContent
        );
        var remoteContent = "远端新稿";
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            5,
            ShaOf(remoteContent),
            "我的小说/chapters/ch001.md",
            remoteContent
        );
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(1, report.Conflicts);
        Assert.Equal(
            "---\nid: ch1\ntitle: 第一章\n---\n" + localContent,
            File.ReadAllText(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"))
        );
        var conflictDir = Path.Combine(_mythologyDir, "cloud-conflicts", NovelId);
        var conflictFile = Assert.Single(
            Directory.GetFiles(conflictDir, "*", SearchOption.AllDirectories)
        );
        Assert.Contains("ch001.md.conflict-", Path.GetFileName(conflictFile));
        Assert.Equal(remoteContent, File.ReadAllText(conflictFile));
        // 冲突后 state 基线 = 远端真值（下次 sync 判「远端未改，本地改」→ push 本地稿，而非误判 pull 丢稿）
        var state = await new CloudSyncStateStore(_mythologyDir).LoadAsync(NovelId);
        Assert.Equal(ShaOf(remoteContent), state.Documents["chapter:ch1"].Sha256);
        Assert.Equal(5, state.Documents["chapter:ch1"].Revision);
        // 本地路径保留（push 时以此为落点）
        Assert.Equal("我的小说/chapters/ch001.md", state.Documents["chapter:ch1"].Path);
    }

    // ── ③b 冲突后的第二轮：本地稿必须被 push（修复静默覆盖丢稿 bug 的回归钉）──

    [Fact]
    public async Task SecondSyncAfterConflict_PushesLocalDraft_DoesNotOverwrite()
    {
        var localContent = "本地新稿";
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile(
            "我的小说/chapters/ch001.md",
            "---\nid: ch1\ntitle: 第一章\n---\n" + localContent
        );
        var remoteContent = "远端新稿";
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            5,
            ShaOf(remoteContent),
            "我的小说/chapters/ch001.md",
            remoteContent
        );
        var service = CreateService();

        // 第一轮：冲突 → 本地保留 + 冲突副本
        var first = await service.SyncAsync(NovelId);
        Assert.Equal(1, first.Conflicts);

        // 第二轮（作者没做任何合并）：本地稿必须被 push 上云端，而非被 pull 覆盖
        _client.PushCalls.Clear();
        var second = await service.SyncAsync(NovelId);
        var chapterPushes = _client
            .PushCalls.SelectMany(c => c)
            .Where(i => i.DocumentType == "chapter")
            .ToList();
        Assert.Single(chapterPushes);
        Assert.Contains(localContent, chapterPushes[0].Content);
        Assert.Equal(5, chapterPushes[0].BaseRevision); // 以冲突时云端 revision 为基线
        Assert.Equal(0, second.Conflicts);
        // 本地稿原样保留
        Assert.Contains(
            localContent,
            File.ReadAllText(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"))
        );
    }

    // ── ④ 本地删文件 → 远端删除传播 ──

    [Fact]
    public async Task LocalDeleted_RemoteUnchanged_PropagatesDelete()
    {
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            1,
            ShaOf(ChapterContent.Replace("内容", "旧稿")),
            "我的小说/chapters/ch001.md",
            ChapterContent.Replace("内容", "旧稿")
        );
        var service = CreateService();
        await service.SyncAsync(NovelId); // bootstrap：拉回本地
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md")));

        File.Delete(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"));
        var report = await service.SyncAsync(NovelId);

        Assert.Equal(1, report.Deleted);
        Assert.Equal("deleted", _client.DeleteResults.Single().Status);
    }

    [Fact]
    public async Task DeleteUnknownStatus_IsSkipped_AndBaselineRetainedForRetry()
    {
        // 服务端未来扩展 delete 结果状态（未知状态）不得静默吞掉：
        // 计入 skipped、基线保留 → 下一次 sync 重试删除。
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            1,
            ShaOf(ChapterContent),
            "我的小说/chapters/ch001.md",
            ChapterContent
        );
        var service = CreateService();
        await service.SyncAsync(NovelId); // bootstrap：拉回本地 + 建立 state 基线
        File.Delete(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"));

        _client.DeleteStatusOverride = "throttled"; // 未知状态
        var report = await service.SyncAsync(NovelId);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Skipped);
        Assert.Equal("throttled", _client.DeleteResults.Single().Status);

        // 恢复常态 → 下一次 sync 重试删除成功（基线未被丢弃）
        _client.DeleteStatusOverride = null;
        var retry = await service.SyncAsync(NovelId);
        Assert.Equal(1, retry.Deleted);
        Assert.Equal(0, retry.Skipped);
    }

    // ── ⑤ 远端删文件 → 本地删除传播（本地未改）──

    [Fact]
    public async Task RemoteDeleted_LocalUnchanged_DeletesLocalFile()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/chapters/ch001.md", ChapterContent.Replace("内容", "旧稿"));
        _client.RemoteDocuments["novel.yaml"] = Full(
            "novel",
            "self",
            1,
            ShaOf("id: nv-001\n"),
            "我的小说/novel.yaml",
            "id: nv-001\n"
        );
        var service = CreateService();

        // 第一次 sync 建立 state（novel 与远端一致收敛；仅 ch1 被推送）
        var first = await service.SyncAsync(NovelId);
        Assert.Equal(1, first.Pushed);

        // 远端删除 ch1（stub push 后 entity-id 键与测试键并存 → 按 entityId 清除）
        foreach (
            var stale in _client
                .RemoteDocuments.Where(kv => kv.Value.EntityId == "ch1")
                .Select(kv => kv.Key)
                .ToList()
        )
        {
            _client.RemoteDocuments.Remove(stale);
        }
        var report = await service.SyncAsync(NovelId);

        Assert.Equal(1, report.Deleted);
        Assert.False(File.Exists(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md")));
    }

    // ── ⑥ pullOnly 不 push ──

    [Fact]
    public async Task PullOnly_NeverPushes()
    {
        CreateNovelFile("我的小说/chapters/local-only.md", "本地新文件");
        var service = CreateService();

        var report = await service.SyncAsync(NovelId, pullOnly: true);

        Assert.Equal(0, report.Pushed);
        Assert.Empty(_client.PushCalls);
    }

    // ── ⑦ state 损坏 → 按空处理不抛 ──

    [Fact]
    public async Task CorruptedStateFile_TreatedAsEmpty()
    {
        var syncDir = Path.Combine(_mythologyDir, "novel-sync");
        Directory.CreateDirectory(syncDir);
        await File.WriteAllTextAsync(Path.Combine(syncDir, "nv-001.json"), "{ not valid json !!");
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        var service = CreateService();

        var exception = await Record.ExceptionAsync(() => service.SyncAsync(NovelId));

        Assert.Null(exception);
        Assert.Equal(1, _client.PushCalls.Sum(c => c.Count)); // 损坏 state 按空处理 → 全量 push
    }

    // ── ⑧ 目录结构保真：pull 落盘逐文件相对路径一致 ──

    [Fact]
    public async Task Bootstrap_PreservesDirectoryStructure()
    {
        _client.RemoteDocuments["novel.yaml"] = Full(
            "novel",
            "self",
            1,
            "h1",
            "我的小说/novel.yaml",
            "id: nv-001\n"
        );
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            1,
            "h2",
            "我的小说/chapters/ch001.md",
            "第一章"
        );
        _client.RemoteDocuments["loc.yaml"] = Full(
            "location",
            "loc-1",
            1,
            "h3",
            "我的小说/locations/昆仑山.yaml",
            "id: loc-1\nname: 昆仑山"
        );
        _client.RemoteDocuments["rel.yaml"] = Full(
            "relationships",
            "self",
            1,
            "h4",
            "我的小说/relationships.yaml",
            "relationships: []"
        );
        _client.RemoteDocuments["logo.png"] = Full(
            "file",
            "我的小说/素材/logo.png",
            1,
            "h5",
            "我的小说/素材/logo.png",
            "AAECAw==",
            "base64"
        );
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(5, report.Pulled);
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "novel.yaml")));
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md")));
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "locations", "昆仑山.yaml")));
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "relationships.yaml")));
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x02, 0x03 },
            File.ReadAllBytes(Path.Combine(_novelsRoot, "我的小说", "素材", "logo.png"))
        );
    }

    // ── ⑩ 忽略清单 ──

    [Fact]
    public async Task IgnoredFiles_AreNotEnumeratedOrPushed()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/.DS_Store", "junk");
        CreateNovelFile("我的小说/chapters/~$draft.md", "office tmp");
        CreateNovelFile("我的小说/notes.tmp", "tmp");
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(1, report.Pushed); // 仅 novel.yaml
        var pushedPaths = _client.PushCalls.SelectMany(c => c).Select(i => i.RelativePath).ToList();
        Assert.DoesNotContain(pushedPaths, p => p.Contains(".DS_Store"));
        Assert.DoesNotContain(pushedPaths, p => p.Contains("~$"));
        Assert.DoesNotContain(pushedPaths, p => p.EndsWith(".tmp"));
    }

    // ── ⑪ 重复 key 降级 file 兜底 ──

    [Fact]
    public async Task DuplicateChapterId_SecondDemotedToFile()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/chapters/a.md", "---\nid: dup\ntitle: A\n---\n甲");
        CreateNovelFile("我的小说/chapters/b.md", "---\nid: dup\ntitle: B\n---\n乙");
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(3, report.Pushed);
        var paths = _client.PushCalls.SelectMany(c => c).Select(i => i.RelativePath).ToList();
        Assert.Contains("我的小说/chapters/a.md", paths);
        Assert.Contains("我的小说/chapters/b.md", paths);
        // a.md 为 typed chapter（路径排序第一），b.md 降级 file
        var a = _client.PushCalls.SelectMany(c => c).First(i => i.RelativePath.EndsWith("a.md"));
        var b = _client.PushCalls.SelectMany(c => c).First(i => i.RelativePath.EndsWith("b.md"));
        Assert.Equal("chapter", a.DocumentType);
        Assert.Equal("file", b.DocumentType);
    }

    // ── ⑫ 重命名三路判定 ──

    [Fact]
    public async Task Rename_RemoteRenamed_FollowsRemotePathWithoutPush()
    {
        // state 基线：ch1 @ 旧路径 rev3
        var stateStore = new CloudSyncStateStore(_mythologyDir);
        var state = new CloudSyncNovelState(
            1,
            new Dictionary<string, CloudSyncDocState>
            {
                ["chapter:ch1"] = new(3, ShaOf(ChapterContent), "我的小说/chapters/ch001.md"),
            }
        );
        await stateStore.SaveAsync(NovelId, state);
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/chapters/ch001.md", ChapterContent);
        // 远端：同内容 rev4，路径已改
        _client.RemoteDocuments["ch002.md"] = Full(
            "chapter",
            "ch1",
            4,
            ShaOf(ChapterContent),
            "我的小说/chapters/ch002.md",
            ChapterContent
        );
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        Assert.Empty(_client.PushCalls.Where(c => c.Any(i => i.DocumentType == "chapter"))); // 远端改名 → 章节不 push
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch002.md")));
        Assert.False(File.Exists(Path.Combine(_novelsRoot, "我的小说", "chapters", "ch001.md"))); // 旧路径未改 → 清理
        var after = await stateStore.LoadAsync(NovelId);
        Assert.Equal("我的小说/chapters/ch002.md", after.Documents["chapter:ch1"].Path);
    }

    [Fact]
    public async Task Rename_LocalRenamed_PushesNewPath()
    {
        var stateStore = new CloudSyncStateStore(_mythologyDir);
        await stateStore.SaveAsync(
            NovelId,
            new CloudSyncNovelState(
                1,
                new Dictionary<string, CloudSyncDocState>
                {
                    ["chapter:ch1"] = new(3, ShaOf(ChapterContent), "我的小说/chapters/ch001.md"),
                }
            )
        );
        // 本地已改名到 ch003（内容未变）
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/chapters/ch003.md", ChapterContent);
        _client.RemoteDocuments["ch001.md"] = Full(
            "chapter",
            "ch1",
            3,
            ShaOf(ChapterContent),
            "我的小说/chapters/ch001.md",
            ChapterContent
        );
        var service = CreateService();

        var report = await service.SyncAsync(NovelId);

        var chapterPushes = _client
            .PushCalls.SelectMany(c => c)
            .Where(i => i.DocumentType == "chapter")
            .ToList();
        Assert.Single(chapterPushes); // 本地改名 → push 本地路径
        Assert.Equal("我的小说/chapters/ch003.md", chapterPushes[0].RelativePath);
        Assert.Equal(3, chapterPushes[0].BaseRevision);
    }

    [Fact]
    public async Task Rename_BothRenamed_LocalPathWins_NoPingPong()
    {
        var stateStore = new CloudSyncStateStore(_mythologyDir);
        await stateStore.SaveAsync(
            NovelId,
            new CloudSyncNovelState(
                1,
                new Dictionary<string, CloudSyncDocState>
                {
                    ["chapter:ch1"] = new(3, ShaOf(ChapterContent), "我的小说/chapters/ch001.md"),
                }
            )
        );
        // 本地改名 → ch003；远端改名 → ch002（内容一致）
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        CreateNovelFile("我的小说/chapters/ch003.md", ChapterContent);
        _client.RemoteDocuments["ch002.md"] = Full(
            "chapter",
            "ch1",
            4,
            ShaOf(ChapterContent),
            "我的小说/chapters/ch002.md",
            ChapterContent
        );
        var service = CreateService();

        await service.SyncAsync(NovelId);

        // push 的是本地路径 ch003 → 第二次 sync 云端 path == 本地 path，收敛不再翻转
        var push = _client.PushCalls.SelectMany(c => c).Single(i => i.DocumentType == "chapter");
        Assert.Equal("我的小说/chapters/ch003.md", push.RelativePath);
        var after = await stateStore.LoadAsync(NovelId);
        Assert.Equal("我的小说/chapters/ch003.md", after.Documents["chapter:ch1"].Path);

        _client.PushCalls.Clear();
        var second = await service.SyncAsync(NovelId);
        Assert.Empty(_client.PushCalls); // 收敛：无翻转
        Assert.Equal(0, second.Pushed);
    }

    // ── ⑬ 崩溃注入：push accepted 后 state 未存 → 幂等重放 ──

    [Fact]
    public async Task CrashAfterPush_ReplayIsIdempotent_NoDuplicateConflict()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        var service = CreateService();

        // 第一次 sync：push accepted 但不保存 state（模拟崩溃）
        _client.SaveState = false;
        await service.SyncAsync(NovelId);
        Assert.Single(_client.PushCalls);

        // 第二次 sync：state 为空 → 再次 push 同内容 → 服务端 sha 幂等 → accepted（云端不重复）
        _client.SaveState = true;
        var report = await service.SyncAsync(NovelId);
        Assert.Equal(1, report.Pushed);
        Assert.All(
            _client.PushCalls.SelectMany(c => c),
            item => Assert.Equal("accepted", _client.PushResults[item.EntityId].Status)
        );
        Assert.Equal(0, report.Conflicts);
    }

    // ── 远端重置保护 ──

    [Fact]
    public async Task RemoteReset_LocalFilesRepushed_NotDeleted()
    {
        // 先建立 state
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        var service = CreateService();
        await service.SyncAsync(NovelId);
        _client.RemoteDocuments.Clear(); // 云端命名空间被重置

        var report = await service.SyncAsync(NovelId);

        Assert.Equal(1, report.Pushed); // 重新 push，而非删除本地
        Assert.Equal(0, report.Deleted);
        Assert.True(File.Exists(Path.Combine(_novelsRoot, "我的小说", "novel.yaml")));
    }

    // ── ⑭ novelsRoot 漂移保护 ──

    [Fact]
    public async Task NovelsRootDrift_SkipsDeletePropagation()
    {
        // 先建立 state（本地目录存在）
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        var service = CreateService();
        await service.SyncAsync(NovelId);

        // 模拟 cwd 漂移：整本小说目录从 novelsRoot 消失（换目录启动），云端仍有文档
        Directory.Delete(Path.Combine(_novelsRoot, "我的小说"), recursive: true);

        var report = await service.SyncAsync(NovelId);

        // 不允许触发删除传播（否则云端被清空）；报告零删除零冲突
        Assert.Empty(_client.DeleteCalls);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(0, report.Conflicts);
        // 本地文件其实还在的情况不受影响（此测试中 novel.yaml 已删，但 state 保留）
    }

    // ── ⑮ 并发串行化 ──

    [Fact]
    public async Task ConcurrentSyncs_AreSerialized()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        var service = CreateService();

        await Task.WhenAll(
            service.SyncAsync(NovelId),
            service.SyncAsync(NovelId),
            service.SyncAsync(NovelId)
        );

        Assert.Equal(1, _client.MaxConcurrent); // 闸门保证互斥
        Assert.Equal(CloudSyncState.Synced, service.State);
    }

    // ── ⑯ 网络异常 → Offline 状态 + CloudApiException ──

    [Fact]
    public async Task NetworkFailure_ReportsOfflineState()
    {
        CreateNovelFile("我的小说/novel.yaml", "id: nv-001\n");
        _client.FailWith = new CloudApiException("network_unreachable", "云端不可达");
        var service = CreateService();

        await Assert.ThrowsAsync<CloudApiException>(() => service.SyncAsync(NovelId));
        Assert.Equal(CloudSyncState.Offline, service.State);

        _client.FailWith = null;
        await service.SyncAsync(NovelId);
        Assert.Equal(CloudSyncState.Synced, service.State);
    }

    // ── stub client ──

    private sealed class StubSyncClient : ICloudNovelSyncClient
    {
        public readonly Dictionary<string, SyncDocumentDto> RemoteDocuments = [];
        public readonly List<List<SyncPushItemDto>> PushCalls = [];
        public readonly Dictionary<string, SyncPushResultDto> PushResults = [];
        public readonly List<List<SyncDeleteItemDto>> DeleteCalls = [];
        public readonly List<SyncDeleteResultDto> DeleteResults = [];
        public bool SaveState = true;

        /// <summary>delete 结果状态强制覆盖（未知状态 default 分支测试用；null = 正常 deleted）。</summary>
        public string? DeleteStatusOverride;
        private int _inFlight;
        public int MaxConcurrent;

        public Task<IReadOnlyList<SyncNovelSummaryDto>> ListNovelsAsync(
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<SyncNovelSummaryDto>>([]);

        public Task<IReadOnlyList<SyncDocumentMetaDto>> ListDocumentsAsync(
            string novelId,
            CancellationToken ct = default
        )
        {
            if (FailWith is not null)
            {
                throw FailWith; // 同步抛出：await 路径直接收到 CloudApiException（无 Aggregate 包装）
            }
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            var result = Task.Run(
                async () =>
                {
                    await Task.Delay(20, ct);
                    return (IReadOnlyList<SyncDocumentMetaDto>)
                        [
                            .. RemoteDocuments.Values.Select(d => new SyncDocumentMetaDto(
                                d.DocumentType,
                                d.EntityId,
                                d.Revision,
                                d.ContentSha256,
                                d.RelativePath,
                                d.UpdatedAt
                            )),
                        ];
                },
                ct
            );
            return result.ContinueWith(
                t =>
                {
                    Interlocked.Decrement(ref _inFlight);
                    return t.Result;
                },
                CancellationToken.None
            );
        }

        public CloudApiException? FailWith;

        public Task<IReadOnlyList<SyncDocumentDto>> FetchDocumentsAsync(
            string novelId,
            IReadOnlyList<(string Type, string EntityId)> keys,
            CancellationToken ct = default
        )
        {
            var keySet = keys.ToHashSet();
            return Task.FromResult<IReadOnlyList<SyncDocumentDto>>([
                .. RemoteDocuments.Values.Where(d => keySet.Contains((d.DocumentType, d.EntityId))),
            ]);
        }

        public Task<IReadOnlyList<SyncPushResultDto>> PushDocumentsAsync(
            string novelId,
            IReadOnlyList<SyncPushItemDto> items,
            CancellationToken ct = default
        )
        {
            PushCalls.Add([.. items]);
            var results = new List<SyncPushResultDto>();
            foreach (var item in items)
            {
                var result = new SyncPushResultDto(
                    item.DocumentType,
                    item.EntityId,
                    "accepted",
                    1,
                    null,
                    null
                );
                PushResults[item.EntityId] = result;
                results.Add(result);
                if (SaveState)
                {
                    // 替换同 (type, entityId) 的既有远端条目（避免旧路径残留产生重复 meta）
                    var stale = RemoteDocuments
                        .Where(kv =>
                            kv.Value.DocumentType == item.DocumentType
                            && kv.Value.EntityId == item.EntityId
                        )
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in stale)
                    {
                        RemoteDocuments.Remove(key);
                    }
                    RemoteDocuments[item.EntityId] = Full(
                        item.DocumentType,
                        item.EntityId,
                        1,
                        item.ContentSha256,
                        item.RelativePath,
                        item.Content,
                        item.ContentEncoding
                    );
                }
            }
            return Task.FromResult<IReadOnlyList<SyncPushResultDto>>(results);
        }

        public Task<IReadOnlyList<SyncDeleteResultDto>> DeleteDocumentsAsync(
            string novelId,
            IReadOnlyList<SyncDeleteItemDto> items,
            CancellationToken ct = default
        )
        {
            DeleteCalls.Add([.. items]);
            var results = new List<SyncDeleteResultDto>();
            foreach (var item in items)
            {
                var status = DeleteStatusOverride ?? "deleted";
                if (status == "deleted")
                {
                    RemoteDocuments.Remove(item.EntityId);
                }
                var result = new SyncDeleteResultDto(
                    item.DocumentType,
                    item.EntityId,
                    status,
                    null,
                    null
                );
                DeleteResults.Add(result);
                results.Add(result);
            }
            return Task.FromResult<IReadOnlyList<SyncDeleteResultDto>>(results);
        }
    }
}
