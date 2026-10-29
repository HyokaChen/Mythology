using System.Security.Cryptography;
using System.Text;
using Core.Data;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 小说云端同步（cloud-novel-sync v3）跨仓库契约测试：真实后端子进程（InMemory）+ 真实
/// <see cref="CloudNovelSyncClient"/> / <see cref="CloudNovelSyncService"/>——
/// 两仓各自的单测只能证明「各自想象的对方」，这里证明 push 之后的目录名、相对路径、
/// 逐字节内容、乐观锁、删除与重命名在**两端之间**成立（计划 B6 的唯一端到端防线）。
/// </summary>
[Collection("live-backend")]
public sealed class NovelSyncContractTests(CloudBackendFixture fixture) : IDisposable
{
    private const string SkipReason =
        "未找到私有后端（设置 MYTHOLOGY_CLOUD_BACKEND 或保持兄弟目录布局），跳过跨仓库契约测试";

    private const string ContractKey = "mk_test_dev-local_contract";

    /// <summary>非 ASCII 目录名：DirectoryName 保真本身就是要验证的契约之一。</summary>
    private const string NovelDirectory = "契约小说";

    /// <summary>非 UTF-8 字节：强制走 base64 通道，验证二进制往返。</summary>
    private static readonly byte[] BinaryBytes =
    [
        0x89,
        0x50,
        0x4E,
        0x47,
        0x0D,
        0x0A,
        0x1A,
        0x0A,
        0xFF,
        0xFE,
        0x00,
        0x7F,
    ];

