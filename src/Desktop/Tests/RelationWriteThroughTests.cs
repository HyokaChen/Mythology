using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 「关联」双向编辑的写穿透层（<see cref="RelationWriteThrough"/>，规划 §3/§4）：
/// 真实临时目录 <see cref="FileNovelStore"/> 落盘 → 应用挂起编辑 → 重新读盘断言双端一致。
/// 覆盖：成员双写与保存时对账、地点防环、相邻距离校验、场景/章节单值设清、
/// 情节锚点替换、道具持有替换、分卷归属不重排、悬空对端跳过。
/// </summary>
public sealed class RelationWriteThroughTests : IDisposable
{
    private const string NovelId = "n1";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public RelationWriteThroughTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-relation-wt-{Guid.NewGuid():N}");
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

    // ── 构造 / 应用 / 重载辅助 ──────────────────────────────────────

    private static PendingRelationEdit Add(
        string edge,
        bool ownerIsSource,
        EntityType counterpart,
        string counterpartId,
        string? detail = null
    ) => new(edge, ownerIsSource, counterpart, counterpartId, RelationEditKind.Add, detail);

    private static PendingRelationEdit Remove(
        string edge,
        bool ownerIsSource,
        EntityType counterpart,
        string counterpartId
    ) => new(edge, ownerIsSource, counterpart, counterpartId, RelationEditKind.Remove, null);

    private Task ApplyAsync(Entity owner, params PendingRelationEdit[] edits) =>
        RelationWriteThrough.ApplyAllAsync(_store, NovelId, owner, MembershipSnapshot.Empty, edits);

    private Task ApplyWithSnapshotAsync(
        Entity owner,
        MembershipSnapshot snapshot,
        params PendingRelationEdit[] edits
    ) => RelationWriteThrough.ApplyAllAsync(_store, NovelId, owner, snapshot, edits);

    private Task SeedNovelAsync() =>
        _store.SaveNovel(new Novel { Id = NovelId, Title = "写穿透测试" });

    private async Task<Character> ReloadCharacter(string id) => (await _store.GetCharacter(id))!;

    private async Task<Faction> ReloadFaction(string id) => (await _store.GetFaction(id))!;

    private async Task<Location> ReloadLocation(string id) => (await _store.GetLocation(id))!;

    private async Task<SceneDefinition> ReloadScene(string id) => (await _store.GetScene(id))!;

    private async Task<Item> ReloadItem(string id) => (await _store.GetItem(id))!;

    private async Task<Chapter> ReloadChapter(string id) => (await _store.GetChapter(id))!;

    private async Task<Volume> ReloadVolume(string id) => (await _store.GetVolume(id))!;

    private async Task<Plotline> ReloadPlotline(string id) => (await _store.GetPlotline(id))!;

    // ── 成员：角色侧（单值 · 双写）──────────────────────────────────

