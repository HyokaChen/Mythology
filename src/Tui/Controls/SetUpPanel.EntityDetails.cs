using System.Text;
using Core.Data;
using Core.Novels;

namespace Tui.Controls;

/// <summary>
/// SetUpPanel 实体详情 Markup 构建器（七类实体：角色/组织/道具/地点/情节线/场景/关系）。
///
/// 从 SetUpPanel.cs 拆出：该类 3000+ 行，这七个构建器是纯静态字符串装配
/// （不触碰面板状态），独立成文件后可按实体类型各自演进；转义约定统一
/// <c>ToolCallRenderer.Escape</c>（小说数据进 Markup 前必须转义）。
/// </summary>
public sealed partial class SetUpPanel
{
    // ── 实体详情构建器 ──
    // ── 实体详情构建器 ──

    /// <summary>
    /// 性别存储值 → 中文展示标签。存储值有两种历史格式：对话框写的英文枚举
    /// （"Male"/"Female"/"Other"，与 Role/Faction.Type 同约定，是跨仓线协议值，不改）
    /// 与种子数据/早期版本的中文（"男"/"女"）。展示侧统一归一成中文；
    /// 未识别的非空值原样返回（让用户看见自己存了什么）。
    /// </summary>
    private static string GenderLabel(string? gender) =>
        gender switch
        {
            "男" or "Male" => "男",
            "女" or "Female" => "女",
            "其他" or "Other" => "其他",
            _ => gender ?? "",
        };

    // ── 实体状态标签（发布事务/联动会改这些字段，卡片必须看得见）──

    /// <summary>角色生存状态 → 带色标签。</summary>
    private static string CharacterStatusLabel(CharacterStatus status) =>
        status switch
        {
            CharacterStatus.Active => "[green]活跃[/]",
            CharacterStatus.Deceased => "[red]已死亡[/]",
            CharacterStatus.Missing => "[yellow]失踪[/]",
            CharacterStatus.Transformed => "[cyan]已转变[/]",
            _ => $"[dim]{status}[/]",
        };

    /// <summary>组织状态 → 带色标签。</summary>
    private static string FactionStatusLabel(FactionStatus status) =>
        status switch
        {
            FactionStatus.Active => "[green]活跃[/]",
            FactionStatus.Dormant => "[yellow]蛰伏[/]",
            FactionStatus.Dissolved => "[red]覆灭[/]",
            _ => $"[dim]{status}[/]",
        };

    /// <summary>道具状态 → 带色标签。</summary>
    private static string ItemStatusLabel(ItemStatus status) =>
        status switch
        {
            ItemStatus.Active => "[green]完好[/]",
            ItemStatus.Lost => "[yellow]遗失[/]",
            ItemStatus.Destroyed => "[red]损毁[/]",
            _ => $"[dim]{status}[/]",
        };

    /// <summary>地点状态 → 带色标签。</summary>
    private static string LocationStatusLabel(LocationStatus status) =>
        status switch
        {
            LocationStatus.Active => "[green]存在[/]",
            LocationStatus.Destroyed => "[red]毁灭[/]",
            LocationStatus.Sealed => "[yellow]封印[/]",
            LocationStatus.Abandoned => "[dim]废弃[/]",
            _ => $"[dim]{status}[/]",
        };

    /// <summary>情节线状态 → 带色标签。</summary>
    private static string PlotlineStatusLabel(PlotlineStatus status) =>
        status switch
        {
            PlotlineStatus.Active => "[green]进行中[/]",
            PlotlineStatus.Resolved => "[cyan]已解决[/]",
            PlotlineStatus.Abandoned => "[dim]放弃[/]",
            _ => $"[dim]{status}[/]",
        };