    private const string ChapterOne = "---\nid: ch1\ntitle: 第一章\n---\n正文一";
    private const string ChapterTwo = "---\nid: ch2\ntitle: 第二章\n---\n正文二";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"novel-sync-contract-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed record Device(
        string NovelsRoot,
        string MythologyDir,
        CloudNovelSyncService Service,
        ICloudNovelSyncClient Client
    );

    private Device NewDevice(string name)
    {
        var novelsRoot = Path.Combine(_root, name, "novels");
        var mythologyDir = Path.Combine(_root, name, "mythology");
        Directory.CreateDirectory(novelsRoot);
        Directory.CreateDirectory(mythologyDir);
        var client = new CloudNovelSyncClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            () => ContractKey,
            fixture.BaseUrl!
        );
        var service = new CloudNovelSyncService(
            client,
            new CloudSyncStateStore(mythologyDir),
            NovelWorkspace.Single(novelsRoot),
            mythologyDir
        );
        return new Device(novelsRoot, mythologyDir, service, client);
    }

    private static string NewNovelId() => $"nv-contract-{Guid.NewGuid():N}"[..20];

    private static string ShaOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static SyncPushItemDto Doc(
        string documentType,
        string entityId,
        string relativePath,
        string content,
        int baseRevision = 0,
        string encoding = "utf8",
        string? title = null
    ) =>
        new(
            documentType,
            entityId,
            baseRevision,
            ShaOf(content),
            title,
            relativePath,
            encoding,
            content
        );

    private static string LocalPath(string novelsRoot, string relativePath) =>
        Path.Combine(novelsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>本地一本小说的最小完整形态：novel.yaml + 2 章 + 1 角色 + 1 二进制素材。</summary>
    private static void WriteLocalNovel(string novelsRoot, string novelId)
    {
        var dir = Path.Combine(novelsRoot, NovelDirectory);
        Directory.CreateDirectory(Path.Combine(dir, "chapters"));
        Directory.CreateDirectory(Path.Combine(dir, "characters"));
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(
            Path.Combine(dir, "novel.yaml"),
            $"id: {novelId}\ntitle: {NovelDirectory}\n"
        );
        File.WriteAllText(Path.Combine(dir, "chapters", "ch-01.md"), ChapterOne);
        File.WriteAllText(Path.Combine(dir, "chapters", "ch-02.md"), ChapterTwo);
        File.WriteAllText(Path.Combine(dir, "characters", "林.yaml"), "id: lin\nname: 林\n");
        File.WriteAllBytes(Path.Combine(dir, "assets", "封面.png"), BinaryBytes);
    }

    private static Dictionary<string, byte[]> SnapshotTree(string novelsRoot)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(novelsRoot))
        {
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(novelsRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(novelsRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            result[relativePath] = File.ReadAllBytes(file);
        }
        return result;
    }

    private static string[] SortedPaths(IEnumerable<string> paths) =>
        [.. paths.OrderBy(path => path, StringComparer.Ordinal)];

    [Fact]
    public async Task PushListFetchDelete_AgainstLiveBackend_PreserveDirectoryPathAndBytes()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var novelId = NewNovelId();
        var client = NewDevice("wire").Client;
        var binaryBase64 = Convert.ToBase64String(BinaryBytes);
        var novelYaml = $"id: {novelId}\ntitle: {NovelDirectory}\n";

        var documents = new[]
        {
            Doc("novel", "self", $"{NovelDirectory}/novel.yaml", novelYaml, title: NovelDirectory),
            Doc("chapter", "ch1", $"{NovelDirectory}/chapters/ch-01.md", ChapterOne),
            Doc("chapter", "ch2", $"{NovelDirectory}/chapters/ch-02.md", ChapterTwo),
            Doc("character", "lin", $"{NovelDirectory}/characters/林.yaml", "id: lin\nname: 林\n"),
            Doc(
                "file",
                $"{NovelDirectory}/assets/封面.png",
                $"{NovelDirectory}/assets/封面.png",
                binaryBase64,
                encoding: "base64"
            ),
            Doc(
                "file",
                $"{NovelDirectory}/misc/大纲草稿.txt",
                $"{NovelDirectory}/misc/大纲草稿.txt",
                "草稿正文"
            ),
        };

        var pushed = await client.PushDocumentsAsync(novelId, documents, ct);
        Assert.All(pushed, result => Assert.Equal("accepted", result.Status));
        Assert.All(pushed, result => Assert.Equal(1, result.Revision));

        // 目录名保真（非 ASCII）+ 文档计数 —— 这是客户端 :cloud sync 后 curl 断言的同一条契约。
        var novels = await client.ListNovelsAsync(ct);
        var summary = Assert.Single(novels, novel => novel.NovelId == novelId);
        Assert.Equal(NovelDirectory, summary.DirectoryName);
        Assert.Equal(documents.Length, summary.DocumentCount);

        var metas = await client.ListDocumentsAsync(novelId, ct);
        Assert.Equal(
            SortedPaths(documents.Select(document => document.RelativePath)),
            SortedPaths(metas.Select(meta => meta.RelativePath))
        );
        foreach (var meta in metas)
        {
            var expected = documents.Single(document =>
                document.DocumentType == meta.DocumentType && document.EntityId == meta.EntityId
            );
            Assert.Equal(expected.ContentSha256, meta.ContentSha256);
        }

        // 取回：文本与二进制都要逐字节一致，相对路径不得被改写。
        var binaryPath = $"{NovelDirectory}/assets/封面.png";
        var fetched = await client.FetchDocumentsAsync(
            novelId,
            [("chapter", "ch1"), ("file", binaryPath)],
            ct
        );
        var chapter = Assert.Single(
            fetched,
            document => document.DocumentType == "chapter" && document.EntityId == "ch1"
        );
        Assert.Equal(ChapterOne, chapter.Content);
        Assert.Equal("utf8", chapter.ContentEncoding);
        Assert.Equal($"{NovelDirectory}/chapters/ch-01.md", chapter.RelativePath);

        var binary = Assert.Single(
            fetched,
            document => document.DocumentType == "file" && document.EntityId == binaryPath
        );
        Assert.Equal("base64", binary.ContentEncoding);
        Assert.Equal(BinaryBytes, Convert.FromBase64String(binary.Content));

        // 乐观锁：基线过期 → HTTP 200 + conflict + Current（不走异常路径）。
        var staleContent = "---\nid: ch1\ntitle: 第一章\n---\n过期写入";
        var conflict = await client.PushDocumentsAsync(
            novelId,
            [Doc("chapter", "ch1", $"{NovelDirectory}/chapters/ch-01.md", staleContent)],
            ct
        );
        Assert.Equal("conflict", conflict[0].Status);
        Assert.Equal(1, conflict[0].Revision);
        Assert.NotNull(conflict[0].Current);
        Assert.Equal(ChapterOne, conflict[0].Current!.Content);

        var acceptedContent = "---\nid: ch1\ntitle: 第一章\n---\n合并后正文";
        var accepted = await client.PushDocumentsAsync(
            novelId,
            [
                Doc(
                    "chapter",
                    "ch1",
                    $"{NovelDirectory}/chapters/ch-01.md",
                    acceptedContent,
                    baseRevision: 1
                ),
            ],
            ct
        );
        Assert.Equal("accepted", accepted[0].Status);
        Assert.Equal(2, accepted[0].Revision);

        // 删除语义：deleted → not_found（幂等）→ conflict（基线过期，附 Current）。
        var deleted = await client.DeleteDocumentsAsync(
            novelId,
            [new SyncDeleteItemDto("chapter", "ch2", 1)],
            ct
        );
        Assert.Equal("deleted", deleted[0].Status);

        var idempotent = await client.DeleteDocumentsAsync(
            novelId,
            [new SyncDeleteItemDto("chapter", "ch2", 1)],
            ct
        );
        Assert.Equal("not_found", idempotent[0].Status);

        var deleteConflict = await client.DeleteDocumentsAsync(
            novelId,
            [new SyncDeleteItemDto("chapter", "ch1", 1)],
            ct
        );
        Assert.Equal("conflict", deleteConflict[0].Status);
        Assert.Equal(2, deleteConflict[0].Current!.Revision);

        var remaining = await client.ListDocumentsAsync(novelId, ct);
        Assert.DoesNotContain(
            remaining,
            meta => meta.DocumentType == "chapter" && meta.EntityId == "ch2"
        );
    }

    [Fact]
    public async Task Service_TwoDevices_PullRestoresByteIdenticalTreeAtOriginalPaths()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var novelId = NewNovelId();
        var deviceA = NewDevice("a");
        WriteLocalNovel(deviceA.NovelsRoot, novelId);

        var pushed = await deviceA.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(5, pushed.Pushed);
        Assert.Equal(0, pushed.Conflicts);

        // 第二台设备：空目录 + 空 state → 只能靠远端 bootstrap。
        var deviceB = NewDevice("b");
        var pulled = await deviceB.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(5, pulled.Pulled);
        Assert.Equal(0, pulled.Conflicts);

        var treeA = SnapshotTree(deviceA.NovelsRoot);
        var treeB = SnapshotTree(deviceB.NovelsRoot);
        Assert.Equal(SortedPaths(treeA.Keys), SortedPaths(treeB.Keys));
        foreach (var (relativePath, bytes) in treeA)
        {
            Assert.True(
                bytes.AsSpan().SequenceEqual(treeB[relativePath]),
                $"同步后文件内容不一致：{relativePath}"
            );
        }

        // 目录名（非 ASCII）与相对路径在第二台设备上原样重建。
        Assert.Contains($"{NovelDirectory}/novel.yaml", treeB.Keys);
        Assert.Contains($"{NovelDirectory}/assets/封面.png", treeB.Keys);
        Assert.Contains($"{NovelDirectory}/chapters/ch-02.md", treeB.Keys);
    }

    [Fact]
    public async Task Service_RemoteRename_PropagatesNewPathAndCleansOldPath()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var novelId = NewNovelId();
        var deviceA = NewDevice("a");
        WriteLocalNovel(deviceA.NovelsRoot, novelId);
        await deviceA.Service.SyncAsync(novelId, ct: ct);

        var deviceB = NewDevice("b");
        await deviceB.Service.SyncAsync(novelId, ct: ct);

        // 设备 B 本地重命名第二章（front-matter id 不变 → 同一 key，只有 path 变）。
        var oldRelative = $"{NovelDirectory}/chapters/ch-02.md";
        var newRelative = $"{NovelDirectory}/chapters/ch-02-改名.md";
        File.Move(
            LocalPath(deviceB.NovelsRoot, oldRelative),
            LocalPath(deviceB.NovelsRoot, newRelative)
        );

        var renameReport = await deviceB.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(1, renameReport.Pushed);
        Assert.Equal(0, renameReport.Conflicts);

        var metas = await deviceB.Client.ListDocumentsAsync(novelId, ct);
        Assert.Contains(
            metas,
            meta =>
                meta.DocumentType == "chapter"
                && meta.EntityId == "ch2"
                && meta.RelativePath == newRelative
        );

        // 设备 A 跟随远端改名：新路径落盘、旧路径清理。
        var followReport = await deviceA.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(1, followReport.Pulled);
        Assert.Equal(0, followReport.Conflicts);
        Assert.True(File.Exists(LocalPath(deviceA.NovelsRoot, newRelative)));
        Assert.False(File.Exists(LocalPath(deviceA.NovelsRoot, oldRelative)));
    }

    [Fact]
    public async Task Service_RemoteDelete_PropagatesDeletionToOtherDevice()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var novelId = NewNovelId();
        var deviceA = NewDevice("a");
        WriteLocalNovel(deviceA.NovelsRoot, novelId);
        await deviceA.Service.SyncAsync(novelId, ct: ct);

        var deviceB = NewDevice("b");
        await deviceB.Service.SyncAsync(novelId, ct: ct);

        var chapterRelative = $"{NovelDirectory}/chapters/ch-01.md";
        File.Delete(LocalPath(deviceB.NovelsRoot, chapterRelative));

        var deleteReport = await deviceB.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(1, deleteReport.Deleted);
        Assert.Equal(0, deleteReport.Conflicts);

        var metas = await deviceB.Client.ListDocumentsAsync(novelId, ct);
        Assert.DoesNotContain(
            metas,
            meta => meta.DocumentType == "chapter" && meta.EntityId == "ch1"
        );

        var followReport = await deviceA.Service.SyncAsync(novelId, ct: ct);
        Assert.Equal(1, followReport.Deleted);
        Assert.False(File.Exists(LocalPath(deviceA.NovelsRoot, chapterRelative)));
        // 删除只影响该文档：其余文件保持原样。
        Assert.True(
            File.Exists(LocalPath(deviceA.NovelsRoot, $"{NovelDirectory}/chapters/ch-02.md"))
        );
    }
}
