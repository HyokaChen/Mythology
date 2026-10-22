using System.Text;
using Core.Data;

namespace Core.Evolution;

/// <summary>harness 信号类别。</summary>
public enum HarnessSignalKind
{
    /// <summary>内容偏差（规则违反/死亡角色出场/悬空引用）。</summary>
    Drift,

    /// <summary>想象力卡顿（无计划变化/情节线停转/无状态记录）。</summary>
    Stall,

    /// <summary>文笔风险（只路由给云端诊断，不评分）。</summary>
    Quality,

    /// <summary>知识缺口。</summary>
    KnowledgeGap,
}

/// <summary>harness 严重度。</summary>
public enum HarnessSeverity
{
    /// <summary>提示。</summary>
    Info,

    /// <summary>警告：建议修正，不阻断。</summary>
    Warning,

    /// <summary>硬偏差：阻断本轮云端诊断。</summary>
    Critical,
}

/// <summary>一条本体 harness 信号。</summary>
public sealed record HarnessSignal(
    string Id,
    HarnessSignalKind Kind,
    HarnessSeverity Severity,
    string Title,
    string Detail,
    string Prescription,
    string? SuggestedTool = null,
    IReadOnlyDictionary<string, string>? SuggestedArgs = null,
    string? RuleId = null,
    EntityType? TargetEntityType = null,
    string? TargetId = null,
    string? Evidence = null
);

/// <summary>harness 策略参数。</summary>
public sealed record OntologyHarnessRules(
    int MaxSignals = 4,
    int StallWindow = 5,
    bool GateCloud = true
);

/// <summary>harness 评估结果。</summary>
public sealed record OntologyHarnessResult(
    IReadOnlyList<HarnessSignal> Signals,
    IReadOnlyList<MaterialDelta> MaterialDeltas,
    bool BlocksCloud,
    string Render
);

/// <summary>
/// 本体驱动的 harness 策略（纯函数、零 LLM/零云端积分）：
/// 把 delta/规则/出场/计划/情节线的确定性结论翻译成「风险/建议/边界」注入文本。
/// </summary>
public static class OntologyHarnessPolicy
{
    /// <summary>注入识别前缀。</summary>
    public const string Prefix = "[本体 harness]";