    /// <summary>
    /// 构建角色详情 Markup（性别/年龄/定位/状态/当前地点/性格/外貌/背景/动机/标签/能力/弱点/习惯/备注）。
    /// <paramref name="locNameMap"/> 解析 CurrentLocationId（地点被删时回显 id，不隐藏信息）。
    /// </summary>
    private static string BuildCharacterDetail(
        Character ch,
        IReadOnlyDictionary<string, string>? locNameMap = null
    )
    {
        var parts = new List<string>();

        // 性别：Gender 是跨仓线协议值，对话框存英文枚举（"Male"/"Female"/"Other"，
        // HarnessContextBuilder 会原样送进云端契约），种子数据/早期版本存中文
        // （"男"/"女"）。展示统一归一成中文标签，图标同理。
        var genderLabel = GenderLabel(ch.Gender);
        var genderIcon = genderLabel switch
        {
            "男" => "♂",
            "女" => "♀",
            _ => string.IsNullOrEmpty(genderLabel) ? "" : "🧑",
        };
        if (!string.IsNullOrEmpty(genderLabel))
            parts.Add($"[dim]性别:[/] {genderIcon} {ToolCallRenderer.Escape(genderLabel)}");

        if (!string.IsNullOrWhiteSpace(ch.Age))
            parts.Add($"[dim]年龄:[/] {ToolCallRenderer.Escape(ch.Age)}");

        var roleLabel = ch.Role switch
        {
            CharacterRole.Protagonist => "[bold yellow]⭐ 主角[/]",
            CharacterRole.Supporting => "[cyan]配角[/]",
            CharacterRole.Minor => "[dim]龙套[/]",
            CharacterRole.Antagonist => "[red]👹 反派[/]",
            _ => $"[dim]{ch.Role}[/]",
        };
        parts.Add($"[dim]定位:[/] {roleLabel}");
        parts.Add($"[dim]状态:[/] {CharacterStatusLabel(ch.Status)}");

        if (!string.IsNullOrWhiteSpace(ch.CurrentLocationId))
        {
            var locationName =
                locNameMap?.GetValueOrDefault(ch.CurrentLocationId) ?? ch.CurrentLocationId;
            parts.Add($"[dim]当前地点:[/] 📍 {ToolCallRenderer.Escape(locationName)}");
        }

        if (!string.IsNullOrWhiteSpace(ch.Personality))
            parts.Add($"[dim]性格:[/] {ToolCallRenderer.Escape(ch.Personality)}");
        if (!string.IsNullOrWhiteSpace(ch.Appearance))
            parts.Add($"[dim]外貌:[/] {ToolCallRenderer.Escape(ch.Appearance)}");
        if (!string.IsNullOrWhiteSpace(ch.Background))
            parts.Add($"[dim]背景:[/] {ToolCallRenderer.Escape(ch.Background)}");
        if (!string.IsNullOrWhiteSpace(ch.Motivation))
            parts.Add($"[dim]动机:[/] {ToolCallRenderer.Escape(ch.Motivation)}");

        // New fields
        if (ch.Tags.Count > 0)
        {
            parts.Add(
                $"[dim]标签:[/] {string.Join(", ", ch.Tags.Select(ToolCallRenderer.Escape))}"
            );
        }
        if (!string.IsNullOrWhiteSpace(ch.Abilities))
            parts.Add($"[dim]能力:[/] {ToolCallRenderer.Escape(ch.Abilities)}");
        if (!string.IsNullOrWhiteSpace(ch.Weakness))
            parts.Add($"[dim]弱点:[/] {ToolCallRenderer.Escape(ch.Weakness)}");
        if (!string.IsNullOrWhiteSpace(ch.Habits))
            parts.Add($"[dim]习惯:[/] {ToolCallRenderer.Escape(ch.Habits)}");
        if (!string.IsNullOrWhiteSpace(ch.Notes))
            parts.Add($"[dim]备注:[/] {ToolCallRenderer.Escape(ch.Notes)}");

        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }

