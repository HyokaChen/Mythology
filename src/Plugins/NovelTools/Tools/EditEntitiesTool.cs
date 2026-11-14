using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using EntityTypeEnum = Core.Data.EntityType;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 设定面板全字段批量编辑：一次调用跨多种实体类型按字段修改条目。
/// 字段白名单与 Desktop 编辑窗同源（<see cref="EntityFieldRegistry"/>，位于 Core），
/// 写入全部经 <see cref="INovelStore"/>：面板 OnEntityChanged 自动刷新、向量索引同步。
/// </summary>
public sealed class EditEntitiesTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<EditEntitiesParams, EditEntitiesDetails>
{
    private static readonly EntityType[] SupportedTypes =
    [
        EntityTypeEnum.Novel,
        EntityTypeEnum.Volume,
        EntityTypeEnum.Chapter,
        EntityTypeEnum.Character,
        EntityTypeEnum.Faction,
        EntityTypeEnum.Location,
        EntityTypeEnum.Plotline,
        EntityTypeEnum.Scene,
        EntityTypeEnum.Item,
        EntityTypeEnum.Relationship,
    ];

    private static readonly string FieldTable = BuildFieldTable();

    public override string Name => "edit_entities";
    public override string[] Aliases =>
        ["批量编辑", "批量修改", "编辑实体", "修改字段", "EditEntity"];
    public override string[] ExampleQueries => ["把所有反派的定位改成龙套", "给三个角色补上弱点"];
    public override string Label => "批量编辑实体";

    public override string Description =>
        "按字段批量修改设定面板条目（小说/分卷/章节细纲/角色/组织/地点/情节/场景/道具/关系），"
        + "一次调用可跨类型混排。写入经 INovelStore：面板自动刷新、向量索引同步；"
        + "角色所属组织与组织成员双向对账。"
        + "章节正文用 write_chapter / edit_text，章节序号用 reorder_chapters。\n可用字段：\n"
        + FieldTable;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<EditEntitiesDetails>> ExecuteAsync(
        string toolCallId,
        EditEntitiesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<EditEntitiesDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (args.Items is null || args.Items.Count == 0)
            return Error("请提供至少一个条目");

        var lines = new List<string>(args.Items.Count);
        var updatedIds = new List<string>(args.Items.Count);
        var failedCount = 0;

        foreach (var item in args.Items)
        {
            ThrowIfCancelled(ct);
            if (item is null)
            {
                failedCount++;
                lines.Add("❌ (空条目)");
                continue;
            }

            try
            {
                var type = ParseSupportedType(item.EntityType);
                var entity = await LoadEntityAsync(type, item, args.NovelId, ct);
                var before = EntityFieldWriter.CaptureMembership(entity);
                var changes = await EntityFieldWriter.ApplyAsync(
                    store,
                    args.NovelId,
                    entity,
                    type,
                    item.Fields,
                    ct
                );

                ThrowIfCancelled(ct);
                await EntityFieldWriter.SaveAsync(store, entity, before, ct);
                ThrowIfCancelled(ct);
                await EntityFieldWriter.IndexAsync(kernel, entity, ct);

                lines.Add(
                    $"✅ {EntityFieldRegistry.Label(type)}「{EntityFieldWriter.DisplayName(entity)}」"
                        + $"(ID: {entity.Id}) 已更新: {string.Join("；", changes)}"
                );
                updatedIds.Add(entity.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                lines.Add($"❌ {item.EntityType} {item.EntityId}: {ex.Message}");
            }
        }

        return new AgentToolResult<EditEntitiesDetails>
        {
            Content = [new TextContent(string.Join("\n", lines))],
            IsError = updatedIds.Count == 0 && failedCount > 0,
            Details = new EditEntitiesDetails
            {
                UpdatedCount = updatedIds.Count,
                FailedCount = failedCount,
                UpdatedIds = [.. updatedIds],
            },
        };
    }

    /// <summary>解析实体类型：先 kebab 词汇表，再枚举名（忽略大小写），最后校验白名单。</summary>
    private static EntityType ParseSupportedType(string value)
    {
        var parsed = EntityTypeExtensions.ParseEntityType(value);
        if (parsed is null && NovelEnum.TryParse<EntityType>(value, out var enumValue))
            parsed = enumValue;

        if (parsed is not { } type || Array.IndexOf(SupportedTypes, type) < 0)
            throw new InvalidOperationException(
                $"不支持的实体类型 '{value}'。支持: "
                    + string.Join("/", SupportedTypes.Select(t => t.ToKebabCaseString()))
            );
        return type;
    }

    /// <summary>按类型载入实体；支持 ID、名称、章节/卷序号，未找到时返回解析器的具体错误。</summary>
    private async Task<Entity> LoadEntityAsync(
        EntityType type,
        EntityEditItem item,
        string novelId,
        CancellationToken ct
    )
    {
        var id = item.EntityId?.Trim() ?? "";
        if (type is EntityTypeEnum.Novel)
            return await LoadNovelAsync(id, novelId, ct)
                ?? throw new InvalidOperationException($"未找到小说 '{id}'");

        var lookup = await NovelReferenceResolver.EntityAsync(store, novelId, type, id, ct);
        return lookup.Value
            ?? throw new InvalidOperationException(
                lookup.Error ?? $"未找到{EntityFieldRegistry.Label(type)} '{id}'"
            );
    }

    /// <summary>小说条目：entity-id 留空时用调用级 novel-id（只读摘要，改的仅 novel.yaml 字段）。</summary>
    private async Task<Novel?> LoadNovelAsync(string id, string novelId, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(id) ? novelId : id;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                "小说条目需要 entity-id（或用 novel-id 指定当前项目）"
            );
        var novels = await store.GetAllNovels(ct);
        return ManageHelper.ResolveByIdOrName(novels, key, n => n.Id, n => n.Title);
    }