    public static OntologyHarnessResult Evaluate(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<WorldRule> rules,
        EntityTimelineView view,
        IReadOnlyList<OntologyAction> recentActions,
        OntologyHarnessRules options
    )
    {
        var boundaries = MaterialBoundaryDetector.DetectForChapter(
            view,
            novel,
            chapter,
            scenes,
            rules,
            recentActions: recentActions
        );
        var material = boundaries
            .SelectMany(b => b.Deltas)
            .Where(d => d.Need != MaterialNeed.None)
            .DistinctBy(d => d.Id, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        var signals = new List<HarnessSignal>();

        // 规则偏差
        foreach (
            var delta in material.Where(d =>
                d.Kind == PlannedDeltaKind.RuleTrigger && d.RuleId is not null
            )
        )
        {
            var rule = rules.FirstOrDefault(r =>
                string.Equals(r.Id, delta.RuleId, StringComparison.Ordinal)
            );
            if (rule is null)
            {
                continue;
            }
            signals.Add(
                new HarnessSignal(
                    Id: $"drift.rule:{rule.Id}:{delta.EntityType.ToKebabCaseString()}:{delta.EntityId}",
                    Kind: HarnessSignalKind.Drift,
                    Severity: rule.Severity switch
                    {
                        RuleSeverity.Constraint => HarnessSeverity.Critical,
                        RuleSeverity.Warning => HarnessSeverity.Warning,
                        _ => HarnessSeverity.Info,
                    },
                    Title: $"规则「{TitleOf(rule)}」",
                    Detail: delta.Summary,
                    Prescription: "先按规则后果修正，或显式说明该规则在本章不适用",
                    SuggestedTool: "check_world_rules",
                    SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["chapter-id"] = chapter.Id,
                    },
                    RuleId: rule.Id,
                    TargetEntityType: delta.EntityType,
                    TargetId: delta.EntityId
                )
            );
        }

        // 死亡/失踪仍出场
        var participantIds = scenes
            .SelectMany(s => s.ParticipantIds)
            .Concat(chapter.CharacterIds)
            .Distinct(StringComparer.Ordinal);
        foreach (var id in participantIds)
        {
            var character = novel.Characters.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.Ordinal)
            );
            if (character is null)
            {
                continue;
            }
            if (character.Status == CharacterStatus.Deceased)
            {
                signals.Add(
                    new HarnessSignal(
                        Id: $"drift.dead:{id}",
                        Kind: HarnessSignalKind.Drift,
                        Severity: HarnessSeverity.Critical,
                        Title: $"已死亡角色「{character.Name}」仍在本章出场",
                        Detail: "场景/章节仍列为参与者",
                        Prescription: "改为回忆/提及，或先修正角色状态或本章计划",
                        SuggestedTool: "edit_entities",
                        SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["entity-type"] = "character",
                            ["entity-id"] = id,
                        },
                        TargetEntityType: EntityType.Character,
                        TargetId: id
                    )
                );
            }
            else if (character.Status == CharacterStatus.Missing)
            {
                signals.Add(
                    new HarnessSignal(
                        Id: $"drift.missing:{id}",
                        Kind: HarnessSignalKind.Drift,
                        Severity: HarnessSeverity.Warning,
                        Title: $"失踪角色「{character.Name}」仍在本章出场",
                        Detail: "场景/章节仍列为参与者",
                        Prescription: "确认是否已回归，或先登记状态变化",
                        SuggestedTool: "record_evolution",
                        TargetEntityType: EntityType.Character,
                        TargetId: id
                    )
                );
            }
        }

        // 悬空引用
        var exists = BuildExistence(novel);
        foreach (var (type, id) in SceneReferences(scenes, chapter))
        {
            if (string.IsNullOrWhiteSpace(id) || exists.Contains((type, id)))
            {
                continue;
            }
            signals.Add(
                new HarnessSignal(
                    Id: $"drift.dangling:{type.ToKebabCaseString()}:{id}",
                    Kind: HarnessSignalKind.Drift,
                    Severity: HarnessSeverity.Warning,
                    Title: $"悬空引用「{id}」",
                    Detail: $"场景/章节引用了不存在的 {type.ToKebabCaseString()}",
                    Prescription: "先创建实体或从场景移除",
                    SuggestedTool: "edit_entities",
                    TargetEntityType: type,
                    TargetId: id
                )
            );
        }

        // 状态一致性：组织覆灭/道具遗失/地点毁灭/情节线解决后的悬空使用
        var dissolvedFactions = novel
            .Factions.Where(f => f.Status == FactionStatus.Dissolved)
            .Select(f => f.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var character in novel.Characters)
        {
            if (character.FactionId is not null && dissolvedFactions.Contains(character.FactionId))
            {
                var factionName =
                    novel.Factions.FirstOrDefault(f => f.Id == character.FactionId)?.Name
                    ?? character.FactionId;
                signals.Add(
                    new HarnessSignal(
                        Id: $"drift.dissolved-faction-member:{character.Id}",
                        Kind: HarnessSignalKind.Drift,
                        Severity: HarnessSeverity.Critical,
                        Title: $"角色「{character.Name}」仍属于已覆灭组织「{factionName}」",
                        Detail: "组织状态为 Dissolved，但角色的 faction-id 仍指向它",
                        Prescription: "清空角色组织归属，或修正组织状态",
                        SuggestedTool: "record_evolution",
                        SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["novel-id"] = novel.Id,
                            ["chapter-id"] = chapter.Id,
                        },
                        TargetEntityType: EntityType.Character,
                        TargetId: character.Id
                    )
                );
            }
        }

        var unavailableItems = novel
            .Items.Where(i => i.Status is ItemStatus.Lost or ItemStatus.Destroyed)
            .ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            foreach (var itemId in scene.ItemIds)
            {
                if (unavailableItems.TryGetValue(itemId, out var item))
                {
                    signals.Add(
                        new HarnessSignal(
                            Id: $"drift.lost-item-used:{scene.Id}:{itemId}",
                            Kind: HarnessSignalKind.Drift,
                            Severity: HarnessSeverity.Warning,
                            Title: $"场景使用了已{item.Status}的道具「{item.Name}」",
                            Detail: $"场景「{scene.Title}」的 item-ids 含该道具",
                            Prescription: "改为回忆/提及，或先登记道具恢复",
                            SuggestedTool: "record_evolution",
                            TargetEntityType: EntityType.Item,
                            TargetId: itemId
                        )
                    );
                }
            }
        }

        var ruinedLocations = novel
            .Locations.Where(l => l.Status is LocationStatus.Destroyed or LocationStatus.Sealed)
            .ToDictionary(l => l.Id, l => l, StringComparer.Ordinal);
        foreach (var scene in scenes)
        {
            if (
                scene.LocationId is not null
                && ruinedLocations.TryGetValue(scene.LocationId, out var location)
            )
            {
                signals.Add(
                    new HarnessSignal(
                        Id: $"drift.destroyed-location:{scene.Id}:{location.Id}",
                        Kind: HarnessSignalKind.Drift,
                        Severity: HarnessSeverity.Critical,
                        Title: $"场景发生在已{location.Status}的地点「{location.Name}」",
                        Detail: $"场景「{scene.Title}」的 location-id 指向该地点",
                        Prescription: "改到可用地点，或先登记地点恢复/替代",
                        SuggestedTool: "manage_scene",
                        TargetEntityType: EntityType.Location,
                        TargetId: location.Id
                    )
                );
            }
        }

        var closedPlotlines = novel
            .Plotlines.Where(p => p.Status is PlotlineStatus.Resolved or PlotlineStatus.Abandoned)
            .ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        var chapterPlotlineRefs = chapter
            .PlotlineIds.Concat(scenes.SelectMany(s => s.PlotlineIds))
            .Distinct(StringComparer.Ordinal);
        foreach (var plotlineId in chapterPlotlineRefs)
        {
            if (
                closedPlotlines.TryGetValue(plotlineId, out var plotline)
                && !string.Equals(
                    plotline.ResolvedAtChapterId,
                    chapter.Id,
                    StringComparison.Ordinal
                )
            )
            {
                signals.Add(
                    new HarnessSignal(
                        Id: $"drift.resolved-plotline:{plotline.Id}",
                        Kind: HarnessSignalKind.Drift,
                        Severity: HarnessSeverity.Warning,
                        Title: $"本章推进了已{plotline.Status}的情节线「{plotline.Name}」",
                        Detail: "章节/场景仍引用该情节线",
                        Prescription: "确认是否需要重新激活，或从本章引用中移除",
                        SuggestedTool: "record_evolution",
                        TargetEntityType: EntityType.Plotline,
                        TargetId: plotline.Id
                    )
                );
            }
        }

        // 未登记死亡（正文检测的特例）
        foreach (
            var death in DeathCandidateDetector.Detect(
                novel,
                chapter,
                view.GetChapterStateChanges(chapter.Id)
            )
        )
        {
            signals.Add(
                new HarnessSignal(
                    Id: $"drift.unrecorded-death:{death.CharacterId}",
                    Kind: HarnessSignalKind.Drift,
                    Severity: HarnessSeverity.Warning,
                    Title: $"{death.Summary}",
                    Detail: death.Evidence,
                    Prescription: "登记 status=deceased 的变化，或确认误报忽略",
                    SuggestedTool: "record_evolution",
                    SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["novel-id"] = novel.Id,
                        ["chapter-id"] = chapter.Id,
                        ["target-id"] = death.CharacterId,
                        ["aspect"] = "status",
                        ["to"] = "deceased",
                    },
                    TargetEntityType: EntityType.Character,
                    TargetId: death.CharacterId,
                    Evidence: death.Evidence
                )
            );
        }

        // 卡顿：没有计划变化
        if (scenes.Count == 0 || scenes.All(s => s.PlannedDeltas.Count == 0))
        {
            signals.Add(
                new HarnessSignal(
                    Id: $"stall.no-planned-delta:{chapter.Id}",
                    Kind: HarnessSignalKind.Stall,
                    Severity: HarnessSeverity.Warning,
                    Title: "本章没有声明计划变化",
                    Detail: "缺少行动/状态/关系/规则的计划，容易写成平推或卡住",
                    Prescription: "用 plan_scene_deltas 至少声明一条变化",
                    SuggestedTool: "plan_scene_deltas",
                    SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["novel-id"] = novel.Id,
                        ["scene-id"] = scenes.Count == 1 ? scenes[0].Id : "",
                    }
                )
            );
        }

        // 卡顿：情节线停转
        foreach (var plotline in novel.Plotlines)
        {
            if (!string.IsNullOrWhiteSpace(plotline.ResolvedAtChapterId))
            {
                continue;
            }
            var lastOrder = LastPlotlineOrder(novel, plotline.Id);
            if (lastOrder is null || chapter.Order - lastOrder.Value < options.StallWindow)
            {
                continue;
            }
            signals.Add(
                new HarnessSignal(
                    Id: $"stall.plotline:{plotline.Id}",
                    Kind: HarnessSignalKind.Stall,
                    Severity: HarnessSeverity.Warning,
                    Title: $"情节线「{plotline.Name}」已 {chapter.Order - lastOrder.Value} 章未推进",
                    Detail: "最近窗口内没有章节/场景推进该情节线",
                    Prescription: "安排一次推进/揭示，或标记已解决",
                    SuggestedTool: "advance_plotline",
                    SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["novel-id"] = novel.Id,
                        ["plotline-id"] = plotline.Id,
                    },
                    TargetEntityType: EntityType.Plotline,
                    TargetId: plotline.Id
                )
            );
        }

        // 文笔风险（只路由）
        var density = WritingProfile.ScanAiBuzzwordDensity(chapter.Content);
        if (density > 1.0)
        {
            signals.Add(
                new HarnessSignal(
                    Id: $"quality.ai-buzz:{chapter.Id}",
                    Kind: HarnessSignalKind.Quality,
                    Severity: HarnessSeverity.Warning,
                    Title: "AI 味密度偏高",
                    Detail: $"{density:0.0}/千字（阈值 1.0）",
                    Prescription: "写完后按云端诊断的 A-9 处方改写；本体 harness 不评分文笔"
                )
            );
        }

        // 卡顿：本章无状态记录
        if (chapter.Content.Length > 800 && view.GetChapterStateChanges(chapter.Id).Count == 0)
        {
            signals.Add(
                new HarnessSignal(
                    Id: $"stall.no-state-change:{chapter.Id}",
                    Kind: HarnessSignalKind.Stall,
                    Severity: HarnessSeverity.Info,
                    Title: "本章尚未登记状态变化",
                    Detail: "正文已超过 800 字，但没有 EntityStateChange",
                    Prescription: "如确有变化，调用 scan_ontology 提案或 record_evolution 登记",
                    SuggestedTool: "scan_ontology",
                    SuggestedArgs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["chapter-ids"] = chapter.Id,
                    }
                )
            );
        }

        var selected = signals
            .OrderByDescending(s => s.Severity)
            .ThenBy(s => s.Kind)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, options.MaxSignals))
            .ToList();
        var blocksCloud =
            options.GateCloud && selected.Any(s => s.Severity == HarnessSeverity.Critical);
        return new OntologyHarnessResult(
            selected,
            material,
            blocksCloud,
            Render(chapter, material, selected)
        );
    }

    private static string Render(
        Chapter chapter,
        IReadOnlyList<MaterialDelta> material,
        IReadOnlyList<HarnessSignal> signals
    )
    {
        if (material.Count == 0 && signals.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder();
        text.AppendLine(
            $"{Prefix} 第{chapter.Order}章 · {chapter.Title}（chapter-id: {chapter.Id}）"
        );
        if (material.Count > 0)
        {
            text.AppendLine("本体变化：");
            foreach (var delta in material)
            {
                text.AppendLine($"- {delta.Summary}（{delta.EntityName}）");
            }
        }
        if (signals.Count > 0)
        {
            text.AppendLine("风险：");
            foreach (var signal in signals)
            {
                text.AppendLine(
                    $"- [{SeverityLabel(signal.Severity)}] {signal.Title}：{signal.Detail} → {signal.Prescription}"
                );
            }
            text.AppendLine("建议：");
            text.AppendLine(
                "- 先调用 check_world_rules(chapter_id=\"…\")；需要素材时调用 knowledge_hint；没有变化请忽略本提示。"
            );
        }
        text.AppendLine("边界：本地只做结构检查与文笔路由，不评分；文笔/爽感由云端诊断负责。");
        return text.ToString().TrimEnd();
    }

    private static string SeverityLabel(HarnessSeverity severity) =>
        severity switch
        {
            HarnessSeverity.Critical => "硬约束",
            HarnessSeverity.Warning => "警告",
            _ => "提示",
        };

    private static string TitleOf(WorldRule rule) =>
        !string.IsNullOrWhiteSpace(rule.Description) ? rule.Description : rule.Type;

    private static HashSet<(EntityType Type, string Id)> BuildExistence(Novel novel)
    {
        var result = new HashSet<(EntityType, string)>();
        foreach (var c in novel.Characters)
            result.Add((EntityType.Character, c.Id));
        foreach (var f in novel.Factions)
            result.Add((EntityType.Faction, f.Id));
        foreach (var l in novel.Locations)
            result.Add((EntityType.Location, l.Id));
        foreach (var i in novel.Items)
            result.Add((EntityType.Item, i.Id));
        foreach (var p in novel.Plotlines)
            result.Add((EntityType.Plotline, p.Id));
        foreach (var s in novel.Scenes)
            result.Add((EntityType.Scene, s.Id));
        return result;
    }

    private static IEnumerable<(EntityType Type, string Id)> SceneReferences(
        IReadOnlyList<SceneDefinition> scenes,
        Chapter chapter
    )
    {
        foreach (var scene in scenes)
        {
            foreach (var id in scene.ParticipantIds)
                yield return (EntityType.Character, id);
            if (!string.IsNullOrWhiteSpace(scene.LocationId))
                yield return (EntityType.Location, scene.LocationId);
            foreach (var id in scene.ItemIds)
                yield return (EntityType.Item, id);
            foreach (var id in scene.PlotlineIds)
                yield return (EntityType.Plotline, id);
        }
        foreach (var id in chapter.CharacterIds)
            yield return (EntityType.Character, id);
        foreach (var id in chapter.PlotlineIds)
            yield return (EntityType.Plotline, id);
    }

    private static int? LastPlotlineOrder(Novel novel, string plotlineId)
    {
        int? last = null;
        foreach (var chapter in novel.Chapters)
        {
            if (chapter.PlotlineIds.Contains(plotlineId, StringComparer.Ordinal))
            {
                last = last is null ? chapter.Order : Math.Max(last.Value, chapter.Order);
            }
        }
        var orders = novel
            .Chapters.GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);
        foreach (var scene in novel.Scenes)
        {
            if (
                scene.ChapterId is { } chapterId
                && orders.TryGetValue(chapterId, out var order)
                && scene.PlotlineIds.Contains(plotlineId, StringComparer.Ordinal)
            )
            {
                last = last is null ? order : Math.Max(last.Value, order);
            }
        }
        return last;
    }
}
