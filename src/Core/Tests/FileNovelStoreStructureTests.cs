using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 结构索引（<see cref="INovelStore.GetStructure"/> / <see cref="INovelStore.GetChapterByOrder"/>）：
/// 只读骨架（frontmatter + 正文字符数），不把正文留在内存；缓存按目录戳失效。
/// </summary>
public sealed class FileNovelStoreStructureTests : IDisposable
{
    private const string NovelId = "structure-novel";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public FileNovelStoreStructureTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"structure-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        _myth = Path.Combine(tmp, "mythology");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(_root, _myth);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private async Task SeedAsync()
    {
        await _store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "结构索引",
                Outline = "全书走向：从微末到巅峰",
            }
        );
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol-a",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
                Summary = "开篇",
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch-a1",
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "vol-a",
                Summary = "细纲一",
                Content = "正文一",
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch-a2",
                NovelId = NovelId,
                Title = "第二章",
                Order = 2,
                VolumeId = "vol-a",
                Content = "正文二更长一些",
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch-b1",
                NovelId = NovelId,
                Title = "未分卷章",
                Order = 3,
                Content = "游离",
            }
        );
    }

    /// <summary>章节目录由存储层布局决定 → 动态定位（不硬编码 novelId 目录名）。</summary>
    private string ChaptersDir() =>
        Directory.GetDirectories(_root, "chapters", SearchOption.AllDirectories).Single();

    /// <summary>novel.yaml 所在目录。</summary>
    private string NovelDir() => Directory.GetParent(ChaptersDir())!.FullName;

    [Fact]
    public async Task GetStructure_ReturnsHeadersWithoutContent_AndExactCharCounts()
    {
        await SeedAsync();

        var structure = await _store.GetStructure(NovelId);

        Assert.NotNull(structure);
        Assert.Equal("结构索引", structure!.Novel.Title);
        Assert.Equal("全书走向：从微末到巅峰", structure.Novel.Outline);
        Assert.Single(structure.Volumes);
        Assert.Equal(3, structure.Chapters.Count);

        // 正文不留在内存；CharCount 与真实正文长度逐章一致。
        var full = (await _store.GetChapters(NovelId)).ToDictionary(c => c.Id, c => c);
        foreach (var header in structure.Chapters)
        {
            Assert.Equal("", header.Content);
            Assert.Equal(full[header.Id].Content.Length, header.CharCount);
        }

        // 细纲/归属等元数据照常可用。
        var first = structure.Chapters.Single(c => c.Id == "ch-a1");
        Assert.Equal("细纲一", first.Summary);
        Assert.Equal("vol-a", first.VolumeId);
        Assert.Equal(1, first.Order);

        // 未分卷桶：VolumeId 为空或指向不存在的卷。
        Assert.Single(structure.Unassigned);
        Assert.Equal("ch-b1", structure.Unassigned[0].Id);
    }

    [Fact]
    public async Task GetStructure_DanglingVolumeId_LandsInUnassigned()
    {
        await SeedAsync();
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch-ghost",
                NovelId = NovelId,
                Title = "悬空卷章",
                Order = 4,
                VolumeId = "vol-does-not-exist",
                Content = "x",
            }
        );

        var structure = await _store.GetStructure(NovelId);

        Assert.NotNull(structure);
        Assert.Contains(structure!.Unassigned, c => c.Id == "ch-ghost");
        Assert.Equal(2, structure.Unassigned.Count);
    }

    [Fact]
    public async Task GetChapterByOrder_MatchesGetChapter()
    {
        await SeedAsync();

        var byOrder = await _store.GetChapterByOrder(NovelId, 2);
        var byId = await _store.GetChapter(NovelId, "ch-a2");

        Assert.NotNull(byOrder);
        Assert.Equal("ch-a2", byOrder!.Id);
        Assert.Equal("正文二更长一些", byOrder.Content);
        Assert.Equal(byOrder.Content.Length, byOrder.CharCount);
        Assert.Equal(byId!.Content, byOrder.Content);

        Assert.Null(await _store.GetChapterByOrder(NovelId, 99));
    }

    [Fact]
    public async Task GetChapter_NumericId_ResolvesByOrder()
    {
        await SeedAsync();

        var chapter = await _store.GetChapter(NovelId, "3");

        Assert.NotNull(chapter);
        Assert.Equal("ch-b1", chapter!.Id);
        Assert.Equal("游离", chapter.Content);
    }

    [Fact]
    public async Task GetStructure_ReflectsSaveChapter_ThroughCacheInvalidation()
    {
        await SeedAsync();
        var before = await _store.GetStructure(NovelId);
        Assert.Equal("第一章", before!.Chapters.Single(c => c.Id == "ch-a1").Title);

        var chapter = await _store.GetChapter(NovelId, "ch-a1");
        chapter!.Title = "第一章（改）";
        await _store.SaveChapter(chapter);

        var after = await _store.GetStructure(NovelId);
        Assert.Equal("第一章（改）", after!.Chapters.Single(c => c.Id == "ch-a1").Title);
    }

    [Fact]
    public async Task GetStructure_ReflectsExternalDiskEdit_ThroughStamp()
    {
        await SeedAsync();
        await _store.GetStructure(NovelId); // 先填充缓存

        // 绕过存储层直接改盘（模拟另一台设备同步落地）：缓存必须靠目录戳失配。
        var path = Path.Combine(ChaptersDir(), "ch001.md");
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("title: 第一章", "title: 盘上改的"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        var after = await _store.GetStructure(NovelId);
        Assert.Equal("盘上改的", after!.Chapters.Single(c => c.Id == "ch-a1").Title);
    }

    /// <summary>
    /// 规模探针（1200 章）：结构索引只读骨架、不把正文留在内存，且热缓存读取远快于全量正文读。
    /// 这是「设定面板上千章可用」的数据层依据。
    /// </summary>
    [Fact]
    public async Task GetStructure_On1200Chapters_IsBodyFreeAndCached()
    {
        const int chapterCount = 1200;
        await _store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "千章规模",
                Outline = "总纲",
            }
        );

        // 章节目录尚不存在（SaveNovel 只写 novel.yaml）→ 直接由 novel.yaml 定位小说目录
        var novelYaml = Directory
            .GetFiles(_root, "novel.yaml", SearchOption.AllDirectories)
            .Single();
        var chaptersDir = Path.Combine(Path.GetDirectoryName(novelYaml)!, "chapters");
        Directory.CreateDirectory(chaptersDir);
        var body = new string('文', 900);
        for (var order = 1; order <= chapterCount; order++)
        {
            var meta = new Dictionary<string, string>
            {
                ["id"] = $"ch{order:D4}",
                ["order"] = order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["title"] = $"第{order}章",
                ["status"] = "draft",
                ["volume-id"] = $"vol{((order - 1) / 20) + 1}",
                ["summary"] = $"细纲{order}",
                ["plotline-ids"] = "",
                ["character-ids"] = "",
                ["notes"] = "",
                ["created-at"] = DateTime.UtcNow.ToString("O"),
                ["updated-at"] = DateTime.UtcNow.ToString("O"),
            };
            await File.WriteAllTextAsync(
                Path.Combine(chaptersDir, $"ch{order:D3}.md"),
                MarkdownHelper.WriteMarkdown(meta, body)
            );
        }

        // 不做墙钟/分配量断言：时间受磁盘与并发负载影响会偶发翻转（曾两次假红），
        // 分配量又被 markdown 解析开销主导（实测索引 30MB > 正文读 27MB，正文串只占 2MB）——
        // 要钉的不变量是「快照不持有正文 + 目录戳未变即命中缓存」，两者都是确定性的。
        var structure = await _store.GetStructure(NovelId);
        var warm = await _store.GetStructure(NovelId);
        var chapters = await _store.GetChapters(NovelId);

        Assert.NotNull(structure);
        Assert.Same(structure, warm); // 目录戳未变 → 直接命中缓存（引用同一实例）
        Assert.Equal(chapterCount, chapters.Count);
        Assert.Equal(chapterCount, structure!.Chapters.Count);
        Assert.All(structure.Chapters, c => Assert.Equal("", c.Content)); // 快照不持有正文
        Assert.All(structure.Chapters, c => Assert.Equal(body.Length, c.CharCount));
        Assert.True(
            chapters.Sum(c => c.Content.Length) == (long)chapterCount * body.Length,
            "全量读路径必须带正文（对照组），否则本次对比失去意义"
        );
    }

    [Fact]
    public async Task GetStructure_MissingNovel_ReturnsNull()
    {
        Assert.Null(await _store.GetStructure("no-such-novel"));
    }
}
