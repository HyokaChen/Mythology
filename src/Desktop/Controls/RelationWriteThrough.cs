using System.Globalization;
using Core.Data;
using Core.Evolution;

namespace Desktop.Controls;

/// <summary>挂起编辑的动作类型。</summary>
public enum RelationEditKind
{
    /// <summary>建立关联。</summary>
    Add,

    /// <summary>移除关联。</summary>
    Remove,
}

/// <summary>
/// 「关联」Tab 的挂起编辑（仅暂存；点「保存」时由 <see cref="RelationWriteThrough"/> 统一应用）。
/// <c>Detail</c> 目前仅 adjacent-to 使用（距离「里」，InvariantCulture 数字串）。
/// </summary>
public sealed record PendingRelationEdit(
    string EdgeType,
    bool OwnerIsSource,
    EntityType CounterpartType,
    string CounterpartId,
    RelationEditKind Kind,
    string? Detail
);

/// <summary>成员对账快照（编辑窗打开时采集；保存时 diff 并补齐双写）。</summary>
public sealed record MembershipSnapshot(
    string? FactionId = null,
    IReadOnlyList<string>? Members = null
)
{
    /// <summary>空快照（新建模式，或非成员实体）。</summary>
    public static readonly MembershipSnapshot Empty = new();
}

/// <summary>实体关联节定义（查看侧视角：边 + 方向 + 对端类型）。</summary>
public sealed record RelationSectionDef(
    string EdgeType,
    bool OwnerIsSource,
    EntityType CounterpartType
);

/// <summary>
/// 「关联」双向编辑的写穿透层（规划 §3/§4）：
/// 把关联节的挂起编辑翻译为对 store 的定向写入；成员关系含保存时机对账（快照 diff 补齐双写）。
/// 全部读写走既有 <see cref="INovelStore"/> API，不落盘任何派生数据；单条编辑「按当前状态再判定」，重试幂等。
/// </summary>
public static class RelationWriteThrough
{
    // ── 快照 / 节定义 / 判定 ──────────────────────────────────────────

    /// <summary>采集成员对账快照（编辑窗 OnOpened 时调用；必须在用户可编辑之前）。</summary>
    public static MembershipSnapshot Capture(Entity entity) =>
        entity switch
        {
            Character c => new MembershipSnapshot(
                FactionId: string.IsNullOrEmpty(c.FactionId) ? null : c.FactionId
            ),
            Faction f => new MembershipSnapshot(Members: [.. f.Members]),
            _ => MembershipSnapshot.Empty,
        };

    /// <summary>该查看侧应渲染的「可编辑」关联节（即使暂无行也渲染，保留「＋」入口）；顺序按 <see cref="EntityRelationIndex.OrderOf"/>。</summary>
    public static IReadOnlyList<RelationSectionDef> EditableSectionsFor(EntityType viewer) =>
        viewer switch
        {
            EntityType.Location =>
            [
                new("contains", true, EntityType.Location),
                new("contains", false, EntityType.Location),
                new("adjacent-to", true, EntityType.Location),
                new("located-at", false, EntityType.Scene),
                new("headquarters", false, EntityType.Faction),
            ],
            EntityType.Character =>
            [
                new("membership", true, EntityType.Faction),
                new("leadership", false, EntityType.Faction),
                new("appears-in", false, EntityType.Scene),
                new("ownership", false, EntityType.Item),
                new("features", false, EntityType.Chapter),
                new("features", false, EntityType.Volume),
            ],
            EntityType.Faction =>
            [
                new("membership", false, EntityType.Character),
                new("leadership", true, EntityType.Character),
                new("headquarters", true, EntityType.Location),
            ],
            EntityType.Plotline =>
            [
                new("advances", false, EntityType.Chapter),
                new("advances", false, EntityType.Volume),
                new("belongs-to", false, EntityType.Scene),
                new("reveals-at", true, EntityType.Chapter),
                new("resolves-at", true, EntityType.Chapter),
            ],
            EntityType.Scene =>
            [
                new("located-at", true, EntityType.Location),
                new("in-chapter", true, EntityType.Chapter),
                new("belongs-to", true, EntityType.Plotline),
                new("appears-in", false, EntityType.Character),
                new("appears-in", false, EntityType.Item),
            ],
            EntityType.Item =>
            [
                new("ownership", true, EntityType.Character),
                new("appears-in", false, EntityType.Scene),
            ],
            EntityType.Chapter =>
            [
                new("in-volume", true, EntityType.Volume),
                new("in-chapter", false, EntityType.Scene),
                new("advances", true, EntityType.Plotline),
                new("features", true, EntityType.Character),
                new("reveals-at", false, EntityType.Plotline),
                new("resolves-at", false, EntityType.Plotline),
            ],
            EntityType.Volume =>
            [
                new("in-volume", false, EntityType.Chapter),
                new("advances", true, EntityType.Plotline),
                new("features", true, EntityType.Character),
            ],
            _ => [],
        };

