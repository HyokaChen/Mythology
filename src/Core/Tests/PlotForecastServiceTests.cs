using Core.Data;
using Core.Forecast;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 远期推演服务端到端：结构索引构建 → 前文证据（只检索已写章）→ 笔记线索（锚点 + 混合检索）
/// → 注入块逐行。固定输入逐行断言（不是「跑通就算」）。
/// </summary>
public sealed class PlotForecastServiceTests : IDisposable
{
    private const string NovelId = "svc-forecast";

    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;
    private readonly InMemoryRetrievalStore _retrieval = new();

    public PlotForecastServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"forecast-{Guid.NewGuid():N}");
        _myth = Path.Combine(_root, "mythology");
        Directory.CreateDirectory(Path.Combine(_root, "novels"));
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(Path.Combine(_root, "novels"), _myth);
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

    private async Task SeedAsync(CancellationToken ct)
    {
        await _store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "推演书",
                Outline = "青铜钥匙开启北境之门",
            },
            ct
        );
        await _store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
                Goal = "拿到青铜钥匙",
            },
            ct
        );

        for (var order = 1; order <= 5; order++)
        {
            await _store.SaveChapter(
                new Chapter
                {
                    Id = $"ch{order}",
                    NovelId = NovelId,
                    Order = order,
                    Title = $"第{order}章",
                    Content = order == 1 ? "青铜钥匙的传闻在井底流传。" : "正文内容。",
                    VolumeId = "v1",
                },
                ct
            );
        }
        foreach (
            var (order, title, summary) in new[]
            {
                (6, "钥匙归位", "回收青铜钥匙"),
                (7, "使者", "北境使者抵达"),
                (8, "决战", "决战"),
            }
        )
        {
            await _store.SaveChapter(
                new Chapter
                {
                    Id = $"ch{order}",
                    NovelId = NovelId,
                    Order = order,
                    Title = title,
                    Summary = summary,
                    VolumeId = "v1",
                    CharacterIds = order == 7 ? ["c-envoy"] : [],
                    PlotlineIds = order == 7 ? ["p-bronze"] : [],
                },
                ct
            );
        }

        await _store.SavePlotline(
            new Plotline
            {
                Id = "p-bronze",
                NovelId = NovelId,
                Name = "青铜钥匙",
                Type = PlotlineType.Foreshadowing,
                PlannedRevealChapterId = "ch6",
                Turnpoints = "第2章：埋下青铜钥匙\n第7章：真相揭露",
            },
            ct
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c-hero",
                NovelId = NovelId,
                Name = "主角",
            },
            ct
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c-envoy",
                NovelId = NovelId,
                Name = "北境使者",
            },
            ct
        );
        await _store.SaveChapterAppearances(
            NovelId,
            "ch1",
            [
                new AppearanceRecord
                {
                    Id = "ap1",
                    NovelId = NovelId,
                    EntityType = EntityType.Character,
                    EntityId = "c-hero",
                    ChapterId = "ch1",
                },
            ],
            ct
        );

        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-a",
                NovelId = NovelId,
                Title = "半块玉的递法",
                Body = "第7章 让北境使者递出半块玉",
                Status = NoteStatus.Active,
            },
            ct
        );
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-b",
                NovelId = NovelId,
                Title = "决战前夜下雨",
                Body = "决战前夜下雨",
                Status = NoteStatus.Inbox,
            },
            ct
        );
    }

    [Fact]
    public async Task BuildAsync_UsesStructureIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var service = new PlotForecastService(_store);

        var forecast = await service.BuildAsync(NovelId, 5, ct);

        Assert.NotNull(forecast);
        Assert.Equal(5, forecast.CurrentOrder);
        Assert.Equal(6, forecast.TargetOrder);
        Assert.Equal("钥匙归位", forecast.TargetChapterTitle);
        Assert.Equal("拿到青铜钥匙", forecast.VolumeGoal);
        Assert.True(forecast.HasPlanData);
    }

    [Fact]
    public async Task BuildAsync_UnknownNovel_ReturnsNull()
    {
        var service = new PlotForecastService(_store);
        Assert.Null(await service.BuildAsync("nope", 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecallAsync_OnlyWrittenChapters()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var service = new PlotForecastService(_store);
        var forecast = await service.BuildAsync(NovelId, 5, ct);
        Assert.NotNull(forecast);

        await using var kernel = new RetrievalKernel(_retrieval, new HashEmbedder());
        var chapters = await _store.GetChapters(NovelId, ct);
        foreach (var chapter in chapters)
        {
            var (doc, chunks) = kernel.Builder.BuildChapterSummary(chapter);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }

        var withKernel = new PlotForecastService(_store, kernel);
        var evidence = await withKernel.RecallAsync(NovelId, forecast!, 6, includeCloud: false, ct);

        Assert.NotEmpty(evidence);
        Assert.All(evidence, e => Assert.Equal("local", e.Source));
        Assert.All(evidence, e => Assert.True(e.ChapterOrder <= forecast!.CurrentOrder));
        // 第7章（未写）的摘要即使命中也不得进入证据
        Assert.DoesNotContain(evidence, e => e.ChapterOrder == 7);
    }

    [Fact]
    public async Task SelectNotesAsync_AnchorsWindowAndReportsInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var service = new PlotForecastService(_store);
        var forecast = await service.BuildAsync(NovelId, 5, ct);
        Assert.NotNull(forecast);

        var selection = await service.SelectNotesAsync(NovelId, forecast!, 4, ct);

        Assert.Equal(1, selection.InboxCount);
        var anchor = Assert.Single(selection.Notes, n => n.Source == "anchor");
        Assert.Equal("note-a", anchor.NoteId);
        Assert.Equal(7, anchor.AnchorOrder);
        Assert.DoesNotContain(selection.Notes, n => n.NoteId == "note-b");
    }

    [Fact]
    public async Task SelectNotesAsync_KernelSearchPicksActiveNotesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        // 无「第N章」锚点的 Active 笔记：只能靠混合检索召回
        await _store.SaveNote(
            new AuthorNote
            {
                Id = "note-c",
                NovelId = NovelId,
                Title = "钥匙会发光",
                Body = "青铜钥匙在月光下会发光",
                Status = NoteStatus.Active,
            },
            ct
        );
        var forecast = await new PlotForecastService(_store).BuildAsync(NovelId, 5, ct);

        await using var kernel = new RetrievalKernel(_retrieval, new HashEmbedder());
        foreach (var note in await _store.GetNotes(NovelId, ct))
        {
            var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }

        var selection = await new PlotForecastService(_store, kernel).SelectNotesAsync(
            NovelId,
            forecast!,
            4,
            ct
        );

        Assert.Contains(selection.Notes, n => n.NoteId == "note-c");
        Assert.DoesNotContain(selection.Notes, n => n.NoteId == "note-b"); // 收件箱不参与
    }

    /// <summary>验证 §6 行为烟测：固定输入 → 注入块逐行。</summary>
    [Fact]
    public async Task EndToEnd_FormattedBlock_MatchesExpectations()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await using var kernel = new RetrievalKernel(_retrieval, new HashEmbedder());
        foreach (var chapter in await _store.GetChapters(NovelId, ct))
        {
            var (doc, chunks) = kernel.Builder.BuildChapterSummary(chapter);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        foreach (var note in await _store.GetNotes(NovelId, ct))
        {
            var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }

        var service = new PlotForecastService(_store, kernel);
        var forecast = await service.BuildAsync(NovelId, 5, ct);
        Assert.NotNull(forecast);
        var evidence = await service.RecallAsync(NovelId, forecast!, 6, includeCloud: false, ct);
        var notes = await service.SelectNotesAsync(NovelId, forecast!, 4, ct);
        var text = PlotForecastFormatter.Format(forecast!, evidence, notes);

        Assert.Contains("[远期推演] 已写至第5章，下一章：第6章", text, StringComparison.Ordinal);
        Assert.Contains("到期义务：", text, StringComparison.Ordinal);
        Assert.Contains(
            "《青铜钥匙》（伏笔）计划第6章揭示，还有 1 章｜已推进 0 次（尚未推进）",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains("风险：", text, StringComparison.Ordinal);
        Assert.Contains("但已写章节中从未推进过该线", text, StringComparison.Ordinal);
        Assert.Contains("计划转折：", text, StringComparison.Ordinal);
        Assert.Contains("第7章", text, StringComparison.Ordinal);
        Assert.Contains("规划缺口：", text, StringComparison.Ordinal);
        Assert.Contains("角色「北境使者」", text, StringComparison.Ordinal);
        Assert.Contains("笔记线索：", text, StringComparison.Ordinal);
        Assert.Contains("《半块玉的递法》第7章", text, StringComparison.Ordinal);
        Assert.Contains("笔记待确认：1 条", text, StringComparison.Ordinal);
        Assert.Contains("前文证据：", text, StringComparison.Ordinal);
        Assert.True(text.Length <= PlotForecastFormatter.MaxChars + 16, $"块过长：{text.Length}");
    }
}