    /// <summary>工具描述里的字段清单（与编辑窗字段注册表同源，跳过章节 Order/Content）。</summary>
    private static string BuildFieldTable()
    {
        var sb = new StringBuilder();
        foreach (var type in SupportedTypes)
        {
            var names = EntityFieldRegistry
                .WritableFields(type)
                .Where(f =>
                    type is not EntityTypeEnum.Chapter || f.Prop is not ("Order" or "Content")
                )
                .Select(f => $"{JsonNamingPolicy.KebabCaseLower.ConvertName(f.Prop)}({f.Label})");
            sb.Append("- ")
                .Append(type.ToKebabCaseString())
                .Append('（')
                .Append(EntityFieldRegistry.Label(type))
                .Append("）: ")
                .AppendLine(string.Join(", ", names));
        }
        return sb.ToString().TrimEnd();
    }

    private static AgentToolResult<EditEntitiesDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

/// <summary>成员关系对账快照（保存前采集：角色 FactionId / 组织 Members）。</summary>
internal sealed record MembershipBefore(
    string? FactionId = null,
    IReadOnlyList<string>? Members = null
);

/// <summary>
/// 实体字段写入器：字段白名单校验 → 值强转 → 成员双写对账 → 落盘 → 向量索引。
/// 全部字段校验并转换完成后才改内存对象（任一条目失败时不会留下半改状态）。
/// </summary>
internal static class EntityFieldWriter
{
    private const BindingFlags PropFlags = BindingFlags.Public | BindingFlags.Instance;
    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly JsonNamingPolicy Kebab = JsonNamingPolicy.KebabCaseLower;

    internal static MembershipBefore CaptureMembership(Entity entity) =>
        entity switch
        {
            Character c => new MembershipBefore(
                FactionId: string.IsNullOrEmpty(c.FactionId) ? null : c.FactionId
            ),
            Faction f => new MembershipBefore(Members: [.. f.Members]),
            _ => new MembershipBefore(),
        };

    internal static string DisplayName(Entity entity) =>
        entity switch
        {
            Novel n => n.Title,
            Volume v => v.Name,
            Chapter c => c.Title,
            Character c => c.Name,
            Faction f => f.Name,
            Location l => l.Name,
            Plotline p => p.Name,
            SceneDefinition s => s.Title,
            Item i => i.Name,
            _ => entity.Id,
        };

