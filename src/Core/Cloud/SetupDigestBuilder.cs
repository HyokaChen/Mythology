using Core.Data;

namespace Core.Cloud;

/// <summary>
/// 设定图构建器（CHANGE_10 · fiction.setup.check）— 把本地 Novel 设定实体映射为
/// <see cref="SetupCheckInput"/>（云端 CloudSetupInput 镜像）。
///
/// 关键约定：客户端章节 ID 是 12 位 hex GUID，云端 ChapterRefResolver 只认
/// ch_N / N / 第N章 形式——因此所有章节引用一律规范化为 ch_{Order}（B2）。
/// 无法映射（章节无 Order / 引用不存在）→ 置 null，云端对无法解析的引用静默降级，无误报。
/// </summary>
public static class SetupDigestBuilder
{
    /// <summary>构建设定图。入参为已加载的实体集合（调用方从 INovelStore 拉取）。</summary>
    public static SetupCheckInput Build(
        Novel novel,
        IReadOnlyList<Character> characters,
        IReadOnlyList<Faction> factions,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<Plotline> plotlines,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<TimelineEvent> timelineEvents,
        IReadOnlyList<Location>? locations = null,
        IReadOnlyList<Item>? items = null,
        IReadOnlyList<WorldRule>? worldRules = null,
        // ENTITY-EVOLUTION §6.1 通道①（E4 镜像：有值才上传——云端未实现前调用方不传）
        IReadOnlyList<AppearanceRecord>? appearances = null,
        IReadOnlyList<EntityStateChange>? stateChanges = null
    )
    {
        // 章节 GUID → ch_{Order} 映射（无 Order 的章节不参与引用规范化）
        var chapterRefMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in chapters.OrderBy(c => c.Order))
        {
            if (c.Order > 0)
                chapterRefMap[c.Id] = $"ch_{c.Order}";
        }

        string? NormalizeRef(string? raw) =>
            raw is not null && chapterRefMap.TryGetValue(raw, out var canonical) ? canonical : null;

        var characterInputs = characters
            .Select(c => new SetupCharacterInput(
                c.Id,
                c.Name,
                Generation: null, // 客户端无世代位势字段
                Status: c.Status.ToString().ToLowerInvariant(),
                Organizations: string.IsNullOrWhiteSpace(c.FactionId) ? null : [c.FactionId], // 组织实体 Id（悬空可判；名称在组织实体上）
                Role: c.Role.ToString().ToLowerInvariant(),
                Personality: NullIfEmpty(c.Personality),
                Motivation: NullIfEmpty(c.Motivation),
                Background: NullIfEmpty(c.Background),
                Habits: NullIfEmpty(c.Habits)
            ))
            .ToList();

        var relationInputs = relationships
            .Select(r =>
            {
                // 本地关系类型 → 云端规范 token：口径单一事实源在 RelationshipOntology
                // （主仆→master、同门→sect-mate 等设定检查专用 token，有向性同源）
                var type = RelationshipOntology.CloudSetupToken(r.Type);
                // 方向语义：本地 Relationship 是 A/B 对（无向）；由本体声明哪些关系有向
                // （A=上级/父母/师的约定），其余一律无向——云端对称检查不判"反向缺失"，避免批量误报。
                var directional = RelationshipOntology.Directed(r.Type);
                return new SetupRelationInput(
                    r.CharacterAId,
                    r.CharacterBId,
                    Type: type,
                    Directional: directional,
                    SinceChapterId: NormalizeRef(r.SinceChapterId),
                    UntilChapterId: NormalizeRef(r.UntilChapterId),
                    Strength: r.Strength.ToString().ToLowerInvariant()
                );
            })
            .ToList();

        var sceneInputs = scenes
            .Select(s => new SetupSceneInput(
                s.Id,
                s.Title,
                ChapterId: NormalizeRef(s.ChapterId),
                LocationId: s.LocationId,
                ParticipantIds: s.ParticipantIds,
                PlotlineIds: s.PlotlineIds,
                TimeDescription: s.TimeDescription,
                ItemIds: s.ItemIds is { Count: > 0 } ? s.ItemIds : null
            ))
            .ToList();

