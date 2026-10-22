using Core.Data;
using Core.Evolution;
using Core.Tools;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定面板大纲的「章节生命周期」投影（Desktop 侧）：
/// 章行元数据 / 展开明细用 EntityLogView 显示待发布计数、🔗联动、地点、情节线；
/// 节点按状态给出 IsDraft / IsPublished（菜单发布 / 撤回的显隐依据）；
/// 发布后（Pending → Committed）刷新即不再计入待发布。
/// </summary>
public sealed class SetUpPanelChapterTimelineTests : IDisposable
{
    private const string NovelId = "n1";
    private const string ChapterId = "ch1";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public SetUpPanelChapterTimelineTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-timeline-{Guid.NewGuid():N}");
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

    private SetUpPanelViewModel CreateViewModel() =>
        new(_store, null, new ToolRegistry(), null, () => NovelId, null, action => action());

    [Fact]
    public async Task ChapterRows_ShowPendingCascadeLocationAndPlotline()
    {
        await SeedAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();

        var row = vm.OutlineRows.Single(r => r.Id == ChapterId);
        Assert.Contains("待发布 1", row.MetaLine);
        Assert.Contains("🔗联动 1", row.MetaLine);

        var lines = vm.ChapterRecordLines(ChapterId);
        Assert.Contains(lines, line => line.Contains("（待发布）", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("🔗 联动", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("📍 地点: 隐龙谷", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("🧵 情节线: 主线", StringComparison.Ordinal));

        // 节点状态（菜单发布/撤回显隐）：草稿章 IsDraft
        var chapterNode = vm
            .OutlineNodes.SelectMany(n => n.Children)
            .SelectMany(n => n.Children)
            .Single(n => n.Kind == Core.Novels.OutlineRowKind.Chapter && n.Row.Id == ChapterId);
        Assert.True(chapterNode.IsDraft);
        Assert.False(chapterNode.IsPublished);
    }

    [Fact]
    public async Task PublishingChapter_ClearsPendingBadge_AndFlipsNodeStatus()
    {
        await SeedAsync();
        var vm = CreateViewModel();
        await vm.RefreshAsync();
        Assert.Contains("待发布 1", vm.OutlineRows.Single(r => r.Id == ChapterId).MetaLine);

        var published = await new ChapterLifecycleService(_store).PublishAsync(
            NovelId,
            ChapterId,
            new PublishOptions()
        );
        Assert.True(published.Success, published.Error);

        await vm.RefreshAsync();
        var row = vm.OutlineRows.Single(r => r.Id == ChapterId);
        Assert.DoesNotContain("待发布", row.MetaLine);
        Assert.Contains("变化 1", row.MetaLine); // 已生效的变化仍计数
        Assert.False(
            vm.ChapterRecordLines(ChapterId)
                .Any(line => line.Contains("（待发布）", StringComparison.Ordinal))
        );

        var chapterNode = vm
            .OutlineNodes.SelectMany(n => n.Children)
            .SelectMany(n => n.Children)
            .Single(n => n.Kind == Core.Novels.OutlineRowKind.Chapter && n.Row.Id == ChapterId);
        Assert.False(chapterNode.IsDraft);
        Assert.True(chapterNode.IsPublished);

        // 时间线 Markdown（实体「历史 / 时间线」窗口数据源）= EntityLogFormatter 表格
        var markdown = await vm.BuildAuditMarkdownAsync(EntityType.Character, "char1");
        Assert.Contains("时间线", markdown);
    }

    private async Task SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "时间线测试" });
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = ChapterId,
                NovelId = NovelId,
                Title = "第1章",
                Order = 1,
                VolumeId = "vol1",
                Status = ChapterStatus.Draft,
                Content = "正文",
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "char1",
                NovelId = NovelId,
                Name = "沈砚",
                Role = CharacterRole.Protagonist,
            }
        );
        await _store.SaveLocation(
            new Core.Data.Location
            {
                Id = "loc1",
                NovelId = NovelId,
                Name = "隐龙谷",
            }
        );
        await _store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = NovelId,
                Name = "主线",
            }
        );
        await _store.SaveEntityStateChanges(
            NovelId,
            ChapterId,
            [
                new EntityStateChange
                {
                    Id = "chg1",
                    NovelId = NovelId,
                    TargetEntityType = EntityType.Character,
                    TargetId = "char1",
                    ChapterId = ChapterId,
                    Sequence = 1,
                    Summary = "与师兄决裂",
                    FromSummary = "信任",
                    ToSummary = "敌视",
                    Lifecycle = ChangeLifecycle.Pending,
                    IsCascade = true,
                    LocationId = "loc1",
                    PlotlineIds = ["pl1"],
                    Source = AuditSource.User,
                    Effects =
                    [
                        new StateEffect
                        {
                            TargetType = EntityType.Character,
                            TargetId = "char1",
                            Field = "status",
                            Operation = StateEffectOperation.Set,
                            Value = "Deceased",
                        },
                    ],
                },
            ]
        );
    }
}