    /// <summary>构建组织详情 Markup（描述/类型/首领/总部/目标/成员/对外关系）。</summary>
    private static string BuildFactionDetailWithName(
        Faction f,
        string leaderName,
        Dictionary<string, string> charNameMap,
        List<FactionRelationship> factionRels,
        Dictionary<string, string> factionNameMap,
        Dictionary<string, string> locNameMap
    )
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Description))
            parts.Add($"[dim]描述:[/] {ToolCallRenderer.Escape(f.Description)}");
        var typeLabel = f.Type switch
        {
            FactionType.Sect => "🏯 门派",
            FactionType.Company => "🏢 公司",
            FactionType.Family => "👪 家族",
            FactionType.Gang => "🏴 帮会",
            FactionType.Government => "🏦 政府",
            FactionType.Other => "⚪ 其他",
            _ => f.Type.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");
        parts.Add($"[dim]状态:[/] {FactionStatusLabel(f.Status)}");
        if (!string.IsNullOrWhiteSpace(f.Leader))
            parts.Add($"[dim]首领:[/] {ToolCallRenderer.Escape(leaderName)}");
        if (!string.IsNullOrWhiteSpace(f.HeadquartersId))
        {
            var hqName = locNameMap.GetValueOrDefault(f.HeadquartersId, f.HeadquartersId);
            parts.Add($"[dim]总部:[/] 📍 {ToolCallRenderer.Escape(hqName)}");
        }
        if (!string.IsNullOrWhiteSpace(f.Goal))
            parts.Add($"[dim]目标:[/] {ToolCallRenderer.Escape(f.Goal)}");

        // Members: resolve character IDs → names
        if (f.Members.Count > 0)
        {
            var memberNames = f
                .Members.Select(id => charNameMap.GetValueOrDefault(id, id))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(ToolCallRenderer.Escape);
            parts.Add($"[dim]成员:[/] {string.Join("、", memberNames)}");
        }

        // Faction relationships: show allies, enemies, etc.
        var rels = factionRels.Where(fr => fr.FactionAId == f.Id || fr.FactionBId == f.Id).ToList();
        if (rels.Count > 0)
        {
            var relParts = new List<string>();
            foreach (var fr in rels)
            {
                var otherId = fr.FactionAId == f.Id ? fr.FactionBId : fr.FactionAId;
                var otherName = factionNameMap.GetValueOrDefault(otherId, otherId);
                var relType = fr.Type switch
                {
                    FactionRelationType.Ally => "🤝 盟友",
                    FactionRelationType.Enemy => "💥 敌对",
                    FactionRelationType.Neutral => "⚪ 中立",
                    FactionRelationType.Trade => "💰 贸易",
                    FactionRelationType.Vassal => "🏴 臣属",
                    _ => fr.Type.ToString(),
                };
                relParts.Add($"{relType}: {ToolCallRenderer.Escape(otherName)}");
            }
            parts.Add($"[dim]对外关系:[/]\n{string.Join("\n", relParts)}");
        }

        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }

    /// <summary>构建道具详情 Markup（类型/描述/持有者及状态）。</summary>
    private static string BuildItemDetailWithName(
        Item it,
        string ownerName,
        CharacterStatus? ownerStatus
    )
    {
        var parts = new List<string>();
        var typeLabel = it.Type switch
        {
            ItemType.Weapon => "💥 武器",
            ItemType.Artifact => "🔮 法器",
            ItemType.Token => "📿 信物",
            ItemType.Consumable => "🧪 消耗品",
            ItemType.Other => "⚪ 其他",
            _ => it.Type.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");
        parts.Add($"[dim]状态:[/] {ItemStatusLabel(it.Status)}");
        if (!string.IsNullOrWhiteSpace(it.Description))
            parts.Add($"[dim]描述:[/] {ToolCallRenderer.Escape(it.Description)}");
        if (!string.IsNullOrWhiteSpace(it.OwnerId))
        {
            var statusTag = ownerStatus switch
            {
                CharacterStatus.Active => "[green]活跃[/]",
                CharacterStatus.Deceased => "[red]死亡[/]",
                CharacterStatus.Missing => "[yellow]失踪[/]",
                CharacterStatus.Transformed => "[cyan]变化[/]",
                _ => "",
            };
            var ownerDisplay = string.IsNullOrEmpty(statusTag)
                ? ToolCallRenderer.Escape(ownerName)
                : $"{ToolCallRenderer.Escape(ownerName)} ({statusTag})";
            parts.Add($"[dim]持有者:[/] {ownerDisplay}");
        }
        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }

    /// <summary>构建地点详情 Markup（类型/氛围/描述/上级地点层级链/下级地点列表）。</summary>
    private static string BuildLocationDetailWithName(Location l, List<Location> allLocations)
    {
        var parts = new List<string>();
        var typeLabel = l.Type switch
        {
            LocationType.City => "🌆 城市",
            LocationType.Rural => "🌾 乡村",
            LocationType.Indoor => "🏠 室内",
            LocationType.Wilderness => "🌲 荒野",
            LocationType.Fictional => "🌀 虚构",
            LocationType.Other => "⚪ 其他",
            _ => l.Type.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");
        parts.Add($"[dim]状态:[/] {LocationStatusLabel(l.Status)}");
        if (!string.IsNullOrWhiteSpace(l.Atmosphere))
            parts.Add($"[dim]氛围:[/] {ToolCallRenderer.Escape(l.Atmosphere)}");
        if (!string.IsNullOrWhiteSpace(l.Description))
            parts.Add($"[dim]描述:[/] {ToolCallRenderer.Escape(l.Description)}");

        // Parent hierarchy: resolve ParentId chain → "蜀山 > 凌霄峰"
        if (!string.IsNullOrWhiteSpace(l.ParentId))
        {
            var locMap = allLocations.ToDictionary(loc => loc.Id, loc => loc);
            var chain = new List<string>();
            var currentId = l.ParentId;
            var visited = new HashSet<string>(); // guard against cycles
            while (
                currentId is not null
                && locMap.TryGetValue(currentId, out var parent)
                && visited.Add(currentId)
            )
            {
                chain.Add(ToolCallRenderer.Escape(parent.Name));
                currentId = parent.ParentId;
            }
            if (chain.Count > 0)
            {
                chain.Reverse(); // root first
                parts.Add($"[dim]上级地点:[/] {string.Join(" > ", chain)}");
            }
        }

        // Child locations: find all locations whose ParentId == this location's Id
        var children = allLocations
            .Where(c => c.ParentId == l.Id)
            .Select(c => ToolCallRenderer.Escape(c.Name))
            .ToList();
        if (children.Count > 0)
            parts.Add($"[dim]下级地点:[/] {string.Join("、", children)}");

        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }

    /// <summary>构建情节线详情 Markup（描述/类型/紧张度/计划揭示/已解决/转折/关联场景）。</summary>
    private static string BuildPlotlineDetail(
        Plotline p,
        List<SceneDefinition> allScenes,
        List<Chapter> allChapters
    )
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Description))
            parts.Add($"[dim]描述:[/] {ToolCallRenderer.Escape(p.Description)}");
        var typeLabel = p.Type switch
        {
            PlotlineType.Main => "⭐ 主线",
            PlotlineType.Side => "🔄 支线",
            PlotlineType.Hidden => "❓ 暗线",
            PlotlineType.Foreshadowing => "⚡ 伏笔",
            _ => p.Type.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");
        parts.Add($"[dim]状态:[/] {PlotlineStatusLabel(p.Status)}");
        var tensionLabel = p.Tension switch
        {
            PlotlineTension.Low => "🟢 低",
            PlotlineTension.Medium => "🟡 中",
            PlotlineTension.High => "🟠 高",
            PlotlineTension.Critical => "🔴 临界",
            _ => p.Tension.ToString(),
        };
        parts.Add($"[dim]紧张度:[/] {tensionLabel}");
        if (!string.IsNullOrWhiteSpace(p.PlannedRevealChapterId))
        {
            var ch = allChapters.FirstOrDefault(c => c.Id == p.PlannedRevealChapterId);
            parts.Add(
                ch != null
                    ? $"[dim]计划揭示:[/] 第{ch.Order}章 {ToolCallRenderer.Escape(ch.Title)}"
                    : "[dim]计划揭示:[/] (章节已删除)"
            );
        }
        if (p.ResolvedAtChapterId != null)
        {
            var ch = allChapters.FirstOrDefault(c => c.Id == p.ResolvedAtChapterId);
            parts.Add(
                ch != null
                    ? $"[dim]已解决于:[/] 第{ch.Order}章 {ToolCallRenderer.Escape(ch.Title)}"
                    : "[dim]已解决于:[/] (章节已删除)"
            );
        }
        if (!string.IsNullOrWhiteSpace(p.Turnpoints))
            parts.Add($"[dim]关键转折:[/] {ToolCallRenderer.Escape(p.Turnpoints)}");

        // 关联场景：查找 PlotlineIds 中包含此情节线 ID 的场景
        var linkedScenes = allScenes
            .Where(s => s.PlotlineIds.Contains(p.Id))
            .OrderBy(s => s.Order)
            .ToList();
        if (linkedScenes.Count > 0)
        {
            var sceneNames = linkedScenes
                .OrderBy(s => s.Order)
                .Select(s => ToolCallRenderer.Escape(s.Title))
                .ToList();
            parts.Add($"[dim]关联场景:[/] {string.Join(", ", sceneNames)}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>构建场景详情 Markup（类型/章节/地点/时间/参与角色/推进情节线/冲突/目标/情绪弧线/备注）。</summary>
    private static string BuildSceneDetail(
        SceneDefinition s,
        Dictionary<string, string> charNameMap,
        Dictionary<string, string> chapterMap,
        List<Location> allLocations,
        Dictionary<string, string> plotlineNameMap
    )
    {
        var parts = new List<string>();

        var typeLabel = s.SceneType switch
        {
            SceneType.Action => "💥 动作",
            SceneType.Dialogue => "💬 对话",
            SceneType.Reflection => "💭 反思",
            SceneType.Transition => "🔄 过渡",
            _ => s.SceneType.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");

        if (
            !string.IsNullOrEmpty(s.ChapterId)
            && chapterMap.TryGetValue(s.ChapterId, out var chTitle)
        )
            parts.Add($"[dim]章节:[/] {ToolCallRenderer.Escape(chTitle)}");

        if (!string.IsNullOrWhiteSpace(s.LocationId))
        {
            var locName =
                allLocations.FirstOrDefault(l => l.Id == s.LocationId)?.Name ?? s.LocationId;
            parts.Add($"[dim]地点:[/] {ToolCallRenderer.Escape(locName)}");
        }

        if (!string.IsNullOrWhiteSpace(s.TimeDescription))
            parts.Add($"[dim]时间:[/] {ToolCallRenderer.Escape(s.TimeDescription)}");

        // 参与角色
        if (s.ParticipantIds.Count > 0)
        {
            var names = s
                .ParticipantIds.Select(id => charNameMap.GetValueOrDefault(id, id))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(ToolCallRenderer.Escape);
            parts.Add($"[dim]角色:[/] {string.Join(", ", names)}");
        }

        // 推进的情节线
        if (s.PlotlineIds.Count > 0)
        {
            var names = s
                .PlotlineIds.Select(id => plotlineNameMap.GetValueOrDefault(id, id))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(ToolCallRenderer.Escape);
            parts.Add($"[dim]情节线:[/] {string.Join(", ", names)}");
        }

        if (!string.IsNullOrWhiteSpace(s.Conflict))
            parts.Add($"[dim]冲突:[/] {ToolCallRenderer.Escape(s.Conflict)}");
        if (!string.IsNullOrWhiteSpace(s.Goal))
            parts.Add($"[dim]目标:[/] {ToolCallRenderer.Escape(s.Goal)}");
        if (!string.IsNullOrWhiteSpace(s.EmotionalArc))
            parts.Add($"[dim]情绪弧线:[/] {ToolCallRenderer.Escape(s.EmotionalArc)}");
        if (!string.IsNullOrWhiteSpace(s.Notes))
            parts.Add($"[dim]备注:[/] {ToolCallRenderer.Escape(s.Notes)}");

        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }

    /// <summary>构建关系详情 Markup（类型/强度/描述）。</summary>
    private static string BuildRelationshipDetail(Relationship r)
    {
        var parts = new List<string>();
        var typeLabel = r.Type switch
        {
            RelationshipType.Family => "👪 家人",
            RelationshipType.Love => "💕 恋人",
            RelationshipType.Friend => "🤝 朋友",
            RelationshipType.Rival => "💥 对手",
            RelationshipType.Enemy => "💢 敌人",
            RelationshipType.Mentor => "📚 导师",
            RelationshipType.MasterServant => "🙇 主仆",
            RelationshipType.FellowDisciple => "👥 同门",
            RelationshipType.ParentOf => "👪 父母",
            RelationshipType.ChildOf => "👶 子女",
            RelationshipType.Sibling => "👫 兄弟姐妹",
            RelationshipType.Spouse => "💍 夫妻",
            RelationshipType.Sworn => "🪢 结拜",
            RelationshipType.Disciple => "🎓 徒弟",
            RelationshipType.Ally => "🤝 盟友",
            _ => r.Type.ToString(),
        };
        parts.Add($"[dim]类型:[/] {typeLabel}");
        var strengthLabel = r.Strength switch
        {
            RelationshipStrength.Weak => "💨 弱",
            RelationshipStrength.Medium => "🔗 中",
            RelationshipStrength.Strong => "💥 强",
            RelationshipStrength.Extreme => "🔥 极强",
            _ => r.Strength.ToString(),
        };
        parts.Add($"[dim]强度:[/] {strengthLabel}");
        if (!string.IsNullOrWhiteSpace(r.Description))
            parts.Add($"[dim]描述:[/] {ToolCallRenderer.Escape(r.Description)}");
        return parts.Count > 0 ? string.Join("\n", parts) : "[dim](无详细信息)[/]";
    }
}