        // §2.7 矩阵源③：Turnpoints 确定性解析（推断数据，云端只产 info/warning 不产 error）
        var validOrders = chapters.Where(c => c.Order > 0).Select(c => c.Order).ToHashSet();
        var plotlineInputs = plotlines
            .Select(p =>
            {
                var beats = TurnpointsBeatParser.Parse(p.Turnpoints, validOrders);
                return new SetupPlotlineInput(
                    p.Id,
                    p.Name,
                    Type: p.Type switch
                    {
                        PlotlineType.Side => "sub", // 云端分档语义：Side ≡ sub
                        _ => p.Type.ToString().ToLowerInvariant(),
                    },
                    PlannedRevealChapterId: NormalizeRef(p.PlannedRevealChapterId),
                    ResolvedAtChapterId: NormalizeRef(p.ResolvedAtChapterId),
                    Beats: beats is { Count: > 0 }
                        ? beats
                            .Select(b => new BeatEntryInput(
                                $"ch_{b.ChapterOrder}",
                                b.Type,
                                b.Tension,
                                b.Description
                            ))
                            .ToList()
                        : null
                );
            })
            .ToList();

        var chapterAnchors = chapters
            .Where(c => c.Order > 0)
            .OrderBy(c => c.Order)
            .Select(c => new SetupChapterAnchorInput(
                $"ch_{c.Order}",
                c.Order,
                WordCount: string.IsNullOrEmpty(c.Content) ? null : c.Content.Length
            ))
            .ToList();

        // v2 一等实体：地点（架空检查）、组织+阵营（孤儿/无脸/纸面阵营）、道具（所有权链）
        var locationInputs = locations
            ?.Where(l => !string.IsNullOrWhiteSpace(l.Id))
            .Select(l => new SetupLocationInput(
                l.Id,
                l.Name,
                Region: l.ParentId, // 客户端 Location 用 ParentId 表层级；云端 Region 仅展示
                Description: NullIfEmpty(l.Description),
                AdjacentLocations: l.AdjacentLocations is { Count: > 0 }
                    ? l
                        .AdjacentLocations.Where(a => a.DistanceLi > 0)
                        .Select(a => new LocationLinkInput(a.TargetId, a.DistanceLi))
                        .ToList()
                    : null
            ))
            .ToList();

        // 组织成员 = Faction.Members ∪ 角色侧 FactionId 反查（双向并集，孤儿判定才准确）
        var membersByFaction = characters
            .Where(c => !string.IsNullOrWhiteSpace(c.FactionId))
            .GroupBy(c => c.FactionId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.Ordinal);
        var organizationInputs = factions
            .Where(f => !string.IsNullOrWhiteSpace(f.Id))
            .Select(f =>
            {
                var memberIds = new List<string>(f.Members);
                if (membersByFaction.TryGetValue(f.Id, out var fromCharacters))
                {
                    foreach (var id in fromCharacters)
                        if (!memberIds.Contains(id))
                            memberIds.Add(id);
                }
                return new SetupOrganizationInput(
                    f.Id,
                    f.Name,
                    MemberIds: memberIds,
                    Description: NullIfEmpty(f.Description)
                );
            })
            .ToList();