    /// <summary>单值边：新增即替换（暂存视觉=旧行划删线 + 新行；应用时直接覆盖写）。</summary>
    public static bool IsSingleValue(
        EntityType viewer,
        string edgeType,
        bool ownerIsSource,
        EntityType counterpart
    ) =>
        (viewer, edgeType, ownerIsSource, counterpart) switch
        {
            (EntityType.Character, "membership", true, EntityType.Faction) => true,
            (EntityType.Faction, "leadership", true, EntityType.Character) => true,
            (EntityType.Faction, "headquarters", true, EntityType.Location) => true,
            (EntityType.Location, "contains", false, EntityType.Location) => true,
            (EntityType.Scene, "located-at", true, EntityType.Location) => true,
            (EntityType.Scene, "in-chapter", true, EntityType.Chapter) => true,
            (EntityType.Item, "ownership", true, EntityType.Character) => true,
            (EntityType.Plotline, "reveals-at", true, EntityType.Chapter) => true,
            (EntityType.Plotline, "resolves-at", true, EntityType.Chapter) => true,
            (EntityType.Chapter, "in-volume", true, EntityType.Volume) => true,
            _ => false,
        };

    /// <summary>
    /// 地点防环：把 <paramref name="nodeToReparent"/> 挂到 <paramref name="newParentId"/> 之下是否会成环
    /// （自环一律命中；否则沿 ParentId 链上行走，若遇到被挂节点即成环）。
    /// </summary>
    public static bool WouldCycle(
        IReadOnlyDictionary<string, string?> parents,
        string nodeToReparent,
        string newParentId
    )
    {
        if (string.Equals(nodeToReparent, newParentId, StringComparison.Ordinal))
            return true;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = newParentId;
        while (!string.IsNullOrEmpty(current) && seen.Add(current))
        {
            if (string.Equals(current, nodeToReparent, StringComparison.Ordinal))
                return true;
            current = parents.TryGetValue(current, out var parent) ? parent : null;
        }
        return false;
    }

    // ── 保存入口 ──────────────────────────────────────────────────────

    /// <summary>
    /// 统一应用：①成员对账（快照 diff）→ ②逐条应用挂起编辑（内存改写本体与对端）→ ③统一落盘（被触实体 + 本体）。
    /// 顺序确定：主 Tab 字段 → 成员对账 → 关联编辑（同字段两处被编辑时后者胜）。异常向上抛（窗口保留，无回滚，规划 §4.6）。
    /// </summary>
    public static async Task ApplyAllAsync(
        INovelStore store,
        string novelId,
        Entity owner,
        MembershipSnapshot snapshot,
        IReadOnlyList<PendingRelationEdit> edits
    )
    {
        using var _ = OntologyWriteContext.BeginUser();
        var context = new ApplyContext(store, novelId);

        switch (owner)
        {
            case Character character:
                await ReconcileCharacterAsync(context, character, snapshot);
                break;
            case Faction faction:
                await ReconcileFactionAsync(context, faction, snapshot);
                break;
        }

        foreach (var edit in edits)
            await ApplyEditAsync(context, owner, edit);

        foreach (var touched in context.Touched)
            await SaveEntityByTypeAsync(store, touched);
        await SaveEntityByTypeAsync(store, owner);
    }

    /// <summary>按具体类型派发保存（从 EntityEditWindow 抽出的共享实现）。</summary>
    public static Task SaveEntityByTypeAsync(INovelStore store, Entity entity) =>
        entity switch
        {
            Character character => store.SaveCharacter(character),
            Plotline plotline => store.SavePlotline(plotline),
            Faction faction => store.SaveFaction(faction),
            Location location => store.SaveLocation(location),
            SceneDefinition scene => store.SaveScene(scene),
            Item item => store.SaveItem(item),
            Relationship relationship => store.SaveRelationship(relationship),
            Chapter chapter => store.SaveChapter(chapter),
            Volume volume => store.SaveVolume(volume),
            Novel novel => store.SaveNovel(novel),
            AuthorNote note => store.SaveNote(note),
            _ => throw new InvalidOperationException($"不支持的实体类型：{entity.GetType().Name}"),
        };