    /// <summary>校验并应用字段补丁；返回可读变更列表。任何字段非法时整条抛错、不修改实体。</summary>
    internal static async Task<IReadOnlyList<string>> ApplyAsync(
        INovelStore store,
        string novelId,
        Entity entity,
        EntityType type,
        IReadOnlyList<EntityFieldPatch>? patches,
        CancellationToken ct
    )
    {
        if (patches is null || patches.Count == 0)
            throw new InvalidOperationException("条目未提供字段");

        var assignments = new List<(PropertyInfo Prop, object? Value, string Change)>(
            patches.Count
        );
        var referenceCache =
            new Dictionary<(EntityType Type, string Key), NovelReferenceResolver.Lookup<string>>();
        foreach (var patch in patches)
        {
            ct.ThrowIfCancellationRequested();
            if (patch is null)
                throw new InvalidOperationException("字段补丁不能为空");

            var name = patch.Field?.Trim() ?? "";
            var def = EntityFieldRegistry.FindWritableField(type, name)
                ?? throw new InvalidOperationException(
                    $"字段 '{name}' 不可写。{EntityFieldRegistry.Label(type)} 可用字段: {FieldNames(type)}"
                );

            if (type is EntityTypeEnum.Chapter)
            {
                if (def.Prop is "Order")
                    throw new InvalidOperationException(
                        "章节序号请用 reorder_chapters（全局序唯一，直接改会撞章节文件名）"
                    );
                if (def.Prop is "Content")
                    throw new InvalidOperationException("章节正文请用 write_chapter / edit_text");
            }

            var prop = entity.GetType().GetProperty(def.Prop, PropFlags)
                ?? throw new InvalidOperationException($"模型缺少字段 {def.Prop}");
            if (assignments.Any(a => a.Prop == prop))
                throw new InvalidOperationException($"字段 '{name}' 重复提交，未做任何修改");

            var value = Coerce(def, prop, patch);
            if (
                value is string scalar
                && IsRequiredText(type, def.Prop)
                && scalar.Trim().Length == 0
            )
                throw new InvalidOperationException($"字段 '{name}' 不能为空");

            if (
                value is string reference
                && EntityFieldRegistry.SelectSource(def.Prop) is { } refType
            )
            {
                if (reference.Trim().Length == 0)
                {
                    if (IsRequiredReference(type, def.Prop))
                        throw new InvalidOperationException($"字段 '{name}' 不能为空");
                }
                else
                {
                    var resolved = await ResolveReferenceIdAsync(
                        store,
                        novelId,
                        refType,
                        reference,
                        referenceCache,
                        ct
                    );
                    if (resolved.Value is not { } id)
                        throw new InvalidOperationException(
                            $"字段 '{name}' 的{EntityFieldRegistry.Label(refType)}引用无效：{resolved.Error}"
                        );
                    if (refType == type && string.Equals(id, entity.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException($"字段 '{name}' 不能引用实体自身");
                    value = id;
                }
            }

            if (
                value is List<string> references
                && EntityFieldRegistry.MultiSelectSource(def.Prop) is { } multiType
            )
            {
                var ids = new List<string>(references.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var listReference in references)
                {
                    if (listReference.Trim().Length == 0)
                        continue;
                    var resolved = await ResolveReferenceIdAsync(
                        store,
                        novelId,
                        multiType,
                        listReference,
                        referenceCache,
                        ct
                    );
                    if (resolved.Value is not { } id)
                        throw new InvalidOperationException(
                            $"字段 '{name}' 的{EntityFieldRegistry.Label(multiType)}引用无效：{resolved.Error}"
                        );
                    if (multiType == type && string.Equals(id, entity.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException($"字段 '{name}' 不能引用实体自身");
                    if (!seen.Add(id))
                        throw new InvalidOperationException($"字段 '{name}' 包含重复引用: {id}");
                    ids.Add(id);
                }
                value = ids;
            }

            if (entity is Novel && def.Prop is "TargetWords" && value is int targetWords)
            {
                if (targetWords < 0)
                    throw new InvalidOperationException("目标字数不能为负数");
            }

            if (entity is Volume && def.Prop is "Order" && value is int volumeOrder)
            {
                if (volumeOrder <= 0)
                    throw new InvalidOperationException("卷序号必须从 1 开始");
                var volumes = await store.GetVolumes(novelId, ct);
                if (volumes.Any(v => v.Id != entity.Id && v.Order == volumeOrder))
                    throw new InvalidOperationException(
                        $"第 {volumeOrder} 卷已存在，卷序号不能重复"
                    );
            }

            var change =
                $"{Kebab.ConvertName(def.Prop)}: "
                + $"{ManageHelper.Diff(FormatValue(prop.GetValue(entity)))} → "
                + $"{ManageHelper.Diff(FormatValue(value))}";
            assignments.Add((prop, value, change));
        }

        var finalValues = assignments.ToDictionary(a => a.Prop.Name, a => a.Value);
        if (entity is Relationship relationship)
        {
            var a =
                finalValues.GetValueOrDefault(nameof(Relationship.CharacterAId)) as string
                ?? relationship.CharacterAId;
            var b =
                finalValues.GetValueOrDefault(nameof(Relationship.CharacterBId)) as string
                ?? relationship.CharacterBId;
            if (a.Length == 0 || b.Length == 0)
                throw new InvalidOperationException("关系两端角色不能为空");
            if (string.Equals(a, b, StringComparison.Ordinal))
                throw new InvalidOperationException("关系的两个角色不能相同");
        }

        if (entity is Location location)
        {
            var parentId =
                finalValues.GetValueOrDefault(nameof(Location.ParentId)) as string
                ?? location.ParentId;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                var locations = await store.GetLocations(novelId, ct);
                var byId = locations.ToDictionary(l => l.Id, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var current = parentId;
                while (!string.IsNullOrWhiteSpace(current))
                {
                    if (string.Equals(current, location.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("父地点不能指向自己或自己的下级");
                    if (!seen.Add(current))
                        throw new InvalidOperationException("父地点链存在循环");
                    current = byId.TryGetValue(current, out var parent) ? parent.ParentId : null;
                }
            }
        }

        foreach (var (prop, value, _) in assignments)
            prop.SetValue(entity, value);
        return [.. assignments.Select(a => a.Change)];
    }

    private static async Task<NovelReferenceResolver.Lookup<string>> ResolveReferenceIdAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string reference,
        Dictionary<(EntityType Type, string Key), NovelReferenceResolver.Lookup<string>> cache,
        CancellationToken ct
    )
    {
        var key = (type, NovelReferenceResolver.NormalizeText(reference));
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var resolved = await NovelReferenceResolver.EntityIdAsync(
            store,
            novelId,
            type,
            reference,
            ct
        );
        cache[key] = resolved;
        return resolved;
    }

    private static bool IsRequiredText(EntityType type, string prop) =>
        prop switch
        {
            "Name" => type
                is EntityTypeEnum.Volume
                    or EntityTypeEnum.Character
                    or EntityTypeEnum.Faction
                    or EntityTypeEnum.Location
                    or EntityTypeEnum.Plotline
                    or EntityTypeEnum.Item,
            "Title" => type
                is EntityTypeEnum.Chapter
                    or EntityTypeEnum.Scene
                    or EntityTypeEnum.Novel,
            _ => false,
        };

    private static bool IsRequiredReference(EntityType type, string prop) =>
        type is EntityTypeEnum.Relationship && prop is "CharacterAId" or "CharacterBId";

    /// <summary>按模型属性类型强制转换（不是 FieldType——列表/枚举的真实形状以模型为准）。</summary>
    private static object? Coerce(FieldDef def, PropertyInfo prop, EntityFieldPatch patch)
    {
        var target = prop.PropertyType;
        var name = Kebab.ConvertName(def.Prop);

        if (target == typeof(List<string>))
        {
            if (patch.Values is null)
                throw new InvalidOperationException(
                    $"字段 '{name}' 是列表字段，请用 values 传数组"
                );
            return patch
                .Values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
        }

        var raw = CoerceOption(def, patch.Value).Trim();

        if (target == typeof(string))
        {
            if (raw.Length > 0)
                return raw;
            // 空值：可空引用类型落 null，非空类型落空串（与编辑窗一致）
            return Nullability.Create(prop).WriteState is NullabilityState.Nullable ? null : "";
        }

        if (target == typeof(bool))
            return TryParseBool(raw, out var flag)
                ? flag
                : throw new InvalidOperationException(
                    $"字段 '{name}' 取值 '{raw}' 不是布尔值（可用: true/false、是/否、1/0、on/off、开/关）"
                );

        if (target == typeof(int))
        {
            if (
                def.Prop is "Order"
                && NovelReferenceResolver.TryParseOrder(raw, out var orderValue)
            )
                return orderValue;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : throw new InvalidOperationException($"字段 '{name}' 取值 '{raw}' 不是整数");
        }

        if (target == typeof(double))
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d
                : throw new InvalidOperationException($"字段 '{name}' 取值 '{raw}' 不是数字");

        if (target.IsEnum)
        {
            if (target == typeof(RelationshipType))
                return RelationshipTypeExtensions.ParseRelationshipType(raw)
                    ?? throw new InvalidOperationException(EnumError(def, target, name, raw));

            return NovelEnum.TryParse(target, raw, out var parsed)
                ? parsed
                : throw new InvalidOperationException(EnumError(def, target, name, raw));
        }

        throw new InvalidOperationException($"字段 '{name}' 类型不支持（{target.Name}）");
    }

    /// <summary>注册表枚举选项：中文标签 → 存储值；已是存储值时规范大小写。</summary>
    private static string CoerceOption(FieldDef def, string? value)
    {
        var raw = value?.Trim() ?? "";
        if (raw.Length == 0 || def.Options is null || def.OptionValues is null)
            return raw;

        var index = Array.IndexOf(def.Options, raw);
        if (index >= 0)
            return def.OptionValues[index];

        var stored = def.OptionValues.FirstOrDefault(v =>
            string.Equals(v, raw, StringComparison.OrdinalIgnoreCase)
        );
        if (stored is not null)
            return stored;

        if (def.Type == FieldType.Select)
            throw new InvalidOperationException(
                $"字段 '{Kebab.ConvertName(def.Prop)}' 取值 '{raw}' 无效。可选: "
                    + string.Join(
                        "、",
                        def.Options.Zip(def.OptionValues, (label, val) => $"{label}={val}")
                    )
            );
        return raw;
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "是" or "on" or "开":
                value = true;
                return true;
            case "false" or "0" or "否" or "off" or "关":
                value = false;
                return true;
            default:
                return bool.TryParse(raw, out value);
        }
    }

    private static string EnumError(FieldDef def, Type enumType, string name, string raw)
    {
        var choices =
            def.Options is not null && def.OptionValues is not null
                ? string.Join(
                    "、",
                    def.Options.Zip(def.OptionValues, (label, value) => $"{label}={value}")
                )
                : string.Join("、", Enum.GetNames(enumType));
        return $"字段 '{name}' 取值 '{raw}' 无效。可选: {choices}";
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "",
            string s => s,
            IEnumerable<string> list => string.Join("、", list),
            _ => value.ToString() ?? "",
        };

    private static string FieldNames(EntityType type) =>
        string.Join(
            ", ",
            EntityFieldRegistry.WritableFields(type).Select(f => Kebab.ConvertName(f.Prop))
        );

    // ── 落盘（成员双写对账）──────────────────────────────────────────

    /// <summary>成员关系对账后落盘：角色 FactionId ↔ 组织 Members 双向补齐。</summary>
    internal static async Task SaveAsync(
        INovelStore store,
        Entity entity,
        MembershipBefore before,
        CancellationToken ct
    )
    {
        switch (entity)
        {
            case Character character:
                await ReconcileCharacterAsync(store, character, before, ct);
                await store.SaveCharacter(character, ct);
                break;
            case Faction faction:
                await ReconcileFactionAsync(store, faction, before, ct);
                await store.SaveFaction(faction, ct);
                break;
            case Plotline plotline:
                await store.SavePlotline(plotline, ct);
                break;
            case Location location:
                await store.SaveLocation(location, ct);
                break;
            case SceneDefinition scene:
                await store.SaveScene(scene, ct);
                break;
            case Item item:
                await store.SaveItem(item, ct);
                break;
            case Relationship relationship:
                await store.SaveRelationship(relationship, ct);
                break;
            case Chapter chapter:
                await store.SaveChapter(chapter, ct);
                break;
            case Volume volume:
                await store.SaveVolume(volume, ct);
                break;
            case Novel novel:
                await store.SaveNovel(novel, ct);
                break;
            default:
                throw new InvalidOperationException($"不支持的实体类型：{entity.GetType().Name}");
        }
    }

    /// <summary>角色侧对账：FactionId 位移 → 老组织出列、新组织入列。</summary>
    private static async Task ReconcileCharacterAsync(
        INovelStore store,
        Character character,
        MembershipBefore before,
        CancellationToken ct
    )
    {
        var oldId = before.FactionId;
        var newId = string.IsNullOrEmpty(character.FactionId) ? null : character.FactionId;
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
            return;

        if (oldId is not null)
        {
            var oldFaction = await store.GetFaction(oldId, ct);
            if (oldFaction is not null && oldFaction.Members.Remove(character.Id))
                await store.SaveFaction(oldFaction, ct);
        }

        if (newId is not null)
        {
            var newFaction = await store.GetFaction(newId, ct);
            if (newFaction is not null && !newFaction.Members.Contains(character.Id))
            {
                newFaction.Members.Add(character.Id);
                await store.SaveFaction(newFaction, ct);
            }
        }
    }

    /// <summary>组织侧对账：Members diff → 新增成员补 FactionId、移除成员清 FactionId（悬空跳过）。</summary>
    private static async Task ReconcileFactionAsync(
        INovelStore store,
        Faction faction,
        MembershipBefore before,
        CancellationToken ct
    )
    {
        var oldSet = new HashSet<string>(before.Members ?? [], StringComparer.Ordinal);
        var newSet = new HashSet<string>(faction.Members, StringComparer.Ordinal);

        foreach (var memberId in newSet.Where(id => !oldSet.Contains(id)))
        {
            var member = await store.GetCharacter(memberId, ct);
            if (
                member is null
                || string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal)
            )
                continue;

            var oldFactionId = member.FactionId;
            if (!string.IsNullOrEmpty(oldFactionId))
            {
                var oldFaction = await store.GetFaction(oldFactionId, ct);
                if (oldFaction is not null && oldFaction.Members.Remove(memberId))
                    await store.SaveFaction(oldFaction, ct);
            }

            member.FactionId = faction.Id;
            await store.SaveCharacter(member, ct);
        }

        foreach (var memberId in oldSet.Where(id => !newSet.Contains(id)))
        {
            var member = await store.GetCharacter(memberId, ct);
            if (
                member is null
                || !string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal)
            )
                continue;

            member.FactionId = null;
            await store.SaveCharacter(member, ct);
        }
    }

    // ── 向量索引 ────────────────────────────────────────────────────

    /// <summary>同步可索引实体的向量文档；章节只重建细纲类摘要（正文未变）。</summary>
    internal static async Task IndexAsync(
        RetrievalKernel? kernel,
        Entity entity,
        CancellationToken ct
    )
    {
        if (kernel is null)
            return;

        (RetrievalDocument Doc, List<RetrievalChunk> Chunks)? built = entity switch
        {
            Character character => kernel.Builder.BuildCharacter(character),
            Faction faction => kernel.Builder.BuildFaction(faction),
            Location location => kernel.Builder.BuildLocation(location),
            Plotline plotline => kernel.Builder.BuildPlotline(plotline),
            Item item => kernel.Builder.BuildItem(item),
            Chapter chapter => kernel.Builder.BuildChapterSummary(chapter),
            _ => null,
        };

        if (built is not { } value)
            return;
        await kernel.IndexDocumentAsync(value.Doc, value.Chunks, ct);
    }
}

public sealed record EditEntitiesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要修改的条目，可跨类型混排；按顺序逐条应用")]
    public List<EntityEditItem>? Items { get; init; }
}

public sealed record EntityEditItem
{
    [Description(
        "实体类型：novel/volume/chapter/character/faction/location/plotline/scene/item/relationship"
    )]
    public string EntityType { get; init; } = "";

    [Description(
        "实体ID；volume/chapter/character/faction/location/plotline/scene/item 也接受名称或序号"
    )]
    public string EntityId { get; init; } = "";

    [Description("字段修改列表；字段名用 kebab-case（也可用模型属性名）")]
    public List<EntityFieldPatch> Fields { get; init; } = [];
}

public sealed record EntityFieldPatch
{
    [Description("字段名，如 personality / status / plotline-ids / volume-id")]
    public string Field { get; init; } = "";

    [Description("标量值（字符串/枚举/数字/布尔）。枚举可传中文标签或英文值")]
    public string? Value { get; init; }

    [Description(
        "列表字段的新值（tags / members / participant-ids / plotline-ids / character-ids / item-ids）；空数组 = 清空"
    )]
    public List<string>? Values { get; init; }
}

public sealed record EditEntitiesDetails
{
    public int UpdatedCount { get; init; }
    public int FailedCount { get; init; }
    public string[] UpdatedIds { get; init; } = [];
}