        // 道具：所有权时段链（CHANGE_10 v4）。
        // 优先用 OwnershipPeriods（多变段≈传承/夺宝）；回退用 OwnerId 单值→单段链。
        var itemInputs = items
            ?.Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i =>
            {
                List<OwnershipInput>? ownerships = null;
                if (i.OwnershipPeriods is { Count: > 0 })
                {
                    ownerships = i
                        .OwnershipPeriods.Where(o => !string.IsNullOrWhiteSpace(o.OwnerId))
                        .Select(o => new OwnershipInput(
                            o.OwnerId,
                            SinceChapterId: NormalizeRef(o.SinceChapterId),
                            UntilChapterId: NormalizeRef(o.UntilChapterId)
                        ))
                        .ToList();
                }
                else if (!string.IsNullOrWhiteSpace(i.OwnerId))
                {
                    ownerships = [new OwnershipInput(i.OwnerId)];
                }
                return new SetupItemInput(i.Id, i.Name, Ownership: ownerships);
            })
            .ToList();

        // 作者声明约束（L2 硬前置）：有声明才启用云端年龄/寿元/旅行 STP
        var worldRuleInputs = (worldRules ?? [])
            .Where(w =>
                w.EntryKind == WorldRuleEntryKind.Rule && !string.IsNullOrWhiteSpace(w.Type)
            )
            .Select(w => new WorldRuleInput(w.Id, w.Type, w.Value, w.Unit, w.Description))
            .ToList();

        // ENTITY-EVOLUTION §6.1 通道①（E4 镜像）：实体×章出现摘要 + 变化事件。
        // 稠密出现按实体聚合（全章序+mode 数组）；稀疏事件逐条直映射。
        // 章节引用一律 ch_{Order} 规范化（B2 先例）；空表不上报（NullIfEmpty 语义）。
        var appearanceInputs = BuildAppearanceInputs(appearances, chapterRefMap);
        var evolutionInputs = stateChanges is { Count: > 0 }
            ? stateChanges
                .Where(c =>
                    c.Lifecycle == ChangeLifecycle.Committed
                    && NormalizeRef(c.ChapterId) is not null
                )
                .Select(c => new CloudEvolutionEventInput(
                    c.TargetId,
                    c.TargetEntityType.ToString().ToLowerInvariant(),
                    NormalizeRef(c.ChapterId)!,
                    c.Aspect.ToString().ToLowerInvariant(),
                    NullIfEmpty(c.FromSummary ?? ""),
                    NullIfEmpty(c.ToSummary ?? ""),
                    c.Summary,
                    NullIfEmpty(c.EvidenceQuote ?? "")
                ))
                .ToList()
            : null;

        return new SetupCheckInput(
            Characters: characterInputs,
            Relations: relationInputs,
            WorldRules: worldRuleInputs, // 无声明 → 空表（云端整类静默）
            Scenes: sceneInputs,
            Plotlines: plotlineInputs,
            ChapterAnchors: chapterAnchors,
            Status: string.Equals(
                novel.CompletionStatus,
                "completed",
                StringComparison.OrdinalIgnoreCase
            )
                ? "completed"
                : "ongoing",
            NovelTitle: novel.Title,
            AssertedFacts: null, // L1 依赖 LLM 抽取层（后续项）
            Locations: locationInputs is { Count: > 0 } ? locationInputs : null,
            Organizations: organizationInputs.Count > 0 ? organizationInputs : null,
            Items: itemInputs is { Count: > 0 } ? itemInputs : null,
            EntityAppearances: appearanceInputs,
            EntityEvolutionEvents: evolutionInputs
        );
    }

    /// <summary>
    /// 出场记录 → 实体聚合摘要（ENTITY-EVOLUTION §6.1）。
    /// 同实体多条记录并成一条：章序数组（升序去重）+ mode 数组（按章序对齐）。
    /// 无法解析到 ch_{Order} 的记录（章已删/无 Order）跳过。
    /// </summary>
    private static List<CloudEntityAppearanceInput>? BuildAppearanceInputs(
        IReadOnlyList<AppearanceRecord>? appearances,
        IReadOnlyDictionary<string, string> chapterRefMap
    )
    {
        if (appearances is not { Count: > 0 })
            return null;

        var normalized = appearances
            .Where(r =>
                !string.IsNullOrWhiteSpace(r.EntityId)
                && chapterRefMap.TryGetValue(r.ChapterId, out var ref_) // 章必须可解析
                && ref_ is not null
            )
            .GroupBy(r => (r.EntityType, r.EntityId));

        var result = new List<CloudEntityAppearanceInput>();
        foreach (var group in normalized)
        {
            var perChapter = group
                .GroupBy(r => r.ChapterId)
                .Select(g =>
                    (
                        Order: int.Parse(chapterRefMap[g.Key].AsSpan(3)), // "ch_N" → N
                        Ref: chapterRefMap[g.Key],
                        // 同章多 mode：任一在场即 present，否则 mentioned
                        Mode: g.Any(r => r.Mode == AppearanceMode.Present) ? "present" : "mentioned"
                    )
                )
                .OrderBy(t => t.Order)
                .ToList();

            result.Add(
                new CloudEntityAppearanceInput(
                    group.Key.EntityId,
                    group.Key.EntityType.ToKebabCaseString(),
                    ChapterOrders: perChapter.Select(t => t.Ref).ToList(),
                    Modes: perChapter.Select(t => t.Mode).ToList()
                )
            );
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>空串 → null（客户端空字段不上报，云端按缺数据处理）。</summary>
    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