    // ── 成员对账（§4.1）─────────────────────────────────────────────

    /// <summary>角色侧对账：FactionId 快照 → 终值 的位移，补齐两侧（老组织出列、新组织入列）。</summary>
    private static async Task ReconcileCharacterAsync(
        ApplyContext context,
        Character character,
        MembershipSnapshot snapshot
    )
    {
        var oldId = string.IsNullOrEmpty(snapshot.FactionId) ? null : snapshot.FactionId;
        var newId = string.IsNullOrEmpty(character.FactionId) ? null : character.FactionId;
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
            return;

        if (oldId is not null)
        {
            var oldFaction = await context.LoadFaction(oldId);
            if (oldFaction is not null && oldFaction.Members.Remove(character.Id))
                context.Touch(oldFaction);
        }

        if (newId is not null)
        {
            var newFaction = await context.LoadFaction(newId);
            if (newFaction is not null && !newFaction.Members.Contains(character.Id))
            {
                newFaction.Members.Add(character.Id);
                context.Touch(newFaction);
            }
        }
    }

    /// <summary>组织侧对账：Members 快照 diff → 新增成员补 FactionId、移除成员清 FactionId（悬空跳过）。</summary>
    private static async Task ReconcileFactionAsync(
        ApplyContext context,
        Faction faction,
        MembershipSnapshot snapshot
    )
    {
        var oldMembers = snapshot.Members ?? [];
        var oldSet = new HashSet<string>(oldMembers, StringComparer.Ordinal);
        var newSet = new HashSet<string>(faction.Members, StringComparer.Ordinal);

        foreach (var memberId in newSet)
        {
            if (oldSet.Contains(memberId))
                continue;
            var member = await context.LoadCharacter(memberId);
            if (
                member is null
                || string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal)
            )
                continue;
            var oldFactionId = member.FactionId;
            if (!string.IsNullOrEmpty(oldFactionId))
            {
                var oldFaction = await context.LoadFaction(oldFactionId);
                if (oldFaction is not null && oldFaction.Members.Remove(memberId))
                    context.Touch(oldFaction);
            }
            member.FactionId = faction.Id;
            context.Touch(member);
        }