    [Fact]
    public async Task Membership_CharacterAddFaction_WritesBothSides()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(new Faction { Id = "f1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });

        var c1 = await ReloadCharacter("c1");
        await ApplyWithSnapshotAsync(
            c1,
            RelationWriteThrough.Capture(c1),
            Add("membership", true, EntityType.Faction, "f1")
        );

        Assert.Equal("f1", (await ReloadCharacter("c1")).FactionId);
        Assert.Contains("c1", (await ReloadFaction("f1")).Members);
    }

    [Fact]
    public async Task Membership_CharacterSwitchFaction_EvictsOldJoinsNew()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = NovelId,
                Members = ["c1"],
            }
        );
        await _store.SaveFaction(new Faction { Id = "f2", NovelId = NovelId });
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = NovelId,
                FactionId = "f1",
            }
        );

        var c1 = await ReloadCharacter("c1");
        await ApplyWithSnapshotAsync(
            c1,
            RelationWriteThrough.Capture(c1),
            Add("membership", true, EntityType.Faction, "f2")
        );

        Assert.Equal("f2", (await ReloadCharacter("c1")).FactionId);
        Assert.DoesNotContain("c1", (await ReloadFaction("f1")).Members);
        Assert.Contains("c1", (await ReloadFaction("f2")).Members);
    }

    [Fact]
    public async Task Membership_CharacterRemoveFaction_ClearsBothSides()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = NovelId,
                Members = ["c1"],
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = NovelId,
                FactionId = "f1",
            }
        );

        var c1 = await ReloadCharacter("c1");
        await ApplyWithSnapshotAsync(
            c1,
            RelationWriteThrough.Capture(c1),
            Remove("membership", true, EntityType.Faction, "f1")
        );

        Assert.Null((await ReloadCharacter("c1")).FactionId);
        Assert.Empty((await ReloadFaction("f1")).Members);
    }

    // ── 成员：组织侧 ────────────────────────────────────────────────

    [Fact]
    public async Task Membership_FactionAddMember_JoinsAndSwitchesFromOld()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(new Faction { Id = "f1", NovelId = NovelId });
        await _store.SaveFaction(new Faction { Id = "f2", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });

        // 冷加入：两侧同时建立
        var f1 = await ReloadFaction("f1");
        await ApplyWithSnapshotAsync(
            f1,
            RelationWriteThrough.Capture(f1),
            Add("membership", false, EntityType.Character, "c1")
        );
        Assert.Contains("c1", (await ReloadFaction("f1")).Members);
        Assert.Equal("f1", (await ReloadCharacter("c1")).FactionId);

        // 换组织：旧组织出列（双写）
        var f2 = await ReloadFaction("f2");
        await ApplyWithSnapshotAsync(
            f2,
            RelationWriteThrough.Capture(f2),
            Add("membership", false, EntityType.Character, "c1")
        );
        Assert.Equal("f2", (await ReloadCharacter("c1")).FactionId);
        Assert.DoesNotContain("c1", (await ReloadFaction("f1")).Members);
        Assert.Contains("c1", (await ReloadFaction("f2")).Members);
    }

    [Fact]
    public async Task Membership_FactionRemoveMember_RemovesOrphanRowToo()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = NovelId,
                Members = ["c1", "c2"],
            }
        );
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = NovelId,
                FactionId = "f1",
            }
        );
        await _store.SaveCharacter(new Character { Id = "c2", NovelId = NovelId }); // 孤儿行

        var f1 = await ReloadFaction("f1");
        await ApplyWithSnapshotAsync(
            f1,
            RelationWriteThrough.Capture(f1),
            Remove("membership", false, EntityType.Character, "c1"),
            Remove("membership", false, EntityType.Character, "c2")
        );

        Assert.Empty((await ReloadFaction("f1")).Members);
        Assert.Null((await ReloadCharacter("c1")).FactionId);
        Assert.Null((await ReloadCharacter("c2")).FactionId);
    }

    // ── 成员：主 Tab 保存时对账（快照 diff）─────────────────────────

    [Fact]
    public async Task Membership_ReconcileFactionSnapshot_BackfillsAndClears()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(new Faction { Id = "f1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });

        // 主 Tab：复选加入成员（只改 Faction.Members）→ 保存时对账补 FactionId
        var f1 = await ReloadFaction("f1");
        var snapshot = RelationWriteThrough.Capture(f1);
        f1.Members.Add("c1");
        await ApplyWithSnapshotAsync(f1, snapshot);

        Assert.Equal("f1", (await ReloadCharacter("c1")).FactionId);
        Assert.Contains("c1", (await ReloadFaction("f1")).Members);

        // 反向：移除成员 → 对账清 FactionId
        var f1After = await ReloadFaction("f1");
        var snapshot2 = RelationWriteThrough.Capture(f1After);
        f1After.Members.Remove("c1");
        await ApplyWithSnapshotAsync(f1After, snapshot2);

        Assert.Null((await ReloadCharacter("c1")).FactionId);
        Assert.DoesNotContain("c1", (await ReloadFaction("f1")).Members);
    }

    [Fact]
    public async Task Membership_ReconcileCharacterSwitch_MovesBetweenFactions()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = NovelId,
                Members = ["c1"],
            }
        );
        await _store.SaveFaction(new Faction { Id = "f2", NovelId = NovelId });
        await _store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = NovelId,
                FactionId = "f1",
            }
        );

        // 主 Tab 改「所属组织」：保存时对账（旧组织出列 + 新组织入列）
        var c1 = await ReloadCharacter("c1");
        var snapshot = RelationWriteThrough.Capture(c1);
        c1.FactionId = "f2";
        await ApplyWithSnapshotAsync(c1, snapshot);

        Assert.Equal("f2", (await ReloadCharacter("c1")).FactionId);
        Assert.DoesNotContain("c1", (await ReloadFaction("f1")).Members);
        Assert.Contains("c1", (await ReloadFaction("f2")).Members);
    }

    // ── 地点：父子（下级/上级）──────────────────────────────────────

    [Fact]
    public async Task Location_ContainsAddRemove_BothDirections()
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });

        // 父侧「下级地点」＋ / ×
        var l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("contains", true, EntityType.Location, "l2"));
        Assert.Equal("l1", (await ReloadLocation("l2")).ParentId);

        l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Remove("contains", true, EntityType.Location, "l2"));
        Assert.Null((await ReloadLocation("l2")).ParentId);

        // 子侧「上级地点」＋ / ×
        var l2 = await ReloadLocation("l2");
        await ApplyAsync(l2, Add("contains", false, EntityType.Location, "l1"));
        Assert.Equal("l1", (await ReloadLocation("l2")).ParentId);

        l2 = await ReloadLocation("l2");
        await ApplyAsync(l2, Remove("contains", false, EntityType.Location, "l1"));
        Assert.Null((await ReloadLocation("l2")).ParentId);
    }

    [Fact]
    public async Task Location_ParentCycle_Rejected()
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(
            new Location
            {
                Id = "l2",
                NovelId = NovelId,
                ParentId = "l1",
            }
        );

        // 子侧：把 l1 挂到其后代 l2 之下 → 拒绝
        var l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("contains", false, EntityType.Location, "l2"));
        Assert.Null((await ReloadLocation("l1")).ParentId);

        // 父侧：把祖先 l1 加为 l2 的下级 → 拒绝
        var l2 = await ReloadLocation("l2");
        await ApplyAsync(l2, Add("contains", true, EntityType.Location, "l1"));
        Assert.Null((await ReloadLocation("l1")).ParentId);

        // 自环：挂到自己 → 拒绝
        l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("contains", false, EntityType.Location, "l1"));
        Assert.Null((await ReloadLocation("l1")).ParentId);

        // 原有父子链不受影响
        Assert.Equal("l1", (await ReloadLocation("l2")).ParentId);
    }

    [Fact]
    public void WouldCycle_DetectsSelfAndDescendant()
    {
        var parents = new Dictionary<string, string?>
        {
            ["a"] = null,
            ["b"] = "a",
            ["c"] = "b",
        };

        Assert.True(RelationWriteThrough.WouldCycle(parents, "a", "a"));
        Assert.True(RelationWriteThrough.WouldCycle(parents, "a", "c"));
        Assert.False(RelationWriteThrough.WouldCycle(parents, "c", "a"));
        Assert.False(RelationWriteThrough.WouldCycle(parents, "c", "z"));
    }

    // ── 地点：相邻（对称 · 距离校验）────────────────────────────────

    [Fact]
    public async Task Adjacency_Add_WritesBothEndsWithDistance()
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });

        var l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("adjacent-to", true, EntityType.Location, "l2", detail: "30"));

        var fromL1 = Assert.Single(
            (await ReloadLocation("l1")).AdjacentLocations!,
            a => a.TargetId == "l2"
        );
        Assert.Equal(30d, fromL1.DistanceLi);
        var fromL2 = Assert.Single(
            (await ReloadLocation("l2")).AdjacentLocations!,
            a => a.TargetId == "l1"
        );
        Assert.Equal(30d, fromL2.DistanceLi);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Adjacency_InvalidDistance_Rejected(string? detail)
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });

        var l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("adjacent-to", true, EntityType.Location, "l2", detail));

        Assert.DoesNotContain(
            (await ReloadLocation("l1")).AdjacentLocations ?? [],
            a => a.TargetId == "l2"
        );
        Assert.DoesNotContain(
            (await ReloadLocation("l2")).AdjacentLocations ?? [],
            a => a.TargetId == "l1"
        );
    }

    [Fact]
    public async Task Adjacency_Remove_ClearsBothEnds()
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });

        var l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Add("adjacent-to", true, EntityType.Location, "l2", detail: "30"));

        l1 = await ReloadLocation("l1");
        await ApplyAsync(l1, Remove("adjacent-to", true, EntityType.Location, "l2"));

        Assert.DoesNotContain(
            (await ReloadLocation("l1")).AdjacentLocations ?? [],
            a => a.TargetId == "l2"
        );
        Assert.DoesNotContain(
            (await ReloadLocation("l2")).AdjacentLocations ?? [],
            a => a.TargetId == "l1"
        );
    }

    // ── 场景：地点 / 章节（单值设清）────────────────────────────────

    [Fact]
    public async Task Scene_Location_SetReplaceClear()
    {
        await SeedNovelAsync();
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });
        await _store.SaveScene(new SceneDefinition { Id = "s1", NovelId = NovelId });

        // 场景侧「场所」＋ → 单值替换
        var s1 = await ReloadScene("s1");
        await ApplyAsync(s1, Add("located-at", true, EntityType.Location, "l1"));
        Assert.Equal("l1", (await ReloadScene("s1")).LocationId);

        s1 = await ReloadScene("s1");
        await ApplyAsync(s1, Add("located-at", true, EntityType.Location, "l2"));
        Assert.Equal("l2", (await ReloadScene("s1")).LocationId);

        // 地点侧「所属场景」× → 清空场景的场所
        var l2 = await ReloadLocation("l2");
        await ApplyAsync(l2, Remove("located-at", false, EntityType.Scene, "s1"));
        Assert.Null((await ReloadScene("s1")).LocationId);
    }

    [Fact]
    public async Task Scene_Chapter_SetAndClear_BothDirections()
    {
        await SeedNovelAsync();
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Order = 1,
            }
        );
        await _store.SaveScene(new SceneDefinition { Id = "s1", NovelId = NovelId });

        // 场景侧「所属章节」＋ / ×
        var s1 = await ReloadScene("s1");
        await ApplyAsync(s1, Add("in-chapter", true, EntityType.Chapter, "ch1"));
        Assert.Equal("ch1", (await ReloadScene("s1")).ChapterId);

        s1 = await ReloadScene("s1");
        await ApplyAsync(s1, Remove("in-chapter", true, EntityType.Chapter, "ch1"));
        Assert.Null((await ReloadScene("s1")).ChapterId);

        // 章节侧「关联场景」＋ / × → 写场景的所属章节
        var ch1 = await ReloadChapter("ch1");
        await ApplyAsync(ch1, Add("in-chapter", false, EntityType.Scene, "s1"));
        Assert.Equal("ch1", (await ReloadScene("s1")).ChapterId);

        ch1 = await ReloadChapter("ch1");
        await ApplyAsync(ch1, Remove("in-chapter", false, EntityType.Scene, "s1"));
        Assert.Null((await ReloadScene("s1")).ChapterId);
    }

    // ── 情节线：关联场景 ────────────────────────────────────────────

    [Fact]
    public async Task Plotline_BelongsToScene_AddRemove()
    {
        await SeedNovelAsync();
        await _store.SavePlotline(new Plotline { Id = "p1", NovelId = NovelId });
        await _store.SaveScene(new SceneDefinition { Id = "s1", NovelId = NovelId });

        var p1 = await ReloadPlotline("p1");
        await ApplyAsync(p1, Add("belongs-to", false, EntityType.Scene, "s1"));
        Assert.Contains("p1", (await ReloadScene("s1")).PlotlineIds);

        p1 = await ReloadPlotline("p1");
        await ApplyAsync(p1, Remove("belongs-to", false, EntityType.Scene, "s1"));
        Assert.DoesNotContain("p1", (await ReloadScene("s1")).PlotlineIds);
    }

    // ── 道具：持有者（单值替换）────────────────────────────────────

    [Fact]
    public async Task Item_Ownership_SetReplaceClear()
    {
        await SeedNovelAsync();
        await _store.SaveItem(new Item { Id = "i1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c2", NovelId = NovelId });

        var i1 = await ReloadItem("i1");
        await ApplyAsync(i1, Add("ownership", true, EntityType.Character, "c1"));
        Assert.Equal("c1", (await ReloadItem("i1")).OwnerId);

        // 单值替换
        i1 = await ReloadItem("i1");
        await ApplyAsync(i1, Add("ownership", true, EntityType.Character, "c2"));
        Assert.Equal("c2", (await ReloadItem("i1")).OwnerId);

        i1 = await ReloadItem("i1");
        await ApplyAsync(i1, Remove("ownership", true, EntityType.Character, "c2"));
        Assert.Null((await ReloadItem("i1")).OwnerId);
    }

    [Fact]
    public async Task ItemOwnership_FromCharacterSide_ReplacesAndClears()
    {
        await SeedNovelAsync();
        await _store.SaveItem(
            new Item
            {
                Id = "i1",
                NovelId = NovelId,
                OwnerId = "c1",
            }
        );
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c2", NovelId = NovelId });

        // 角色侧「持有道具」＋ → 替换原持有者
        var c2 = await ReloadCharacter("c2");
        await ApplyAsync(c2, Add("ownership", false, EntityType.Item, "i1"));
        Assert.Equal("c2", (await ReloadItem("i1")).OwnerId);

        c2 = await ReloadCharacter("c2");
        await ApplyAsync(c2, Remove("ownership", false, EntityType.Item, "i1"));
        Assert.Null((await ReloadItem("i1")).OwnerId);
    }

    // ── 章节：情节锚点（计划揭示 / 已解决）──────────────────────────

    [Fact]
    public async Task Chapter_PlotlineAnchors_ReplaceAndClear()
    {
        await SeedNovelAsync();
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch2",
                NovelId = NovelId,
                Order = 2,
            }
        );
        await _store.SavePlotline(new Plotline { Id = "p1", NovelId = NovelId });

        // reveals-at：替换 + 清空
        var ch1 = await ReloadChapter("ch1");
        await ApplyAsync(ch1, Add("reveals-at", false, EntityType.Plotline, "p1"));
        Assert.Equal("ch1", (await ReloadPlotline("p1")).PlannedRevealChapterId);

        var ch2 = await ReloadChapter("ch2");
        await ApplyAsync(ch2, Add("reveals-at", false, EntityType.Plotline, "p1"));
        Assert.Equal("ch2", (await ReloadPlotline("p1")).PlannedRevealChapterId);

        ch2 = await ReloadChapter("ch2");
        await ApplyAsync(ch2, Remove("reveals-at", false, EntityType.Plotline, "p1"));
        Assert.Null((await ReloadPlotline("p1")).PlannedRevealChapterId);

        // resolves-at：设 + 清
        ch1 = await ReloadChapter("ch1");
        await ApplyAsync(ch1, Add("resolves-at", false, EntityType.Plotline, "p1"));
        Assert.Equal("ch1", (await ReloadPlotline("p1")).ResolvedAtChapterId);

        ch1 = await ReloadChapter("ch1");
        await ApplyAsync(ch1, Remove("resolves-at", false, EntityType.Plotline, "p1"));
        Assert.Null((await ReloadPlotline("p1")).ResolvedAtChapterId);
    }

    // ── 分卷：包含章节（替换 · 不重排）──────────────────────────────

    [Fact]
    public async Task Volume_InVolume_ReplacesAndKeepsOrder()
    {
        await SeedNovelAsync();
        await _store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = NovelId,
                Order = 1,
            }
        );
        await _store.SaveVolume(
            new Volume
            {
                Id = "v2",
                NovelId = NovelId,
                Order = 2,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Order = 5,
                VolumeId = "v2",
            }
        );

        var v1 = await ReloadVolume("v1");
        await ApplyAsync(v1, Add("in-volume", false, EntityType.Chapter, "ch1"));
        var ch = await ReloadChapter("ch1");
        Assert.Equal("v1", ch.VolumeId);
        Assert.Equal(5, ch.Order); // 不重排

        v1 = await ReloadVolume("v1");
        await ApplyAsync(v1, Remove("in-volume", false, EntityType.Chapter, "ch1"));
        ch = await ReloadChapter("ch1");
        Assert.Null(ch.VolumeId);
        Assert.Equal(5, ch.Order);
    }

    // ── 兜底：空 / 悬空对端跳过（不抛、不断批）──────────────────────

    [Fact]
    public async Task Edge_SkipsEmptyAndDanglingCounterparts()
    {
        await SeedNovelAsync();
        await _store.SaveFaction(new Faction { Id = "f1", NovelId = NovelId });
        await _store.SaveCharacter(new Character { Id = "c1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l1", NovelId = NovelId });
        await _store.SaveLocation(new Location { Id = "l2", NovelId = NovelId });

        // 悬空组织 + 空 id 被跳过，后续有效编辑照常应用
        var c1 = await ReloadCharacter("c1");
        await ApplyWithSnapshotAsync(
            c1,
            RelationWriteThrough.Capture(c1),
            Add("membership", true, EntityType.Faction, "f-missing"),
            Add("membership", true, EntityType.Faction, ""),
            Add("membership", true, EntityType.Faction, "f1")
        );
        Assert.Equal("f1", (await ReloadCharacter("c1")).FactionId);
        Assert.Contains("c1", (await ReloadFaction("f1")).Members);

        var l1 = await ReloadLocation("l1");
        await ApplyAsync(
            l1,
            Add("contains", true, EntityType.Location, "l-missing"),
            Add("contains", true, EntityType.Location, ""),
            Add("contains", true, EntityType.Location, "l2")
        );
        Assert.Equal("l1", (await ReloadLocation("l2")).ParentId);
    }
}
