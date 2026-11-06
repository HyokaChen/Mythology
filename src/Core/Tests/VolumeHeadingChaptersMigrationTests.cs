using Core.Data;
using Core.Data.Migrations;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 010：把「第N卷 …」空章节还原为分卷。
/// 覆盖：卷名/卷序/卷纲字段搬运、正文非空的卷标题章不动、引用清理、幂等。
/// </summary>
public sealed class VolumeHeadingChaptersMigrationTests : IDisposable
{
    private const string NovelId = "nv-010";
    private readonly string _tmp;
    private readonly string _novelsRoot;
    private readonly string _mythologyDir;
    private readonly string _novelDir;
    private readonly FileNovelStore _store;

    public VolumeHeadingChaptersMigrationTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"volume-heading-{Guid.NewGuid():N}");
        _novelsRoot = Path.Combine(_tmp, "novels");
        _mythologyDir = Path.Combine(_tmp, "mythology");
        _novelDir = Path.Combine(_novelsRoot, "恋爱吧！折花君");
        Directory.CreateDirectory(_novelDir);
        Directory.CreateDirectory(_mythologyDir);
        _store = new FileNovelStore(_novelsRoot, _mythologyDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task VolumeHeadingChapters_ConvertedToVolumes_ReferencesCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var migration = new VolumeHeadingChapters();
        await migration.UpAsync(_mythologyDir, _novelsRoot, ct);

        // ① 章节文件：ch1/ch2/ch3（卷标题、正文为空）已删；ch4（有正文）/ch5（普通章）保留
        var chapterFiles = Directory.GetFiles(Path.Combine(_novelDir, "chapters"), "*.md");
        Assert.Equal(2, chapterFiles.Length);
        Assert.DoesNotContain(chapterFiles, f => f.EndsWith("ch001.md", StringComparison.Ordinal));
        Assert.DoesNotContain(chapterFiles, f => f.EndsWith("ch002.md", StringComparison.Ordinal));
        Assert.DoesNotContain(chapterFiles, f => f.EndsWith("ch003.md", StringComparison.Ordinal));

        // ② 分卷实体：编号/卷名从标题解析；章节的卷纲字段搬进卷
        var volumes = await _store.GetVolumes(NovelId, ct);
        Assert.Equal([1, 2, 11], volumes.Select(v => v.Order));
        Assert.Equal("眷恋的幽灵——谁在眷恋着世间？", volumes[0].Name);
        Assert.Equal("夏夜的萤火——为你而绽放的一生", volumes[1].Name);
        Assert.Equal("风起", volumes[2].Name);
        Assert.Equal("第二卷梗概", volumes[1].Summary);
        Assert.Equal(["pl1", "pl2"], volumes[1].PlotlineIds);
        Assert.Equal(["c1"], volumes[1].CharacterIds);
        Assert.Equal("卷备注", volumes[1].Notes);

        // ③ 结构索引：3 卷 + 剩余 2 章（都未归属分卷）
        var structure = await _store.GetStructure(NovelId, ct);
        Assert.NotNull(structure);
        Assert.Equal(3, structure!.Volumes.Count);
        Assert.Equal(2, structure.Chapters.Count);
        Assert.Equal(2, structure.Unassigned.Count);

        // ④ 场景引用：被删章的 chapter-id 清空；未涉及章节保持
        var scenes = await _store.GetScenesByNovel(NovelId, ct);
        Assert.Null(scenes.Single(s => s.Id == "sc1").ChapterId);
        Assert.Equal("ch5", scenes.Single(s => s.Id == "sc2").ChapterId);

        // ⑤ 情节线引用清空
        var plotline = (await _store.GetPlotlines(NovelId, ct)).Single(p => p.Id == "pl1");
        Assert.Null(plotline.PlannedRevealChapterId);
        Assert.Null(plotline.ResolvedAtChapterId);

        // ⑥ 道具所有权时窗引用清空
        var itemYaml = await File.ReadAllTextAsync(
            Path.Combine(_novelDir, "items", "半颗心-it1.yaml"),
            ct
        );
        Assert.DoesNotContain("since-chapter-id", itemYaml);
        Assert.DoesNotContain("until-chapter-id", itemYaml);

        // ⑦ 关系 / 世界规则引用清空
        var relationships = await File.ReadAllTextAsync(
            Path.Combine(_novelDir, "relationships.yaml"),
            ct
        );
        Assert.DoesNotContain("since-chapter-id", relationships);
        var rules = await File.ReadAllTextAsync(Path.Combine(_novelDir, "world-rules.yaml"), ct);
        Assert.DoesNotContain("effective-from-chapter-id", rules);

        // ⑧ 出场分片清空后整文件删除；变更集目录删除
        Assert.False(
            File.Exists(Path.Combine(_novelDir, "appearances", "ch001.yaml")),
            "该章唯一出场记录被移除后分片应删除"
        );
        Assert.False(Directory.Exists(Path.Combine(_novelDir, "changesets", "ch1")));

        // ⑨ 幂等：再跑一遍不新增卷、不报错
        await migration.UpAsync(_mythologyDir, _novelsRoot, ct);
        Assert.Equal(3, (await _store.GetVolumes(NovelId, ct)).Count);
    }

    [Fact]
    public async Task VolumeHeadingChapter_SameOrderDifferentName_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "novel.yaml"),
            "id: nv-010\ntitle: 重复卷序\n",
            ct
        );
        await WriteChapterAsync("ch001.md", "chB", 1, "第一卷 A", "", ct);
        await WriteYamlAsync(
            "volumes/另一个-v1.yaml",
            """
            id: v1
            novel-id: nv-010
            name: 另一个
            order: 1
            summary: ""
            description: ""
            goal: ""
            plotline-ids: []
            character-ids: []
            notes: ""
            """,
            ct
        );

        await new VolumeHeadingChapters().UpAsync(_mythologyDir, _novelsRoot, ct);

        // 同卷序已有不同卷名：不重复建同号卷，也不删除占位章（宁可漏迁）
        Assert.True(File.Exists(Path.Combine(_novelDir, "chapters", "ch001.md")));
        Assert.Single(await _store.GetVolumes(NovelId, ct));
    }

    [Fact]
    public async Task MigrationRunner_DiscoversAndApplies010()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "novel.yaml"),
            "id: nv-runner\ntitle: 启动迁移\n",
            ct
        );
        await WriteChapterAsync("ch001.md", "chR", 1, "第一卷 起风", "", ct);

        await new MigrationRunner(_mythologyDir, _novelsRoot).RunAsync(ct);

        var version = await File.ReadAllTextAsync(Path.Combine(_mythologyDir, "version.json"), ct);
        Assert.Contains("010_VolumeHeadingChapters", version);
        Assert.False(File.Exists(Path.Combine(_novelDir, "chapters", "ch001.md")));
        Assert.Single(await _store.GetVolumes("nv-runner", ct));
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "novel.yaml"),
            """
            id: nv-010
            title: 恋爱吧！折花君
            genre: 都市生活
            description: ""
            outline: ""
            writing-style: ""
            completion-status: completed
            created-at: 2026-07-09T19:41:55.9159330+08:00
            updated-at: 2026-10-04T14:16:41.0942810Z
            """,
            ct
        );

        Directory.CreateDirectory(Path.Combine(_novelDir, "chapters"));
        await WriteChapterAsync(
            "ch001.md",
            "ch1",
            1,
            "第一卷 眷恋的幽灵——谁在眷恋着世间？",
            "",
            ct
        );
        await WriteChapterAsync(
            "ch002.md",
            "ch2",
            2,
            "第二卷 夏夜的萤火——为你而绽放的一生",
            "",
            ct,
            "summary: 第二卷梗概\nplotline-ids: pl1,pl2\ncharacter-ids: c1\nnotes: 卷备注"
        );
        await WriteChapterAsync("ch003.md", "ch3", 3, "第十一卷 风起", "", ct);
        await WriteChapterAsync("ch004.md", "ch4", 4, "第一卷 有正文", "正文不是卷标题占位", ct);
        await WriteChapterAsync("ch005.md", "ch5", 5, "第一章 开场", "", ct);

        await WriteYamlAsync(
            "scenes/场景一-sc1.yaml",
            """
            id: sc1
            novel-id: nv-010
            title: 场景一
            order: 1
            chapter-id: ch1
            location-id: ''
            participant-ids: []
            plotline-ids: []
            item-ids: []
            conflict: ''
            goal: ''
            emotional-arc: ''
            scene-type: dialogue
            notes: ''
            created-at: 2026-09-02T12:15:56.2947120Z
            updated-at: 2026-09-02T12:15:56.2947120Z
            """,
            ct
        );
        await WriteYamlAsync(
            "scenes/场景二-sc2.yaml",
            """
            id: sc2
            novel-id: nv-010
            title: 场景二
            order: 2
            chapter-id: ch5
            created-at: 2026-09-02T12:15:56.2947120Z
            updated-at: 2026-09-02T12:15:56.2947120Z
            """,
            ct
        );

        await WriteYamlAsync(
            "plotlines/卷一·眷恋的幽灵-pl1.yaml",
            """
            id: pl1
            novel-id: nv-010
            name: 卷一·眷恋的幽灵
            description: ''
            type: main
            tension: medium
            planned-reveal-chapter-id: ch2
            resolved-at-chapter-id: ch2
            status: active
            turnpoints: ''
            created-at: 2026-08-04T16:44:36.9809540Z
            updated-at: 2026-08-04T16:44:36.9809540Z
            """,
            ct
        );

        await WriteYamlAsync(
            "items/半颗心-it1.yaml",
            """
            id: it1
            novel-id: nv-010
            name: 半颗心
            type: artifact
            description: ''
            owner-id: c1
            location-id: ''
            ownership-periods:
            - owner-id: c1
              since-chapter-id: ch1
              until-chapter-id: ch2
            status: active
            metrics: {}
            created-at: 2026-08-04T16:44:36.9809540Z
            updated-at: 2026-08-04T16:44:36.9809540Z
            """,
            ct
        );

        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "relationships.yaml"),
            """
            - id: rel1
              novel-id: nv-010
              character-a-id: c1
              character-b-id: c2
              type: friend
              strength: medium
              description: ''
              since-chapter-id: ch1
              until-chapter-id: ''
            """,
            ct
        );
        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "world-rules.yaml"),
            """
            - id: wr1
              novel-id: nv-010
              entry-kind: rule
              category: other
              type: test
              value: 1
              unit: ''
              description: ''
              rule-kind: numeric
              effective-from-chapter-id: ch1
              effective-until-chapter-id: ''
              severity: warning
            """,
            ct
        );

        Directory.CreateDirectory(Path.Combine(_novelDir, "appearances"));
        await File.WriteAllTextAsync(
            Path.Combine(_novelDir, "appearances", "ch001.yaml"),
            """
            - id: ap1
              novel-id: nv-010
              entity-type: character
              entity-id: c1
              chapter-id: ch1
              mode: present
              source: system
              created-at: 2026-08-04T16:44:36.9809540Z
              updated-at: 2026-08-04T16:44:36.9809540Z
            """,
            ct
        );

        var changesetDir = Path.Combine(_novelDir, "changesets", "ch1");
        Directory.CreateDirectory(changesetDir);
        await File.WriteAllTextAsync(
            Path.Combine(changesetDir, "draft.json"),
            """{"id":"ch1","novel-id":"nv-010"}""",
            ct
        );
    }

    private async Task WriteChapterAsync(
        string fileName,
        string id,
        int order,
        string title,
        string body,
        CancellationToken ct,
        string extraMeta = ""
    )
    {
        var text = $"""
            ---
            id: {id}
            order: {order}
            title: {title}
            status: draft
            created-at: 2026-08-04T16:44:23.4932660Z
            updated-at: 2026-08-04T16:44:23.4932660Z
            {(extraMeta.Length > 0 ? extraMeta : "")}
            ---

            {body}
            """;

        await WriteFileAsync(Path.Combine("chapters", fileName), text, ct);
    }

    private Task WriteYamlAsync(string relativePath, string content, CancellationToken ct) =>
        WriteFileAsync(relativePath, content, ct);

    private async Task WriteFileAsync(string relativePath, string content, CancellationToken ct)
    {
        var path = Path.Combine(_novelDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }
}