        foreach (var memberId in oldSet)
        {
            if (newSet.Contains(memberId))
                continue;
            var member = await context.LoadCharacter(memberId);
            if (
                member is null
                || !string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal)
            )
                continue;
            member.FactionId = null;
            context.Touch(member);
        }
    }

    // ── 挂起编辑应用（§3 写穿透规则表）──────────────────────────────

    private static async Task ApplyEditAsync(
        ApplyContext context,
        Entity owner,
        PendingRelationEdit edit
    )
    {
        if (string.IsNullOrEmpty(edit.CounterpartId))
            return;
        // 自环兜底：关联节候选已排除本体，此处再防一手（§4.4）。
        if (
            TypeOf(owner) == edit.CounterpartType
            && string.Equals(owner.Id, edit.CounterpartId, StringComparison.Ordinal)
        )
            return;

        switch (owner)
        {
            case Character character:
                await ApplyCharacterEditAsync(context, character, edit);
                break;
            case Faction faction:
                await ApplyFactionEditAsync(context, faction, edit);
                break;
            case Location location:
                await ApplyLocationEditAsync(context, location, edit);
                break;
            case SceneDefinition scene:
                await ApplySceneEditAsync(context, scene, edit);
                break;
            case Item item:
                await ApplyItemEditAsync(context, item, edit);
                break;
            case Plotline plotline:
                await ApplyPlotlineEditAsync(context, plotline, edit);
                break;
            case Chapter chapter:
                await ApplyChapterEditAsync(context, chapter, edit);
                break;
            case Volume volume:
                await ApplyVolumeEditAsync(context, volume, edit);
                break;
        }
    }

    /// <summary>角色侧：所属组织（单值·双写）/ 领导组织（集合）/ 参与场景 / 持有道具 / 出场章节·卷。</summary>
    private static async Task ApplyCharacterEditAsync(
        ApplyContext context,
        Character character,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "membership" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Faction:
            {
                var faction = await context.LoadFaction(edit.CounterpartId);
                if (faction is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    // 单值切换：旧组织出列（双写）
                    var oldId = character.FactionId;
                    if (!string.Equals(oldId, faction.Id, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(oldId))
                        {
                            var oldFaction = await context.LoadFaction(oldId);
                            if (oldFaction is not null && oldFaction.Members.Remove(character.Id))
                                context.Touch(oldFaction);
                        }
                        character.FactionId = faction.Id;
                    }
                    if (!faction.Members.Contains(character.Id))
                    {
                        faction.Members.Add(character.Id);
                        context.Touch(faction);
                    }
                }
                else
                {
                    if (string.Equals(character.FactionId, faction.Id, StringComparison.Ordinal))
                        character.FactionId = null;
                    if (faction.Members.Remove(character.Id))
                        context.Touch(faction);
                }
                break;
            }

            case "leadership"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Faction:
            {
                var faction = await context.LoadFaction(edit.CounterpartId);
                if (faction is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!string.Equals(faction.Leader, character.Id, StringComparison.Ordinal))
                    {
                        faction.Leader = character.Id;
                        context.Touch(faction);
                    }
                }
                else if (string.Equals(faction.Leader, character.Id, StringComparison.Ordinal))
                {
                    faction.Leader = "";
                    context.Touch(faction);
                }
                break;
            }

            case "appears-in" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Scene:
            {
                var scene = await context.LoadScene(edit.CounterpartId);
                if (scene is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!scene.ParticipantIds.Contains(character.Id))
                    {
                        scene.ParticipantIds.Add(character.Id);
                        context.Touch(scene);
                    }
                }
                else if (scene.ParticipantIds.Remove(character.Id))
                {
                    context.Touch(scene);
                }
                break;
            }

            case "ownership" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Item:
            {
                var item = await context.LoadItem(edit.CounterpartId);
                if (item is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    // 替换原持有者；时窗不动（§4.8）
                    if (!string.Equals(item.OwnerId, character.Id, StringComparison.Ordinal))
                    {
                        item.OwnerId = character.Id;
                        context.Touch(item);
                    }
                }
                else if (string.Equals(item.OwnerId, character.Id, StringComparison.Ordinal))
                {
                    item.OwnerId = null;
                    context.Touch(item);
                }
                break;
            }

            case "features" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                var chapter = await context.LoadChapter(edit.CounterpartId);
                if (chapter is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!chapter.CharacterIds.Contains(character.Id))
                    {
                        chapter.CharacterIds.Add(character.Id);
                        context.Touch(chapter);
                    }
                }
                else if (chapter.CharacterIds.Remove(character.Id))
                {
                    context.Touch(chapter);
                }
                break;
            }

            case "features" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Volume:
            {
                var volume = await context.LoadVolume(edit.CounterpartId);
                if (volume is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!volume.CharacterIds.Contains(character.Id))
                    {
                        volume.CharacterIds.Add(character.Id);
                        context.Touch(volume);
                    }
                }
                else if (volume.CharacterIds.Remove(character.Id))
                {
                    context.Touch(volume);
                }
                break;
            }
        }
    }

    /// <summary>组织侧：成员（集合·双写，兼容孤儿行）/ 首领（单值 own）/ 总部（单值 own）。</summary>
    private static async Task ApplyFactionEditAsync(
        ApplyContext context,
        Faction faction,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "membership"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                var member = await context.LoadCharacter(edit.CounterpartId);
                if (member is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    var oldId = member.FactionId;
                    if (!string.Equals(oldId, faction.Id, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(oldId))
                        {
                            var oldFaction = await context.LoadFaction(oldId);
                            if (oldFaction is not null && oldFaction.Members.Remove(member.Id))
                                context.Touch(oldFaction);
                        }
                        member.FactionId = faction.Id;
                        context.Touch(member);
                    }
                    if (!faction.Members.Contains(member.Id))
                        faction.Members.Add(member.Id);
                }
                else
                {
                    // 兼容孤儿：FactionId 未指向本组织、但在 Members 列表中的行
                    faction.Members.Remove(member.Id);
                    if (string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal))
                    {
                        member.FactionId = null;
                        context.Touch(member);
                    }
                }
                break;
            }

            case "leadership"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(faction.Leader, edit.CounterpartId, StringComparison.Ordinal)
                    )
                        faction.Leader = edit.CounterpartId;
                }
                else if (
                    string.Equals(faction.Leader, edit.CounterpartId, StringComparison.Ordinal)
                )
                {
                    faction.Leader = "";
                }
                break;
            }

            case "headquarters"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Location:
            {
                var location = await context.LoadLocation(edit.CounterpartId);
                if (location is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            faction.HeadquartersId,
                            location.Id,
                            StringComparison.Ordinal
                        )
                    )
                        faction.HeadquartersId = location.Id;
                }
                else if (
                    string.Equals(faction.HeadquartersId, location.Id, StringComparison.Ordinal)
                )
                {
                    faction.HeadquartersId = null;
                }
                break;
            }
        }
    }

    /// <summary>地点侧：下级地点（集合）/ 上级地点（单值·防环）/ 相邻（对称·距离）/ 所属场景 / 作为总部。</summary>
    private static async Task ApplyLocationEditAsync(
        ApplyContext context,
        Location location,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "contains" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Location:
            {
                var child = await context.LoadLocation(edit.CounterpartId);
                if (child is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    var parents = await context.ParentsAsync();
                    // 防环：拒绝成环的组合（候选页已过滤，此处兜底静默跳过；§4.4）
                    if (WouldCycle(parents, nodeToReparent: child.Id, newParentId: location.Id))
                        return;
                    if (!string.Equals(child.ParentId, location.Id, StringComparison.Ordinal))
                    {
                        child.ParentId = location.Id;
                        context.UpdateParentMemo(child.Id, location.Id);
                        context.Touch(child);
                    }
                }
                else if (string.Equals(child.ParentId, location.Id, StringComparison.Ordinal))
                {
                    child.ParentId = null;
                    context.UpdateParentMemo(child.Id, null);
                    context.Touch(child);
                }
                break;
            }

            case "contains" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Location:
            {
                var parent = await context.LoadLocation(edit.CounterpartId);
                if (parent is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    var parents = await context.ParentsAsync();
                    if (WouldCycle(parents, nodeToReparent: location.Id, newParentId: parent.Id))
                        return;
                    if (!string.Equals(location.ParentId, parent.Id, StringComparison.Ordinal))
                    {
                        location.ParentId = parent.Id;
                        context.UpdateParentMemo(location.Id, parent.Id);
                    }
                }
                else if (string.Equals(location.ParentId, parent.Id, StringComparison.Ordinal))
                {
                    location.ParentId = null;
                    context.UpdateParentMemo(location.Id, null);
                }
                break;
            }

            case "adjacent-to" when edit.CounterpartType == EntityType.Location:
            {
                var other = await context.LoadLocation(edit.CounterpartId);
                if (other is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!TryParseDistance(edit.Detail, out var distance))
                        return; // 距离无效：拒绝（UI 已校验，此处兜底）
                    UpsertAdjacency(location, other.Id, distance);
                    UpsertAdjacency(other, location.Id, distance);
                    context.Touch(other);
                }
                else
                {
                    RemoveAdjacency(location, other.Id);
                    if (RemoveAdjacency(other, location.Id))
                        context.Touch(other);
                }
                break;
            }

            case "located-at" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Scene:
            {
                var scene = await context.LoadScene(edit.CounterpartId);
                if (scene is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!string.Equals(scene.LocationId, location.Id, StringComparison.Ordinal))
                    {
                        scene.LocationId = location.Id;
                        context.Touch(scene);
                    }
                }
                else if (string.Equals(scene.LocationId, location.Id, StringComparison.Ordinal))
                {
                    scene.LocationId = null;
                    context.Touch(scene);
                }
                break;
            }

            case "headquarters"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Faction:
            {
                var faction = await context.LoadFaction(edit.CounterpartId);
                if (faction is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            faction.HeadquartersId,
                            location.Id,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        faction.HeadquartersId = location.Id;
                        context.Touch(faction);
                    }
                }
                else if (
                    string.Equals(faction.HeadquartersId, location.Id, StringComparison.Ordinal)
                )
                {
                    faction.HeadquartersId = null;
                    context.Touch(faction);
                }
                break;
            }
        }
    }

    /// <summary>场景侧：场所（单值 own）/ 所属章节（单值 own）/ 推进情节线 / 参与角色 / 出场道具。</summary>
    private static async Task ApplySceneEditAsync(
        ApplyContext context,
        SceneDefinition scene,
        PendingRelationEdit edit
    )
    {
        _ = context;
        switch (edit.EdgeType)
        {
            case "located-at"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Location:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            scene.LocationId,
                            edit.CounterpartId,
                            StringComparison.Ordinal
                        )
                    )
                        scene.LocationId = edit.CounterpartId;
                }
                else if (
                    string.Equals(scene.LocationId, edit.CounterpartId, StringComparison.Ordinal)
                )
                {
                    scene.LocationId = null;
                }
                break;
            }

            case "in-chapter" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            scene.ChapterId,
                            edit.CounterpartId,
                            StringComparison.Ordinal
                        )
                    )
                        scene.ChapterId = edit.CounterpartId;
                }
                else if (
                    string.Equals(scene.ChapterId, edit.CounterpartId, StringComparison.Ordinal)
                )
                {
                    scene.ChapterId = null;
                }
                break;
            }

            case "belongs-to"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Plotline:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(scene.PlotlineIds, edit.CounterpartId);
                else
                    scene.PlotlineIds.Remove(edit.CounterpartId);
                break;
            }

            case "appears-in"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(scene.ParticipantIds, edit.CounterpartId);
                else
                    scene.ParticipantIds.Remove(edit.CounterpartId);
                break;
            }

            case "appears-in" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Item:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(scene.ItemIds, edit.CounterpartId);
                else
                    scene.ItemIds.Remove(edit.CounterpartId);
                break;
            }
        }
    }

    /// <summary>道具侧：持有者（单值 own，时窗不动）/ 出场场景（写对端）。</summary>
    private static async Task ApplyItemEditAsync(
        ApplyContext context,
        Item item,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "ownership"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!string.Equals(item.OwnerId, edit.CounterpartId, StringComparison.Ordinal))
                        item.OwnerId = edit.CounterpartId;
                }
                else if (string.Equals(item.OwnerId, edit.CounterpartId, StringComparison.Ordinal))
                {
                    item.OwnerId = null;
                }
                break;
            }

            case "appears-in" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Scene:
            {
                var scene = await context.LoadScene(edit.CounterpartId);
                if (scene is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!scene.ItemIds.Contains(item.Id))
                    {
                        scene.ItemIds.Add(item.Id);
                        context.Touch(scene);
                    }
                }
                else if (scene.ItemIds.Remove(item.Id))
                {
                    context.Touch(scene);
                }
                break;
            }
        }
    }

    /// <summary>情节侧：被推进章节/卷、关联场景（写对端）/ 计划揭示、已解决（单值 own）。</summary>
    private static async Task ApplyPlotlineEditAsync(
        ApplyContext context,
        Plotline plotline,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "advances" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                var chapter = await context.LoadChapter(edit.CounterpartId);
                if (chapter is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!chapter.PlotlineIds.Contains(plotline.Id))
                    {
                        chapter.PlotlineIds.Add(plotline.Id);
                        context.Touch(chapter);
                    }
                }
                else if (chapter.PlotlineIds.Remove(plotline.Id))
                {
                    context.Touch(chapter);
                }
                break;
            }

            case "advances" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Volume:
            {
                var volume = await context.LoadVolume(edit.CounterpartId);
                if (volume is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!volume.PlotlineIds.Contains(plotline.Id))
                    {
                        volume.PlotlineIds.Add(plotline.Id);
                        context.Touch(volume);
                    }
                }
                else if (volume.PlotlineIds.Remove(plotline.Id))
                {
                    context.Touch(volume);
                }
                break;
            }

            case "belongs-to" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Scene:
            {
                var scene = await context.LoadScene(edit.CounterpartId);
                if (scene is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!scene.PlotlineIds.Contains(plotline.Id))
                    {
                        scene.PlotlineIds.Add(plotline.Id);
                        context.Touch(scene);
                    }
                }
                else if (scene.PlotlineIds.Remove(plotline.Id))
                {
                    context.Touch(scene);
                }
                break;
            }

            case "reveals-at" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            plotline.PlannedRevealChapterId,
                            edit.CounterpartId,
                            StringComparison.Ordinal
                        )
                    )
                        plotline.PlannedRevealChapterId = edit.CounterpartId;
                }
                else if (
                    string.Equals(
                        plotline.PlannedRevealChapterId,
                        edit.CounterpartId,
                        StringComparison.Ordinal
                    )
                )
                {
                    plotline.PlannedRevealChapterId = null;
                }
                break;
            }

            case "resolves-at"
                when edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            plotline.ResolvedAtChapterId,
                            edit.CounterpartId,
                            StringComparison.Ordinal
                        )
                    )
                        plotline.ResolvedAtChapterId = edit.CounterpartId;
                }
                else if (
                    string.Equals(
                        plotline.ResolvedAtChapterId,
                        edit.CounterpartId,
                        StringComparison.Ordinal
                    )
                )
                {
                    plotline.ResolvedAtChapterId = null;
                }
                break;
            }
        }
    }

    /// <summary>章节侧：所属分卷（单值 own·不重排）/ 关联场景 / 待揭示、已解决情节（写对端）/ 推进、计划出场（own）。</summary>
    private static async Task ApplyChapterEditAsync(
        ApplyContext context,
        Chapter chapter,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "in-volume" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Volume:
            {
                if (edit.Kind == RelationEditKind.Add)
                {
                    // 只写 VolumeId，章节序号保持（不重排序，§4.7）
                    if (
                        !string.Equals(
                            chapter.VolumeId,
                            edit.CounterpartId,
                            StringComparison.Ordinal
                        )
                    )
                        chapter.VolumeId = edit.CounterpartId;
                }
                else if (
                    string.Equals(chapter.VolumeId, edit.CounterpartId, StringComparison.Ordinal)
                )
                {
                    chapter.VolumeId = null;
                }
                break;
            }

            case "in-chapter" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Scene:
            {
                var scene = await context.LoadScene(edit.CounterpartId);
                if (scene is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (!string.Equals(scene.ChapterId, chapter.Id, StringComparison.Ordinal))
                    {
                        scene.ChapterId = chapter.Id;
                        context.Touch(scene);
                    }
                }
                else if (string.Equals(scene.ChapterId, chapter.Id, StringComparison.Ordinal))
                {
                    scene.ChapterId = null;
                    context.Touch(scene);
                }
                break;
            }

            case "advances" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Plotline:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(chapter.PlotlineIds, edit.CounterpartId);
                else
                    chapter.PlotlineIds.Remove(edit.CounterpartId);
                break;
            }

            case "features" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(chapter.CharacterIds, edit.CounterpartId);
                else
                    chapter.CharacterIds.Remove(edit.CounterpartId);
                break;
            }

            case "reveals-at"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Plotline:
            {
                var plotline = await context.LoadPlotline(edit.CounterpartId);
                if (plotline is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    // 把其锚点替换为本章
                    if (
                        !string.Equals(
                            plotline.PlannedRevealChapterId,
                            chapter.Id,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        plotline.PlannedRevealChapterId = chapter.Id;
                        context.Touch(plotline);
                    }
                }
                else if (
                    string.Equals(
                        plotline.PlannedRevealChapterId,
                        chapter.Id,
                        StringComparison.Ordinal
                    )
                )
                {
                    plotline.PlannedRevealChapterId = null;
                    context.Touch(plotline);
                }
                break;
            }

            case "resolves-at"
                when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Plotline:
            {
                var plotline = await context.LoadPlotline(edit.CounterpartId);
                if (plotline is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    if (
                        !string.Equals(
                            plotline.ResolvedAtChapterId,
                            chapter.Id,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        plotline.ResolvedAtChapterId = chapter.Id;
                        context.Touch(plotline);
                    }
                }
                else if (
                    string.Equals(
                        plotline.ResolvedAtChapterId,
                        chapter.Id,
                        StringComparison.Ordinal
                    )
                )
                {
                    plotline.ResolvedAtChapterId = null;
                    context.Touch(plotline);
                }
                break;
            }
        }
    }

    /// <summary>分卷侧：包含章节（写对端·不重排）/ 推进情节线、关键角色（own）。</summary>
    private static async Task ApplyVolumeEditAsync(
        ApplyContext context,
        Volume volume,
        PendingRelationEdit edit
    )
    {
        switch (edit.EdgeType)
        {
            case "in-volume" when !edit.OwnerIsSource && edit.CounterpartType == EntityType.Chapter:
            {
                var chapter = await context.LoadChapter(edit.CounterpartId);
                if (chapter is null)
                    return;
                if (edit.Kind == RelationEditKind.Add)
                {
                    // 替换归属；不重排序（§4.7）
                    if (!string.Equals(chapter.VolumeId, volume.Id, StringComparison.Ordinal))
                    {
                        chapter.VolumeId = volume.Id;
                        context.Touch(chapter);
                    }
                }
                else if (string.Equals(chapter.VolumeId, volume.Id, StringComparison.Ordinal))
                {
                    chapter.VolumeId = null;
                    context.Touch(chapter);
                }
                break;
            }

            case "advances" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Plotline:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(volume.PlotlineIds, edit.CounterpartId);
                else
                    volume.PlotlineIds.Remove(edit.CounterpartId);
                break;
            }

            case "features" when edit.OwnerIsSource && edit.CounterpartType == EntityType.Character:
            {
                if (edit.Kind == RelationEditKind.Add)
                    ListAdd(volume.CharacterIds, edit.CounterpartId);
                else
                    volume.CharacterIds.Remove(edit.CounterpartId);
                break;
            }
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────

    private static void ListAdd(List<string> list, string id)
    {
        if (!list.Contains(id))
            list.Add(id);
    }

    private static bool TryParseDistance(string? detail, out double distance)
    {
        distance = 0;
        if (string.IsNullOrWhiteSpace(detail))
            return false;
        if (
            !double.TryParse(
                detail,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
        )
            return false;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0)
            return false;
        distance = parsed;
        return true;
    }

    /// <summary>写入/更新一侧的邻接条目（距离覆盖写）。</summary>
    private static void UpsertAdjacency(Location location, string targetId, double distanceLi)
    {
        location.AdjacentLocations ??= [];
        var existing = location.AdjacentLocations.Find(a =>
            string.Equals(a.TargetId, targetId, StringComparison.Ordinal)
        );
        if (existing is null)
        {
            location.AdjacentLocations.Add(
                new LocationAdjacency { TargetId = targetId, DistanceLi = distanceLi }
            );
        }
        else
        {
            existing.DistanceLi = distanceLi;
        }
    }

    /// <summary>移除一侧的邻接条目；返回是否有改动。</summary>
    private static bool RemoveAdjacency(Location location, string targetId) =>
        location.AdjacentLocations is not null
        && location.AdjacentLocations.RemoveAll(a =>
            string.Equals(a.TargetId, targetId, StringComparison.Ordinal)
        ) > 0;

    private static EntityType? TypeOf(Entity entity) =>
        entity switch
        {
            Character => EntityType.Character,
            Faction => EntityType.Faction,
            Location => EntityType.Location,
            SceneDefinition => EntityType.Scene,
            Item => EntityType.Item,
            Plotline => EntityType.Plotline,
            Chapter => EntityType.Chapter,
            Volume => EntityType.Volume,
            Novel => EntityType.Novel,
            Relationship => EntityType.Relationship,
            _ => null,
        };

    // ── 加载上下文（缓存 + 被触实体收集；本体不进 Touched，由 ApplyAllAsync 末尾统一保存）──

    private sealed class ApplyContext(INovelStore store, string novelId)
    {
        private readonly Dictionary<(EntityType Type, string Id), Entity?> _cache = [];
        private Dictionary<string, string?>? _parentMemo;

        /// <summary>被触实体（保存顺序=首次触及顺序）。</summary>
        public List<Entity> Touched { get; } = [];

        public void Touch(Entity entity)
        {
            if (!Touched.Contains(entity))
                Touched.Add(entity);
        }

        public async Task<Character?> LoadCharacter(string id) =>
            (Character?)await GetRaw(EntityType.Character, id);

        public async Task<Faction?> LoadFaction(string id) =>
            (Faction?)await GetRaw(EntityType.Faction, id);

        public async Task<Location?> LoadLocation(string id) =>
            (Location?)await GetRaw(EntityType.Location, id);

        public async Task<SceneDefinition?> LoadScene(string id) =>
            (SceneDefinition?)await GetRaw(EntityType.Scene, id);

        public async Task<Item?> LoadItem(string id) => (Item?)await GetRaw(EntityType.Item, id);

        public async Task<Plotline?> LoadPlotline(string id) =>
            (Plotline?)await GetRaw(EntityType.Plotline, id);

        public async Task<Chapter?> LoadChapter(string id) =>
            (Chapter?)await GetRaw(EntityType.Chapter, id);

        public async Task<Volume?> LoadVolume(string id) =>
            (Volume?)await GetRaw(EntityType.Volume, id);

        private async Task<Entity?> GetRaw(EntityType type, string id)
        {
            if (_cache.TryGetValue((type, id), out var cached))
                return cached;
            Entity? entity = type switch
            {
                EntityType.Character => await store.GetCharacter(id),
                EntityType.Faction => await store.GetFaction(id),
                EntityType.Location => await store.GetLocation(id),
                EntityType.Scene => await store.GetScene(id),
                EntityType.Item => await store.GetItem(id),
                EntityType.Plotline => await store.GetPlotline(id),
                EntityType.Chapter => await store.GetChapter(id),
                EntityType.Volume => await store.GetVolume(id),
                _ => null,
            };
            _cache[(type, id)] = entity;
            return entity;
        }

        /// <summary>全量地点 ParentId 备忘（防环判定用；批次内随写入同步更新）。</summary>
        public async Task<Dictionary<string, string?>> ParentsAsync()
        {
            if (_parentMemo is not null)
                return _parentMemo;
            var memo = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var location in await store.GetLocations(novelId))
                memo[location.Id] = string.IsNullOrEmpty(location.ParentId)
                    ? null
                    : location.ParentId;
            _parentMemo = memo;
            return memo;
        }

        /// <summary>批次内同步 ParentId 备忘（备忘未建立时无操作）。</summary>
        public void UpdateParentMemo(string childId, string? parentId)
        {
            if (_parentMemo is not null)
                _parentMemo[childId] = parentId;
        }
    }
}
