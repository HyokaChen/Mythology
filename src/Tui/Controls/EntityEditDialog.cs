using Core.Data;
using Core.Evolution;
using Core.Novels;
using Microsoft.Extensions.Logging.Abstractions;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 章节编辑作用域（大纲行 ⋯ 菜单的「细纲 / 内容」两个入口）：
/// Full = 全部页签；Outline = 除正文外的页签；Content = 只留正文页签。
/// 保存按键名守卫：作用域表单缺省的字段（如内容窗里的标题）保持原值，不清空。
/// </summary>
public enum ChapterEditScope
{
    Full,
    Outline,
    Content,
}

/// <summary>
/// 实体编辑对话框 — 用于创建或编辑小说实体（角色、组织、地点、情节线等）。
/// 支持选项卡分组、Tag 多选、只读折叠区、ID 引用 Select 下拉。
/// </summary>
public sealed class EntityEditDialog(
    INovelStore store,
    EntityType entityType,
    string? entityId = null,
    string? novelId = null,
    string? presetVolumeId = null,
    ChapterEditScope editScope = ChapterEditScope.Full
)
{
    /// <summary>当前小说 ID。</summary>
    private readonly string _novelId = novelId ?? "";

    /// <summary>新建章节时的预置所属分卷（null = 未分卷）。</summary>
    private readonly string? _presetVolumeId = presetVolumeId;

    /// <summary>章节编辑作用域（细纲 = 不含正文；内容 = 只留正文；新建恒 Full）。</summary>
    private readonly ChapterEditScope _editScope = editScope;

    // ── 字段类型 ──
    private enum FieldType
    {
        TextBox, // 单行文本
        TextArea, // 多行文本
        Select, // 下拉选择
        MultiSelect, // Tag 多选（从已有实体列表选取）
        FreeTag, // Tag 自由输入（如角色标签，回车添加）
        ReadOnlyTags, // 只读 Tag 展示（折叠项内）
        ReadOnly, // 只读单行信息（如章节号/字数/更新时间：展示用，不参与保存）
    }

    // ── 字段定义 ──
    private sealed record FieldDef(
        string Prop, // 属性名（对应实体模型字段）
        string Label, // 中文标签
        FieldType Type, // 字段类型
        string[]? Options = null, // 下拉选项显示名
        string[]? OptionValues = null, // 下拉选项值（枚举值或 ID）
        string? Placeholder = null, // 输入占位提示
        string? Icon = null // 只读 Tag 前缀图标
    );

    // ── 标签页定义 ──
    private sealed record TabDef(string Name, FieldDef[] Fields); // Name=标签名, Fields=字段列表

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Character（3 标签）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] CharacterTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Name", "姓名", FieldType.TextBox),
                new FieldDef(
                    "Gender",
                    "性别",
                    FieldType.Select,
                    ["男", "女", "其他"],
                    // 存英文枚举值：与 Role / Faction.Type / Location.Type 等字段同一约定，
                    // 且 HarnessContextBuilder 会把它原样送进云端契约（CloudCharacterInput）、
                    // DocumentBuilder 会把它拼进向量文档——是线协议值，不能改成中文。
                    // 渲染侧（SetUpPanel 的 ♂/♀ 图标）因此必须同时认中文旧值，见那边注释。
                    ["Male", "Female", "Other"]
                ),
                new FieldDef("Age", "年龄", FieldType.TextBox),
                new FieldDef(
                    "Role",
                    "定位",
                    FieldType.Select,
                    ["主角", "配角", "龙套", "反派"],
                    ["Protagonist", "Supporting", "Minor", "Antagonist"]
                ),
                new FieldDef("FactionId", "所属组织", FieldType.Select),
                new FieldDef("CurrentLocationId", "当前地点", FieldType.Select),
            ]
        ),
        new(
            "性格描写",
            [
                new FieldDef("Personality", "性格", FieldType.TextArea),
                new FieldDef("Appearance", "外貌", FieldType.TextArea),
                new FieldDef("Habits", "习惯", FieldType.TextArea),
                new FieldDef("Background", "背景", FieldType.TextArea),
                new FieldDef("Motivation", "动机", FieldType.TextArea),
                new FieldDef("Weakness", "弱点", FieldType.TextArea),
            ]
        ),
        new(
            "备注",
            [
                new FieldDef("Tags", "标签", FieldType.FreeTag, Placeholder: "回车添加标签"),
                new FieldDef("Abilities", "能力", FieldType.TextArea),
                new FieldDef("Notes", "备注", FieldType.TextArea),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Plotline（2 标签，增强：关联场景只读 Tag）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] PlotlineTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Name", "名称", FieldType.TextBox),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["主线", "支线", "隐藏", "伏笔"],
                    ["Main", "Side", "Hidden", "Foreshadowing"]
                ),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["进行中", "已解决", "放弃"],
                    ["Active", "Resolved", "Abandoned"]
                ),
                new FieldDef(
                    "Tension",
                    "紧张度",
                    FieldType.Select,
                    ["低", "中", "高", "临界"],
                    ["Low", "Medium", "High", "Critical"]
                ),
                new FieldDef("PlannedRevealChapterId", "计划揭示章节", FieldType.Select),
                new FieldDef("ResolvedAtChapterId", "解决章节", FieldType.Select),
            ]
        ),
        new(
            "叙事内容",
            [
                new FieldDef("Description", "描述", FieldType.TextArea),
                new FieldDef(
                    "Turnpoints",
                    "关键转折",
                    FieldType.TextArea,
                    Placeholder: "每行一个转折点，如：主角发现真相"
                ),
                new FieldDef("RelatedScenes", "关联场景", FieldType.MultiSelect, Icon: "🎬"),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Faction（2 标签，增强：成员多选 + 对外关系只读 Tag）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] FactionTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Name", "名称", FieldType.TextBox),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["门派", "公司", "家族", "帮会", "政府", "其他"],
                    ["Sect", "Company", "Family", "Gang", "Government", "Other"]
                ),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["活跃", "蛰伏", "覆灭"],
                    ["Active", "Dormant", "Dissolved"]
                ),
                new FieldDef("Leader", "首领", FieldType.Select),
                new FieldDef("HeadquartersId", "总部", FieldType.Select),
                new FieldDef("Description", "描述", FieldType.TextArea),
                new FieldDef("Goal", "目标", FieldType.TextArea),
            ]
        ),
        new(
            "成员与关系",
            [
                new FieldDef("Members", "成员", FieldType.MultiSelect),
                new FieldDef("FactionRelations", "对外关系", FieldType.ReadOnlyTags, Icon: "🤝"),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Location（平铺，增强：下级地点只读 Tag）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] LocationTabs =
    [
        new(
            "",
            [
                new FieldDef("Name", "名称", FieldType.TextBox),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["城市", "乡村", "室内", "荒野", "虚构", "其他"],
                    ["City", "Rural", "Indoor", "Wilderness", "Fictional", "Other"]
                ),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["存在", "毁灭", "封印", "废弃"],
                    ["Active", "Destroyed", "Sealed", "Abandoned"]
                ),
                new FieldDef("Atmosphere", "氛围", FieldType.TextBox),
                new FieldDef("Description", "描述", FieldType.TextArea),
                new FieldDef("ParentId", "所属区域", FieldType.Select),
                new FieldDef("ChildLocations", "下级地点", FieldType.ReadOnlyTags, Icon: "📍"),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Item（平铺，ID→Select）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] ItemTabs =
    [
        new(
            "",
            [
                new FieldDef("Name", "名称", FieldType.TextBox),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["武器", "法器", "信物", "消耗品", "其他"],
                    ["Weapon", "Artifact", "Token", "Consumable", "Other"]
                ),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["完好", "遗失", "损毁"],
                    ["Active", "Lost", "Destroyed"]
                ),
                new FieldDef("Description", "描述", FieldType.TextArea),
                new FieldDef("OwnerId", "持有者", FieldType.Select),
                new FieldDef("LocationId", "所在位置", FieldType.Select),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Relationship（平铺，ID→Select）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] RelationshipTabs =
    [
        new(
            "",
            [
                new FieldDef("CharacterAId", "角色A", FieldType.Select),
                new FieldDef("CharacterBId", "角色B", FieldType.Select),
                new FieldDef("SinceChapterId", "起始章节", FieldType.Select),
                new FieldDef("UntilChapterId", "结束章节", FieldType.Select),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["朋友", "对手", "家人", "恋人", "敌人", "导师", "主仆", "同门"],
                    [
                        "Friend",
                        "Rival",
                        "Family",
                        "Love",
                        "Enemy",
                        "Mentor",
                        "MasterServant",
                        "FellowDisciple",
                    ]
                ),
                new FieldDef(
                    "Strength",
                    "强度",
                    FieldType.Select,
                    ["弱", "中", "强", "极强"],
                    ["Weak", "Medium", "Strong", "Extreme"]
                ),
                new FieldDef("Description", "描述", FieldType.TextArea),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Scene（2 标签，删除 Order，多选→Tag）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] SceneTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Title", "标题", FieldType.TextBox),
                new FieldDef("ChapterId", "所属章节", FieldType.Select),
                new FieldDef("LocationId", "地点", FieldType.Select),
                new FieldDef("TimeDescription", "时间", FieldType.TextBox),
                new FieldDef(
                    "SceneType",
                    "场景类型",
                    FieldType.Select,
                    ["动作", "对话", "反思", "过渡"],
                    ["Action", "Dialogue", "Reflection", "Transition"]
                ),
            ]
        ),
        new(
            "叙事内容",
            [
                new FieldDef("ParticipantIds", "参与角色", FieldType.MultiSelect),
                new FieldDef("PlotlineIds", "推进情节线", FieldType.MultiSelect),
                new FieldDef("ItemIds", "出场道具", FieldType.MultiSelect),
                new FieldDef("Conflict", "冲突", FieldType.TextArea),
                new FieldDef("Goal", "目标", FieldType.TextArea),
                new FieldDef(
                    "EmotionalArc",
                    "情绪弧线",
                    FieldType.TextBox,
                    Placeholder: "如：紧张→释放→希望"
                ),
                new FieldDef("Notes", "备注", FieldType.TextArea),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Chapter（平铺，不变）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] ChapterTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Title", "标题", FieldType.TextBox),
                // 章节号/字数/更新时间都是只读信息：章节号按卷内自增（换卷从 1 重新计数），
                // 由 LoadEntityAsync 预计算后直接展示，不参与保存。
                new FieldDef("ChapterNumber", "章节号", FieldType.ReadOnly),
                new FieldDef("CharCount", "字数", FieldType.ReadOnly),
                new FieldDef("UpdatedAt", "更新时间", FieldType.ReadOnly),
                new FieldDef("Order", "全书序号", FieldType.TextBox),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["草稿", "发布"],
                    ["Draft", "Published"]
                ),
                new FieldDef("VolumeId", "所属分卷", FieldType.Select),
                new FieldDef(
                    "Summary",
                    "细纲·梗概",
                    FieldType.TextArea,
                    Placeholder: "本章发生什么"
                ),
            ]
        ),
        new(
            "细纲",
            [
                new FieldDef("PlotlineIds", "计划推进情节线", FieldType.MultiSelect),
                new FieldDef("CharacterIds", "计划出场角色", FieldType.MultiSelect),
                new FieldDef("Notes", "备注", FieldType.TextArea, Placeholder: "节奏/伏笔提示"),
            ]
        ),
        new("正文", [new FieldDef("Content", "内容", FieldType.TextArea)]),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — Volume（大纲二层：卷纲）/ Novel（大纲一层：总纲）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] VolumeTabs =
    [
        new(
            "基本信息",
            [
                new FieldDef("Name", "卷名", FieldType.TextBox),
                new FieldDef("Order", "卷序号", FieldType.TextBox),
                new FieldDef(
                    "Summary",
                    "卷纲",
                    FieldType.TextArea,
                    Placeholder: "本卷剧情走向 / 关键转折"
                ),
                new FieldDef("Description", "卷描述", FieldType.TextArea),
                new FieldDef("Goal", "本卷目标 / 核心冲突", FieldType.TextArea),
                new FieldDef(
                    "Notes",
                    "备注",
                    FieldType.TextArea,
                    Placeholder: "节奏/伏笔/卷末钩子"
                ),
            ]
        ),
        new(
            "关系",
            [
                new FieldDef("PlotlineIds", "推进情节线", FieldType.MultiSelect),
                new FieldDef("CharacterIds", "关键角色", FieldType.MultiSelect),
            ]
        ),
    ];

    private static readonly TabDef[] NovelTabs =
    [
        new(
            "小说定义",
            [
                new FieldDef("Title", "标题", FieldType.TextBox),
                new FieldDef("Genre", "类型", FieldType.TextBox),
                new FieldDef("Description", "简介", FieldType.TextArea),
                new FieldDef(
                    "Outline",
                    "总纲",
                    FieldType.TextArea,
                    Placeholder: "全书走向 / 主题 / 核心冲突 / 结局方向"
                ),
                new FieldDef("WritingStyle", "写作风格", FieldType.TextBox),
                new FieldDef(
                    "CompletionStatus",
                    "连载状态",
                    FieldType.Select,
                    ["连载中", "已完结"],
                    ["ongoing", "completed"]
                ),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — AuthorNote（随手记；写入口是 manage_note，这里只做查看/手工编辑）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] NoteTabs =
    [
        new(
            "",
            [
                new FieldDef("Title", "标题", FieldType.TextBox),
                new FieldDef(
                    "Body",
                    "正文",
                    FieldType.TextArea,
                    Placeholder: "随手记：设定 / 桥段 / 伏笔 / 灵感"
                ),
                new FieldDef("Tags", "标签", FieldType.FreeTag),
                new FieldDef(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["收件箱", "已确认", "已落地", "已废弃"],
                    ["Inbox", "Active", "Done", "Dropped"]
                ),
                new FieldDef("CapturedChapterOrder", "记录于第N章", FieldType.ReadOnly),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — TimelineEvent（平铺，不变）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] TimelineEventTabs =
    [
        new(
            "",
            [
                new FieldDef("Title", "标题", FieldType.TextBox),
                new FieldDef("Description", "描述", FieldType.TextArea),
                new FieldDef("ChapterId", "所属章节", FieldType.Select),
            ]
        ),
    ];

    // ═══════════════════════════════════════════════════════════════════════════
    // 字段定义 — FactionRelationship（平铺，ID→Select）
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly TabDef[] FactionRelationshipTabs =
    [
        new(
            "",
            [
                new FieldDef("FactionAId", "组织A", FieldType.Select),
                new FieldDef("FactionBId", "组织B", FieldType.Select),
                new FieldDef(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["盟友", "敌对", "中立"],
                    ["Ally", "Enemy", "Neutral"]
                ),
                new FieldDef("Description", "描述", FieldType.TextArea),
            ]
        ),
    ];

    // ── 列表转换 ──

    private static List<string> ParseTags(string commaTags)
    {
        if (string.IsNullOrWhiteSpace(commaTags))
            return [];
        return commaTags
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 从 values 字典中获取字符串值。支持 string 和 List&lt;string&gt; 类型。
    /// List&lt;string&gt; 会被忽略（由各 save 分支直接处理），返回 defaultValue。
    /// </summary>
    private static string Val(
        Dictionary<string, object?> values,
        string key,
        string defaultValue = ""
    )
    {
        if (!values.TryGetValue(key, out var val) || val is null)
            return defaultValue;
        if (val is string s)
            return s;
        // List<string> values are handled directly by model assignment, not via this helper
        return defaultValue;
    }

    /// <summary>
    /// 从 values 字典中获取 List&lt;string&gt; 值。
    /// </summary>
    private static List<string> ValList(Dictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var val) || val is null)
            return [];
        if (val is List<string> list)
            return list;
        return [];
    }

    // ── Select 选项加载 ──

    /// <summary>
    /// 根据字段名异步加载 Select 选项列表（名称 + ID）。
    /// 返回 (displayNames, optionValues) — displayNames 包含"（无）"首项。
    /// </summary>
    private async Task<(string[] DisplayNames, string[] OptionValues)> LoadSelectOptionsAsync(
        string prop
    )
    {
        const string noneDisplay = "（无）";
        const string noneValue = "";

        switch (prop)
        {
            case "FactionId":
            {
                var factions = await store.GetFactions(_novelId);
                var names = factions.Select(f => f.Name).Prepend(noneDisplay).ToArray();
                var values = factions.Select(f => f.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "Leader":
            {
                var chars = await store.GetCharacters(_novelId);
                var names = chars.Select(c => c.Name).Prepend(noneDisplay).ToArray();
                var values = chars.Select(c => c.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "HeadquartersId":
            {
                var locs = await store.GetLocations(_novelId);
                var names = locs.Select(l => l.Name).Prepend(noneDisplay).ToArray();
                var values = locs.Select(l => l.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "ChapterId":
            case "PlannedRevealChapterId":
            case "ResolvedAtChapterId":
            case "SinceChapterId":
            case "UntilChapterId":
            {
                var chapters = (await store.GetChapters(_novelId)).OrderBy(c => c.Order).ToList();
                var names = chapters
                    .Select(c => $"{c.Order}. {c.Title}")
                    .Prepend(noneDisplay)
                    .ToArray();
                var values = chapters.Select(c => c.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "VolumeId":
            {
                var volumes = (await store.GetVolumes(_novelId)).OrderBy(v => v.Order).ToList();
                var names = volumes
                    .Select(v => $"第{v.Order}卷 {v.Name}")
                    .Prepend(noneDisplay)
                    .ToArray();
                var values = volumes.Select(v => v.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "LocationId":
            case "CurrentLocationId":
            case "ParentId":
            {
                var locs = await store.GetLocations(_novelId);
                var names = locs.Select(l => l.Name).Prepend(noneDisplay).ToArray();
                var values = locs.Select(l => l.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "OwnerId":
            case "CharacterAId":
            case "CharacterBId":
            {
                var chars = await store.GetCharacters(_novelId);
                var names = chars.Select(c => c.Name).Prepend(noneDisplay).ToArray();
                var values = chars.Select(c => c.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            case "FactionAId":
            case "FactionBId":
            {
                var factions = await store.GetFactions(_novelId);
                var names = factions.Select(f => f.Name).Prepend(noneDisplay).ToArray();
                var values = factions.Select(f => f.Id).Prepend(noneValue).ToArray();
                return (names, values);
            }
            default:
                return ([noneDisplay], [noneValue]);
        }
    }

    /// <summary>
    /// 世界规则面板的主体/后果目标候选（名称 + ID）。章节用结构索引，不加载正文。
    /// </summary>
    private async Task<
        IReadOnlyDictionary<EntityType, (string[] Names, string[] Ids)>
    > LoadWorldRuleEntityOptionsAsync()
    {
        var options = new Dictionary<EntityType, (string[] Names, string[] Ids)>();

        var characters = await store.GetCharacters(_novelId);
        options[EntityType.Character] = (
            characters.Select(c => c.Name).ToArray(),
            characters.Select(c => c.Id).ToArray()
        );

        var factions = await store.GetFactions(_novelId);
        options[EntityType.Faction] = (
            factions.Select(f => f.Name).ToArray(),
            factions.Select(f => f.Id).ToArray()
        );

        var locations = await store.GetLocations(_novelId);
        options[EntityType.Location] = (
            locations.Select(l => l.Name).ToArray(),
            locations.Select(l => l.Id).ToArray()
        );

        var items = await store.GetItems(_novelId);
        options[EntityType.Item] = (
            items.Select(i => i.Name).ToArray(),
            items.Select(i => i.Id).ToArray()
        );

        var plotlines = await store.GetPlotlines(_novelId);
        options[EntityType.Plotline] = (
            plotlines.Select(p => p.Name).ToArray(),
            plotlines.Select(p => p.Id).ToArray()
        );

        var chapters = (await store.GetStructure(_novelId))?.Chapters ?? [];
        options[EntityType.Chapter] = (
            chapters.Select(c => $"{c.Order}. {c.Title}").ToArray(),
            chapters.Select(c => c.Id).ToArray()
        );

        return options;
    }

    // ── 只读 Tag 数据加载 ──

    /// <summary>
    /// 加载只读 Tag 展示数据。返回 (tagTexts, tagIcons) 数组。
    /// </summary>
    private async Task<(string[] TagTexts, string[] TagIcons)> LoadReadOnlyTagDataAsync(string prop)
    {
        switch (prop)
        {
            case "RelatedScenes" when entityId is not null:
            {
                var scenes = await store.GetScenesByNovel(_novelId);
                var plotline = await store.GetPlotline(entityId);
                if (plotline is null)
                    return ([], []);
                var linked = scenes
                    .Where(s => s.PlotlineIds.Contains(plotline.Id))
                    .OrderBy(s => s.Order)
                    .ToArray();
                var texts = linked.Select(s => s.Title).ToArray();
                var icons = linked.Select(_ => "🎬").ToArray();
                return (texts, icons);
            }
            case "FactionRelations" when entityId is not null:
            {
                var faction = await store.GetFaction(entityId);
                if (faction is null)
                    return ([], []);
                var rels = await store.GetFactionRelationships(_novelId);
                var factions = await store.GetFactions(_novelId);
                var facNameMap = factions.ToDictionary(f => f.Id, f => f.Name);
                var related = rels.Where(fr =>
                    fr.FactionAId == faction.Id || fr.FactionBId == faction.Id
                );
                var texts = new List<string>();
                var icons = new List<string>();
                foreach (var fr in related)
                {
                    var otherId = fr.FactionAId == faction.Id ? fr.FactionBId : fr.FactionAId;
                    var otherName = facNameMap.GetValueOrDefault(otherId, otherId);
                    var (relIcon, relLabel) = fr.Type switch
                    {
                        FactionRelationType.Ally => ("🤝", "盟友"),
                        FactionRelationType.Enemy => ("💥", "敌对"),
                        FactionRelationType.Neutral => ("⚪", "中立"),
                        FactionRelationType.Trade => ("💰", "贸易"),
                        FactionRelationType.Vassal => ("🏴", "臣属"),
                        _ => ("❓", fr.Type.ToString()),
                    };
                    texts.Add($"{relLabel} {otherName}");
                    icons.Add(relIcon);
                }
                return (texts.ToArray(), icons.ToArray());
            }
            case "ChildLocations" when entityId is not null:
            {
                var locations = await store.GetLocations(_novelId);
                var children = locations
                    .Where(l => l.ParentId == entityId)
                    .Select(l => l.Name)
                    .ToArray();
                return (children, children.Select(_ => "📍").ToArray());
            }
            default:
                return ([], []);
        }
    }

    // ── MultiSelect 选项加载 ──

    /// <summary>
    /// 加载多选字段的候选选项（名称 + ID）。
    /// </summary>
    private async Task<(string[] DisplayNames, string[] OptionValues)> LoadMultiSelectOptionsAsync(
        string prop
    )
    {
        switch (prop)
        {
            case "ParticipantIds":
            {
                var chars = await store.GetCharacters(_novelId);
                return (chars.Select(c => c.Name).ToArray(), chars.Select(c => c.Id).ToArray());
            }
            case "CharacterIds":
            {
                var chars = await store.GetCharacters(_novelId);
                return (chars.Select(c => c.Name).ToArray(), chars.Select(c => c.Id).ToArray());
            }
            case "PlotlineIds":
            {
                var plotlines = await store.GetPlotlines(_novelId);
                return (
                    plotlines.Select(p => p.Name).ToArray(),
                    plotlines.Select(p => p.Id).ToArray()
                );
            }
            case "ItemIds":
            {
                var items = await store.GetItems(_novelId);
                return (items.Select(i => i.Name).ToArray(), items.Select(i => i.Id).ToArray());
            }
            case "Members":
            {
                var chars = await store.GetCharacters(_novelId);
                return (chars.Select(c => c.Name).ToArray(), chars.Select(c => c.Id).ToArray());
            }
            case "RelatedScenes":
            {
                var scenes = await store.GetScenesByNovel(_novelId);
                return (scenes.Select(s => s.Title).ToArray(), scenes.Select(s => s.Id).ToArray());
            }
            default:
                return ([], []);
        }
    }

    // ── MultiSelect 已选项加载 ──

    /// <summary>
    /// 加载多选字段已选中的 ID 列表。
    /// </summary>
    private async Task<string[]> LoadMultiSelectSelectedIdsAsync(string prop)
    {
        switch (prop)
        {
            case "ParticipantIds" when entityType == EntityType.Scene && entityId is not null:
            {
                var sc = await store.GetScene(entityId);
                return sc is null ? [] : [.. sc.ParticipantIds];
            }
            case "PlotlineIds" when entityType == EntityType.Scene && entityId is not null:
            {
                var sc = await store.GetScene(entityId);
                return sc is null ? [] : [.. sc.PlotlineIds];
            }
            case "PlotlineIds" when entityType == EntityType.Chapter && entityId is not null:
            {
                var ch = await store.GetChapter(entityId);
                return ch is null ? [] : [.. ch.PlotlineIds];
            }
            case "CharacterIds" when entityType == EntityType.Chapter && entityId is not null:
            {
                var ch = await store.GetChapter(entityId);
                return ch is null ? [] : [.. ch.CharacterIds];
            }
            case "PlotlineIds" when entityType == EntityType.Volume && entityId is not null:
            {
                var vol = await store.GetVolume(entityId);
                return vol is null ? [] : [.. vol.PlotlineIds];
            }
            case "CharacterIds" when entityType == EntityType.Volume && entityId is not null:
            {
                var vol = await store.GetVolume(entityId);
                return vol is null ? [] : [.. vol.CharacterIds];
            }
            case "ItemIds" when entityType == EntityType.Scene && entityId is not null:
            {
                var sc = await store.GetScene(entityId);
                return sc is null ? [] : [.. sc.ItemIds];
            }
            case "Members" when entityType == EntityType.Faction && entityId is not null:
            {
                var f = await store.GetFaction(entityId);
                return f is null ? [] : [.. f.Members];
            }
            case "RelatedScenes" when entityType == EntityType.Plotline && entityId is not null:
            {
                var scenes = await store.GetScenesByNovel(_novelId);
                var plotline = await store.GetPlotline(entityId);
                if (plotline is null)
                    return [];
                return scenes
                    .Where(s => s.PlotlineIds.Contains(plotline.Id))
                    .Select(s => s.Id)
                    .ToArray();
            }
            default:
                return [];
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UI 组件构建
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建只读 Tag 展示区（折叠项 + Tag 列表）。
    /// </summary>
    private static Visual BuildReadOnlyTagSection(
        string label,
        string icon,
        string[] tagTexts,
        string[] tagIcons
    )
    {
        if (tagTexts.Length == 0)
            return new Markup($"[dim]{icon} {label}: （无）[/]");

        var tags = new WrapHStack().Spacing(1);
        for (var i = 0; i < tagTexts.Length; i++)
        {
            var tagIcon = tagIcons.Length > i ? tagIcons[i] : "";
            var tagText = ToolCallRenderer.Escape(tagTexts[i]);
            tags.Add(
                new Border(new Markup($"[dim]{tagIcon}[/] {tagText}"))
                    .Style(
                        BorderStyle.Rounded with
                        {
                            BorderCellStyle = Style
                                .None.WithForeground(Color.Rgb(0x6E, 0x4A, 0x36)) // desert3
                                .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // desert2
                            BackgroundStyle = Style.None.WithBackground(
                                Color.Rgb(0x6E, 0x4A, 0x36)
                            ), // desert2
                        }
                    )
                    .Padding(new Thickness(0, 0, 0, 0))
            );
        }

        var header = new Markup($"[dim]{icon} {label}[/]");
        var collapsible = new Collapsible(header, tags) { IsExpanded = false }.Style(
            ThemeStyles.BuildCollapsible(PanelThemes.Current)
        );

        return collapsible;
    }

    /// <summary>
    /// 构建多选 Tag 编辑区：已选 Tag（带 × 移除）+ 下拉添加。
    /// </summary>
    private static VStack BuildMultiSelectField(
        string label,
        string[] optionDisplayNames,
        string[] optionValues,
        State<List<string>> selectedIdsState
    )
    {
        var idToName = new Dictionary<string, string>();
        for (var i = 0; i < optionValues.Length; i++)
            idToName[optionValues[i]] = optionDisplayNames[i];

        // Tag 展示区
        var tagPanel = new WrapHStack().Spacing(1);
        RebuildMultiSelectTags(tagPanel, selectedIdsState.Value, idToName, selectedIdsState);

        // 下拉添加区
        var availableNames = optionDisplayNames
            .Where((_, i) => !selectedIdsState.Value.Contains(optionValues[i]))
            .Prepend("➕ 添加…")
            .ToArray();
        var availableValues = optionValues
            .Where(v => !selectedIdsState.Value.Contains(v))
            .Prepend("")
            .ToArray();

        var addSelect = new Select<string>()
            .Items(availableNames)
            .SelectedIndex(0)
            .Style(SelectStyle.NoBorder);
        addSelect.HorizontalAlignment = Align.Stretch;

        if (availableValues.Length > 1)
        {
            addSelect.SelectionChanged(
                (_, e) =>
                {
                    var idx = e.NewIndex;
                    if (idx <= 0 || idx >= availableValues.Length)
                        return;
                    var addId = availableValues[idx];
                    if (!selectedIdsState.Value.Contains(addId))
                    {
                        selectedIdsState.Value.Add(addId);
                        selectedIdsState.Value = [.. selectedIdsState.Value]; // trigger change
                        RebuildMultiSelectTags(
                            tagPanel,
                            selectedIdsState.Value,
                            idToName,
                            selectedIdsState
                        );
                        // Reset select to "添加…"
                        addSelect.SelectedIndex = 0;
                    }
                }
            );
        }

        var scroll = new ScrollViewer(tagPanel, focusable: false) { MaxHeight = 8 }
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .Style(PanelThemes.ContentScrollStyle(PanelThemes.DesertContentBg));
        var layout = new VStack(new Markup($"[dim]{label}[/]"), scroll, addSelect).Spacing(1);

        return layout;
    }

    /// <summary>
    /// 重建多选 Tag 面板中的 Tag 项。
    /// </summary>
    private static void RebuildMultiSelectTags(
        WrapHStack panel,
        List<string> selectedIds,
        Dictionary<string, string> idToName,
        State<List<string>> state
    )
    {
        panel.Children.Clear();
        foreach (var id in selectedIds)
        {
            var name = idToName.GetValueOrDefault(id, id.Length > 8 ? id[..8] + "…" : id);
            var removeId = id; // capture
            var tagContent = new HStack(
                new Markup($"[dim]{name}[/]"),
                new Markup("[red] ×[/]") { IsSelectable = false }
            ).Spacing(1);

            var tagBorder = new Border(tagContent)
                .Style(
                    BorderStyle.Rounded with
                    {
                        BorderCellStyle = Style
                            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // warm copper
                            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // desert2
                        BackgroundStyle = Style.None.WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // desert2
                    }
                )
                .Padding(new Thickness(0, 0, 0, 0));

            // × 按钮点击移除
            if (tagContent.Children.ElementAtOrDefault(1) is Markup)
            {
                // Use Button for the × action
                var removeBtn = new Button(new Markup("[red]×[/]")).Style(
                    ButtonStyle.Default with
                    {
                        Padding = new Thickness(0, 0, 0, 0),
                        ShowBorder = false,
                        Normal = Style
                            .None.WithForeground(Color.Rgb(0xFB, 0x49, 0x34))
                            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)),
                    }
                );
                removeBtn.Click(() =>
                {
                    state.Value.Remove(removeId);
                    state.Value = [.. state.Value];
                    RebuildMultiSelectTags(panel, state.Value, idToName, state);
                });

                // Replace the × Markup with the Button
                var newTagContent = new HStack(new Markup($"[dim]{name}[/]"), removeBtn).Spacing(1);

                tagBorder = new Border(newTagContent)
                    .Style(
                        BorderStyle.Rounded with
                        {
                            BorderCellStyle = Style
                                .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48))
                                .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)),
                            BackgroundStyle = Style.None.WithBackground(
                                Color.Rgb(0x6E, 0x4A, 0x36)
                            ),
                        }
                    )
                    .Padding(new Thickness(0, 0, 0, 0));
            }

            panel.Add(tagBorder);
        }
    }

    /// <summary>
    /// Keep entity-editor inputs on the modal content surface instead of exposing the app-wide input fill at its edge.
    /// </summary>
    private static TextBoxStyle BuildDialogTextBoxStyle() =>
        TextBoxStyle.Default with
        {
            Background = PanelThemes.Current.DialogContentBg,
        };

    private static TextAreaStyle BuildDialogTextAreaStyle() =>
        TextAreaStyle.Default with
        {
            Background = PanelThemes.Current.DialogContentBg,
        };

    /// <summary>
    /// 构建自由标签（FreeTag）编辑区：已输入 Tag + 输入框回车添加。
    /// </summary>
    private static VStack BuildFreeTagField(string label, State<List<string>> tagListState)
    {
        var tagPanel = new WrapHStack().Spacing(1);
        RebuildFreeTags(tagPanel, tagListState.Value, tagListState);

        var inputText = new State<string?>("");
        var input = new TextBox()
            .Text(inputText)
            .Placeholder("回车添加标签")
            .Style(BuildDialogTextBoxStyle())
            .HorizontalAlignment(Align.Stretch);
        input.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Enter)
                {
                    var text = (inputText.Value ?? "").Trim();
                    if (text.Length > 0 && !tagListState.Value.Contains(text))
                    {
                        tagListState.Value.Add(text);
                        tagListState.Value = [.. tagListState.Value];
                        RebuildFreeTags(tagPanel, tagListState.Value, tagListState);
                        inputText.Value = "";
                    }
                    e.Handled = true;
                }
            }
        );

        var scroll = new ScrollViewer(tagPanel, focusable: false) { MaxHeight = 8 }
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .Style(PanelThemes.ContentScrollStyle(PanelThemes.DesertContentBg));
        var layout = new VStack(new Markup($"[dim]{label}[/]"), scroll, input).Spacing(1);

        return layout;
    }

    /// <summary>重建自由标签面板中的 Tag 项（带 × 移除按钮）。</summary>
    private static void RebuildFreeTags(
        WrapHStack panel,
        List<string> tags,
        State<List<string>> state
    )
    {
        panel.Children.Clear();
        foreach (var tag in tags)
        {
            var capturedTag = tag;
            var removeBtn = new Button(new Markup("[red]×[/]")).Style(
                ButtonStyle.Default with
                {
                    Padding = new Thickness(0, 0, 0, 0),
                    ShowBorder = false,
                    Normal = Style
                        .None.WithForeground(Color.Rgb(0xFB, 0x49, 0x34))
                        .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)),
                }
            );
            removeBtn.Click(() =>
            {
                state.Value.Remove(capturedTag);
                state.Value = [.. state.Value];
                RebuildFreeTags(panel, state.Value, state);
            });

            var tagContent = new HStack(new Markup($"[dim]{tag}[/]"), removeBtn).Spacing(1);

            var tagBorder = new Border(tagContent)
                .Style(
                    BorderStyle.Rounded with
                    {
                        BorderCellStyle = Style
                            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48))
                            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)),
                        BackgroundStyle = Style.None.WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)),
                    }
                )
                .Padding(new Thickness(0, 0, 0, 0));

            panel.Add(tagBorder);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 对话框主流程
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>显示实体编辑对话框（fire-and-forget）。</summary>
    public void Show() => _ = ShowAsync();

    /// <summary>
    /// 异步显示实体编辑对话框：加载数据 → 构建表单 → 保存。
    /// </summary>
    private async Task ShowAsync()
    {
        var tabs = GetTabDefs();
        var isEdit = entityId is not null;
        // 实体标签来自固定枚举，但标题走统一宽度安全入口（与其余三个动态标题一致）
        var title = isEdit ? $"📝 {EntityLabel()}编辑" : $"➕ {EntityLabel()}新建";
        // 作用域化章节：标题随入口变化（细纲 / 内容），不与「编辑章节」混淆
        if (isEdit && entityType == EntityType.Chapter && _editScope != ChapterEditScope.Full)
            title = _editScope == ChapterEditScope.Content ? "📖 章节内容" : "📝 章节细纲";
        var displayTitle = ChromeText.ForTitle(title, 40);
        var hasMultipleTabs = tabs.Length > 1;

        // 编辑模式：加载已有实体数据
        Dictionary<string, object?> initialValues = [];
        if (isEdit)
        {
            try
            {
                initialValues = await LoadEntityAsync();
            }
            catch (Exception ex)
            {
                ToastService.Error($"加载{EntityLabel()}失败: {ex.Message}");
                return;
            }
        }
        else if (entityType == EntityType.Chapter && !string.IsNullOrEmpty(_presetVolumeId))
        {
            // 新建章节且从某卷入口进入：表单预选目标卷（ID-ref Select 按 ID 定位索引）
            initialValues["VolumeId"] = _presetVolumeId;
        }

        // 小说详情第二个 tab：世界规则/设定（即时保存）
        WorldRulePanel? worldRulePanel = null;
        if (isEdit && entityType == EntityType.Novel && _novelId.Length > 0)
        {
            List<WorldRule> rules = [];
            try
            {
                rules = await store.GetWorldRules(_novelId);
            }
            catch (Exception ex)
            {
                ToastService.Warning($"加载世界规则失败: {ex.Message}");
            }

            IReadOnlyDictionary<EntityType, (string[] Names, string[] Ids)> entityOptions;
            try
            {
                entityOptions = await LoadWorldRuleEntityOptionsAsync();
            }
            catch (Exception ex)
            {
                ToastService.Warning($"加载世界规则可选实体失败: {ex.Message}");
                entityOptions = new Dictionary<EntityType, (string[] Names, string[] Ids)>();
            }

            worldRulePanel = new WorldRulePanel(
                store,
                _novelId,
                rules,
                entityOptions,
                NullLogger.Instance
            );
        }

        hasMultipleTabs = hasMultipleTabs || worldRulePanel is not null;

        // 加载 ID 引用字段的 Select 选项（如组织、角色、章节等）
        var selectOptionsMap = new Dictionary<string, (string[] Names, string[] Values)>();
        foreach (var tab in tabs)
        {
            foreach (var def in tab.Fields)
            {
                if (def.Type is FieldType.Select && def.OptionValues is null)
                {
                    selectOptionsMap[def.Prop] = await LoadSelectOptionsAsync(def.Prop);
                }
            }
        }

        // 加载多选字段的候选选项 + 已选 ID
        var multiSelectData =
            new Dictionary<string, (string[] Names, string[] Values, string[] SelectedIds)>();
        foreach (var tab in tabs)
        {
            foreach (var def in tab.Fields)
            {
                if (def.Type is FieldType.MultiSelect)
                {
                    var (names, values) = await LoadMultiSelectOptionsAsync(def.Prop);
                    var selectedIds = isEdit ? await LoadMultiSelectSelectedIdsAsync(def.Prop) : [];
                    multiSelectData[def.Prop] = (names, values, selectedIds);
                }
            }
        }

        // 加载只读 Tag 数据（如关联场景、组织关系等）
        var readOnlyTagData = new Dictionary<string, (string[] Texts, string[] Icons)>();
        foreach (var tab in tabs)
        {
            foreach (var def in tab.Fields)
            {
                if (def.Type is FieldType.ReadOnlyTags)
                {
                    readOnlyTagData[def.Prop] = await LoadReadOnlyTagDataAsync(def.Prop);
                }
            }
        }

        // 为每个字段创建 State 绑定
        var states =
            new List<(
                string Prop, // 属性名
                State<string?> TextState, // 文本状态
                State<int>? SelectState, // 下拉选中索引状态（null 表示非 Select）
                FieldDef Def // 字段定义
            )>();

        // 多选字段状态（独立于表单 Grid 的 State）
        var multiSelectStates = new Dictionary<string, State<List<string>>>();
        // 自由标签状态
        var freeTagStates = new Dictionary<string, State<List<string>>>();

        foreach (var tab in tabs)
        {
            foreach (var def in tab.Fields)
            {
                switch (def.Type)
                {
                    case FieldType.MultiSelect:
                    {
                        var data = multiSelectData.GetValueOrDefault(def.Prop);
                        var selectedIds = data.SelectedIds;
                        var msState = new State<List<string>>([.. selectedIds]);
                        multiSelectStates[def.Prop] = msState;
                        // Also add a text state for compatibility with save logic
                        states.Add((def.Prop, new State<string?>(""), null, def));
                        break;
                    }
                    case FieldType.FreeTag:
                    {
                        var tagList =
                            initialValues.TryGetValue(def.Prop, out var tagsVal)
                            && tagsVal is List<string> tl
                                ? new List<string>(tl)
                                : new List<string>();
                        var ftState = new State<List<string>>(tagList);
                        freeTagStates[def.Prop] = ftState;
                        states.Add((def.Prop, new State<string?>(""), null, def));
                        break;
                    }
                    case FieldType.ReadOnlyTags:
                    {
                        // No editable state needed
                        states.Add((def.Prop, new State<string?>(""), null, def));
                        break;
                    }
                    case FieldType.ReadOnly:
                    {
                        // 只读信息：值已在 initialValues 里预计算，渲染时直接取，不建可编辑状态
                        states.Add((def.Prop, new State<string?>(""), null, def));
                        break;
                    }
                    default:
                    {
                        var initialVal =
                            initialValues.GetValueOrDefault(def.Prop, "")?.ToString() ?? "";

                        State<string?>? textState;
                        State<int>? selectState = null;

                        if (def.Type == FieldType.Select)
                        {
                            // ID-ref Select: find index by ID value
                            if (selectOptionsMap.TryGetValue(def.Prop, out var opts))
                            {
                                var idx = Array.FindIndex(opts.Values, v => v == initialVal);
                                // For ID-ref fields, initialVal is the ID (from LoadEntityAsync)
                                selectState = new State<int>(idx < 0 ? 0 : idx);
                                textState = new State<string?>(initialVal);
                            }
                            else if (def.OptionValues is not null)
                            {
                                // Enum Select: find index by enum value
                                var idx = Array.FindIndex(def.OptionValues, v => v == initialVal);
                                if (idx < 0 && def.Options is not null)
                                    idx = Array.FindIndex(def.Options, o => o == initialVal);
                                selectState = new State<int>(idx < 0 ? 0 : idx);
                                textState = new State<string?>(initialVal);
                            }
                            else
                            {
                                textState = new State<string?>(initialVal);
                            }
                        }
                        else
                        {
                            textState = new State<string?>(initialVal);
                        }

                        states.Add((def.Prop, textState, selectState, def));
                        break;
                    }
                }
            }
        }

        // 构建内容区 — 多标签页用 Tab，单标签页用滚动内容
        Visual content;
        if (hasMultipleTabs)
        {
            var tabPages = new List<TabPage>();
            foreach (var tabDef in tabs)
            {
                var tabContent = BuildFormContent(
                    tabDef.Fields,
                    states,
                    selectOptionsMap,
                    multiSelectData,
                    multiSelectStates,
                    freeTagStates,
                    readOnlyTagData,
                    initialValues
                );
                tabPages.Add(new TabPage(tabDef.Name, tabContent));
            }
            if (worldRulePanel is not null)
            {
                tabPages.Add(new TabPage("世界规则", worldRulePanel.Root));
            }
            content = PanelThemes.CreateDialogTabs(tabPages.ToArray());
        }
        else if (_editScope == ChapterEditScope.Content)
        {
            // 章节内容：整块都是正文编辑器——不要「内容」标签行（那是键值对形式，
            // 用户报障：内容应该直接全部铺满），也不要 3~8 行的自动高度，
            // 让编辑器撑满对话框内容区。
            var contentState = states.First(state => state.Prop == "Content");
            var editor = new TextArea()
                .Text(contentState.TextState)
                .AutoSizeMode(TextEditorAutoSizeMode.None)
                .MinHeight(1)
                .MaxHeight(int.MaxValue)
                .AcceptTab(false)
                .Scrollable()
                .Style(BuildDialogTextAreaStyle())
                .HorizontalAlignment(Align.Stretch);
            editor.VerticalAlignment = Align.Stretch;
            content = PanelThemes.CreateScrollableDialogContent(editor);
        }
        else
        {
            var form = BuildFormContent(
                tabs[0].Fields,
                states,
                selectOptionsMap,
                multiSelectData,
                multiSelectStates,
                freeTagStates,
                readOnlyTagData,
                initialValues
            );
            content = PanelThemes.CreateScrollableDialogContent(form);
        }

        var dlg = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title(displayTitle)
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(76)
            .MinHeight(isEdit && entityType == EntityType.Novel ? 20 : 14)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(content);

        var closeBtn = PanelThemes.CreateDialogCloseButton(dlg.Close);

        var saveBtn = new Button(new Markup("[bold]保存[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() => SaveEntity(dlg));

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dlg.Close);

        dlg.TopRightText(closeBtn);
        dlg.BottomRightText(new HStack(cancelBtn, saveBtn).Spacing(2));

        dlg.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dlg.Close();
                    e.Handled = true;
                }
            }
        );
        dlg.Show();
        return;

        // UI event handler (button click): 异常处理收敛到 TuiSafeFire（Toast），
        // 核心逻辑内新增 await 无需扩 try。dialog.Close() 只在保存成功后调用。
        void SaveEntity(Dialog dialog)
        {
            if (string.IsNullOrEmpty(_novelId))
            {
                ToastService.Warning("请先选择小说项目");
                dialog.Close();
                return;
            }
            TuiSafeFire.SafeFire(
                async () =>
                {
                    var fieldValues = new Dictionary<string, object?>();

                    // Standard fields
                    foreach (var s in states)
                    {
                        if (
                            s.Def.Type
                            is FieldType.MultiSelect
                                or FieldType.ReadOnlyTags
                                or FieldType.ReadOnly
                        )
                            continue; // handled separately / 只读不参与保存

                        var value = s.SelectState is not null
                            ? ResolveSelectValue(s, selectOptionsMap)
                            : s.TextState.Value ?? "";

                        // FreeTag: join tag list as comma-separated string
                        if (
                            s.Def.Type is FieldType.FreeTag
                            && freeTagStates.TryGetValue(s.Prop, out var ftState)
                        )
                        {
                            value = ftState.Value.Count > 0 ? string.Join(", ", ftState.Value) : "";
                        }

                        fieldValues[s.Prop] = value;
                    }

                    // MultiSelect fields: store List<string> directly
                    foreach (var (prop, msState) in multiSelectStates)
                    {
                        fieldValues[prop] = msState.Value;
                    }

                    await SaveEntityAsync(fieldValues);
                    ToastService.Success(
                        isEdit ? $"{EntityLabel()}已保存" : $"{EntityLabel()}已创建"
                    );
                    dialog.Close();
                },
                "save entity",
                errorMessage: "保存失败"
            );
        }
    }

    /// <summary>
    /// 解析 Select 字段的当前值（支持 ID-ref Select 和 enum Select）。
    /// </summary>
    private static string ResolveSelectValue(
        (string Prop, State<string?> TextState, State<int>? SelectState, FieldDef Def) s,
        Dictionary<string, (string[] Names, string[] Values)> selectOptionsMap
    )
    {
        if (s.SelectState is null || s.Def.Type != FieldType.Select)
            return s.TextState.Value ?? "";

        // ID-ref Select: return the option value (which is the ID)
        if (selectOptionsMap.TryGetValue(s.Prop, out var opts))
        {
            var idx = Math.Clamp(s.SelectState.Value, 0, opts.Values.Length - 1);
            return opts.Values[idx];
        }

        // Enum Select: return the OptionValue
        if (s.Def.OptionValues is not null)
        {
            var idx = Math.Clamp(s.SelectState.Value, 0, s.Def.OptionValues.Length - 1);
            return s.Def.OptionValues[idx];
        }

        return s.TextState.Value ?? "";
    }

    /// <summary>
    /// 构建表单内容（增强版：支持所有字段类型）。
    /// </summary>
    private VStack BuildFormContent(
        FieldDef[] fields,
        List<(string Prop, State<string?> TextState, State<int>? SelectState, FieldDef Def)> states,
        Dictionary<string, (string[] Names, string[] Values)> selectOptionsMap,
        Dictionary<string, (string[] Names, string[] Values, string[] SelectedIds)> multiSelectData,
        Dictionary<string, State<List<string>>> multiSelectStates,
        Dictionary<string, State<List<string>>> freeTagStates,
        Dictionary<string, (string[] Texts, string[] Icons)> readOnlyTagData,
        Dictionary<string, object?> initialValues
    )
    {
        var rows = new List<Visual>();

        foreach (var def in fields)
        {
            var def1 = def;
            var stateIdx = states.FindIndex(s => s.Prop == def1.Prop);
            if (stateIdx < 0)
                continue;

            switch (def.Type)
            {
                case FieldType.ReadOnlyTags:
                {
                    var tagData = readOnlyTagData.GetValueOrDefault(def.Prop);
                    var section = BuildReadOnlyTagSection(
                        def.Label,
                        def.Icon ?? "📋",
                        tagData.Texts,
                        tagData.Icons
                    );
                    rows.Add(section);
                    break;
                }
                case FieldType.MultiSelect:
                {
                    var data = multiSelectData.GetValueOrDefault(def.Prop);
                    var msState = multiSelectStates.GetValueOrDefault(def.Prop);
                    if (msState is null)
                    {
                        rows.Add(new Markup($"[dim]{def.Label}: （无选项）[/]"));
                        break;
                    }
                    var field = BuildMultiSelectField(def.Label, data.Names, data.Values, msState);
                    rows.Add(field);
                    break;
                }
                case FieldType.FreeTag:
                {
                    var ftState = freeTagStates.GetValueOrDefault(def.Prop);
                    if (ftState is null)
                    {
                        // Fallback to TextBox
                        goto default;
                    }
                    var field = BuildFreeTagField(def.Label, ftState);
                    // Wire up the freeTagStates for save
                    rows.Add(field);
                    break;
                }
                default:
                {
                    // Standard Grid row: label + control
                    var row = new Grid()
                        .Columns(
                            new ColumnDefinition { Width = GridLength.Auto },
                            new ColumnDefinition { Width = GridLength.Star() }
                        )
                        .ColumnGap(2)
                        .RowGap(0)
                        .HorizontalAlignment(Align.Stretch);
                    row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    row.Cell(new Markup($"[dim]{def.Label}[/]"), 0, 0);

                    switch (def.Type)
                    {
                        case FieldType.Select:
                        {
                            if (selectOptionsMap.TryGetValue(def.Prop, out var opts))
                            {
                                // ID-ref Select
                                var sel = new MythSelect()
                                    .Items(opts.Names)
                                    .SelectedIndex(states[stateIdx].SelectState!)
                                    .Style(SelectStyle.NoBorder)
                                    .MaxWidth(int.MaxValue);
                                sel.HorizontalAlignment = Align.Stretch;
                                row.Cell(sel, 0, 1);
                            }
                            else if (def.Options is not null)
                            {
                                // Enum Select
                                var sel = new MythSelect()
                                    .Items(def.Options)
                                    .SelectedIndex(states[stateIdx].SelectState!)
                                    .Style(SelectStyle.NoBorder)
                                    .MaxWidth(int.MaxValue);
                                sel.HorizontalAlignment = Align.Stretch;
                                row.Cell(sel, 0, 1);
                            }
                            break;
                        }
                        case FieldType.ReadOnly:
                        {
                            // 只读信息（章节号/字数/更新时间）：值由 LoadEntityAsync 预计算，
                            // 这里只展示，不建可编辑控件（也就不会有「改了但存不回去」的错觉）。
                            var roValue = initialValues.TryGetValue(def.Prop, out var ro)
                                ? ro?.ToString() ?? ""
                                : "";
                            row.Cell(
                                new Markup($"[dim]{ToolCallRenderer.Escape(roValue)}[/]")
                                {
                                    IsSelectable = false,
                                },
                                0,
                                1
                            );
                            break;
                        }
                        case FieldType.TextArea:
                        {
                            var ta = new TextArea()
                                .Text(states[stateIdx].TextState)
                                .Placeholder(def.Placeholder ?? $"输入{def.Label}…")
                                .AutoSizeMode(TextEditorAutoSizeMode.Height)
                                .MinHeight(3)
                                .MaxHeight(8)
                                .AcceptTab(false)
                                .Scrollable()
                                .Style(BuildDialogTextAreaStyle())
                                .HorizontalAlignment(Align.Stretch);
                            row.Cell(ta, 0, 1);
                            break;
                        }
                        default: // TextBox
                        {
                            var tb = new TextBox()
                                .Text(states[stateIdx].TextState)
                                .Placeholder(def.Placeholder ?? $"输入{def.Label}")
                                .Style(BuildDialogTextBoxStyle())
                                .HorizontalAlignment(Align.Stretch);
                            row.Cell(tb, 0, 1);
                            break;
                        }
                    }
                    rows.Add(row);
                    break;
                }
            }
        }

        return new VStack(rows.ToArray()).Spacing(1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 章节的卷内序号（1 起；同卷内按 <see cref="Chapter.Order"/> 升序数，换卷从 1 重新计数；
    /// 未分卷的章节算作同一组）。与全书全局 <see cref="Chapter.Order"/> 区分开——
    /// 展示用卷内号，落库/排序仍用全局 Order。
    /// </summary>
    private async Task<string> ChapterNumberOfAsync(Chapter ch)
    {
        var siblings = (await store.GetChapters(ch.NovelId))
            .Where(c =>
                string.Equals(c.VolumeId ?? "", ch.VolumeId ?? "", StringComparison.Ordinal)
            )
            .OrderBy(c => c.Order)
            .ToList();
        var index = siblings.FindIndex(c => string.Equals(c.Id, ch.Id, StringComparison.Ordinal));
        return index < 0 ? "—" : $"第 {index + 1} 章";
    }

    /// <summary>根据实体类型获取标签页定义。</summary>
    private TabDef[] GetTabDefs() =>
        entityType switch
        {
            EntityType.Character => CharacterTabs,
            EntityType.Faction => FactionTabs,
            EntityType.Location => LocationTabs,
            EntityType.Plotline => PlotlineTabs,
            EntityType.Item => ItemTabs,
            EntityType.Relationship => RelationshipTabs,
            EntityType.Scene => SceneTabs,
            // 作用域化章节（编辑模式才可能非 Full；新建走快速对话框，恒 Full）
            EntityType.Chapter when entityId is not null && _editScope != ChapterEditScope.Full =>
                _editScope == ChapterEditScope.Content
                    ? [.. ChapterTabs.Where(t => t.Name == "正文")]
                    : [.. ChapterTabs.Where(t => t.Name != "正文")],
            EntityType.Chapter => ChapterTabs,
            EntityType.Volume => VolumeTabs,
            EntityType.Novel => NovelTabs,
            EntityType.TimelineEvent => TimelineEventTabs,
            EntityType.FactionRelationship => FactionRelationshipTabs,
            EntityType.Note => NoteTabs,
            _ => throw new InvalidOperationException($"未支持的实体类型: {entityType}"),
        };

    /// <summary>根据实体类型获取中文标签名。</summary>
    private string EntityLabel() =>
        entityType switch
        {
            EntityType.Character => "角色",
            EntityType.Faction => "组织",
            EntityType.Location => "地点",
            EntityType.Plotline => "情节线",
            EntityType.Item => "道具",
            EntityType.Relationship => "关系",
            EntityType.Scene => "场景",
            EntityType.Chapter => "章节",
            EntityType.Volume => "分卷",
            EntityType.Novel => "小说",
            EntityType.TimelineEvent => "时间线事件",
            EntityType.FactionRelationship => "组织关系",
            EntityType.Note => "笔记",
            _ => EntityDisplayHelper.DisplayName(entityType),
        };

    // ═══════════════════════════════════════════════════════════════════════════
    // 加载实体数据
    // ═══════════════════════════════════════════════════════════════════════════

    // entityId! 非空证明：LoadEntityAsync 仅在编辑模式（isEdit=true）下调用，
    // 而 isEdit 由 WireEdit 设置，WireEdit 保证 id != null 才打开编辑对话框。
    private async Task<Dictionary<string, object?>> LoadEntityAsync() =>
        entityType switch
        {
            EntityType.Character when await store.GetCharacter(entityId!) is { } c =>
                new Dictionary<string, object?>
                {
                    ["Name"] = c.Name,
                    ["Gender"] = c.Gender,
                    ["Age"] = c.Age,
                    ["Role"] = c.Role.ToString(),
                    ["FactionId"] = c.FactionId ?? "", // ID directly, Select resolves it
                    ["CurrentLocationId"] = c.CurrentLocationId ?? "", // ID directly
                    ["Personality"] = c.Personality,
                    ["Appearance"] = c.Appearance,
                    ["Habits"] = c.Habits,
                    ["Background"] = c.Background,
                    ["Motivation"] = c.Motivation,
                    ["Weakness"] = c.Weakness,
                    ["Tags"] = c.Tags,
                    ["Abilities"] = c.Abilities,
                    ["Notes"] = c.Notes,
                },

            EntityType.Faction when await store.GetFaction(entityId!) is { } f => new Dictionary<
                string,
                object?
            >
            {
                ["Name"] = f.Name,
                ["Type"] = f.Type.ToString(),
                ["Status"] = f.Status.ToString(),
                ["Description"] = f.Description,
                ["Leader"] = f.Leader, // ID directly
                ["HeadquartersId"] = f.HeadquartersId ?? "", // ID directly
                ["Goal"] = f.Goal,
                ["Members"] = f.Members, // List<string>, handled by MultiSelect
                ["FactionRelations"] = "", // computed, handled by ReadOnlyTags
            },

            EntityType.Location when await store.GetLocation(entityId!) is { } l => new Dictionary<
                string,
                object?
            >
            {
                ["Name"] = l.Name,
                ["Type"] = l.Type.ToString(),
                ["Status"] = l.Status.ToString(),
                ["Atmosphere"] = l.Atmosphere,
                ["Description"] = l.Description,
                ["ParentId"] = l.ParentId ?? "", // ID directly
                ["ChildLocations"] = "", // computed, handled by ReadOnlyTags
            },

            EntityType.Plotline when await store.GetPlotline(entityId!) is { } p => new Dictionary<
                string,
                object?
            >
            {
                ["Name"] = p.Name,
                ["Description"] = p.Description,
                ["Type"] = p.Type.ToString(),
                ["Status"] = p.Status.ToString(),
                ["Tension"] = p.Tension.ToString(),
                ["PlannedRevealChapterId"] = p.PlannedRevealChapterId ?? "",
                ["ResolvedAtChapterId"] = p.ResolvedAtChapterId ?? "",
                ["Turnpoints"] = p.Turnpoints,
                ["RelatedScenes"] = "", // computed, handled by ReadOnlyTags
            },

            EntityType.Item when await store.GetItem(entityId!) is { } it => new Dictionary<
                string,
                object?
            >
            {
                ["Name"] = it.Name,
                ["Type"] = it.Type.ToString(),
                ["Status"] = it.Status.ToString(),
                ["Description"] = it.Description,
                ["OwnerId"] = it.OwnerId ?? "", // ID directly
                ["LocationId"] = it.LocationId ?? "", // ID directly
            },

            EntityType.Relationship when await store.GetRelationship(entityId!) is { } r =>
                new Dictionary<string, object?>
                {
                    ["CharacterAId"] = r.CharacterAId, // ID directly
                    ["CharacterBId"] = r.CharacterBId, // ID directly
                    ["Type"] = r.Type.ToString(),
                    ["Strength"] = r.Strength.ToString(),
                    ["Description"] = r.Description,
                },

            EntityType.Scene when await store.GetScene(entityId!) is { } sc => new Dictionary<
                string,
                object?
            >
            {
                ["Title"] = sc.Title,
                ["ChapterId"] = sc.ChapterId ?? "", // ID directly, nullable
                ["LocationId"] = sc.LocationId ?? "", // ID directly
                ["TimeDescription"] = sc.TimeDescription,
                ["SceneType"] = sc.SceneType.ToString(),
                ["ParticipantIds"] = sc.ParticipantIds, // List<string>, handled by MultiSelect
                ["PlotlineIds"] = sc.PlotlineIds, // List<string>, handled by MultiSelect
                ["Conflict"] = sc.Conflict,
                ["Goal"] = sc.Goal,
                ["EmotionalArc"] = sc.EmotionalArc,
                ["Notes"] = sc.Notes,
            },

            EntityType.Chapter when await store.GetChapter(entityId!) is { } ch => new Dictionary<
                string,
                object?
            >
            {
                ["Title"] = ch.Title,
                // 卷内序号：同卷（未分卷算一组）内按 Order 升序的位置，1 起；换卷从 1 重新计数
                ["ChapterNumber"] = await ChapterNumberOfAsync(ch),
                ["CharCount"] = OutlineRowText.FormatWords(ch.CharCount) + " 字",
                ["UpdatedAt"] = ch.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["Order"] = ch.Order.ToString(),
                ["Status"] = ch.Status.ToString(),
                ["VolumeId"] = ch.VolumeId ?? "",
                ["Summary"] = ch.Summary,
                ["PlotlineIds"] = ch.PlotlineIds, // List<string>, handled by MultiSelect
                ["CharacterIds"] = ch.CharacterIds, // List<string>, handled by MultiSelect
                ["Notes"] = ch.Notes,
                ["Content"] = ch.Content,
            },

            EntityType.Volume when await store.GetVolume(entityId!) is { } vol => new Dictionary<
                string,
                object?
            >
            {
                ["Name"] = vol.Name,
                ["Order"] = vol.Order.ToString(),
                ["Summary"] = vol.Summary,
                ["Description"] = vol.Description,
                ["Goal"] = vol.Goal,
                ["PlotlineIds"] = vol.PlotlineIds, // List<string>, handled by MultiSelect
                ["CharacterIds"] = vol.CharacterIds, // List<string>, handled by MultiSelect
                ["Notes"] = vol.Notes,
            },

            EntityType.Novel when await store.GetNovel(entityId!) is { } nv => new Dictionary<
                string,
                object?
            >
            {
                ["Title"] = nv.Title,
                ["Genre"] = nv.Genre,
                ["Description"] = nv.Description,
                ["Outline"] = nv.Outline,
                ["WritingStyle"] = nv.WritingStyle,
                ["CompletionStatus"] = nv.CompletionStatus,
            },

            EntityType.TimelineEvent
                when await store.GetTimelineEvents(_novelId) is { } evts
                    && evts.FirstOrDefault(e => e.Id == entityId!) is { } te => new Dictionary<
                string,
                object?
            >
            {
                ["Title"] = te.Title,
                ["Description"] = te.Description,
                ["ChapterId"] = te.ChapterId ?? "",
            },

            EntityType.FactionRelationship
                when await store.GetFactionRelationships(_novelId) is { } frs
                    && frs.FirstOrDefault(fr => fr.Id == entityId!) is { } frel => new Dictionary<
                string,
                object?
            >
            {
                ["FactionAId"] = frel.FactionAId, // ID directly
                ["FactionBId"] = frel.FactionBId, // ID directly
                ["Type"] = frel.Type.ToString(),
                ["Description"] = frel.Description,
            },

            EntityType.Note
                when await store.GetNotes(_novelId) is { } notes
                    && notes.FirstOrDefault(n => n.Id == entityId!) is { } note => new Dictionary<
                string,
                object?
            >
            {
                ["Title"] = note.Title,
                ["Body"] = note.Body,
                ["Tags"] = note.Tags, // List<string>, handled by FreeTag
                ["Status"] = note.Status.ToString(),
                ["CapturedChapterOrder"] = note.CapturedChapterOrder is { } order
                    ? $"第{order}章"
                    : "—",
            },

            _ => new Dictionary<string, object?>(),
        };

    // ═══════════════════════════════════════════════════════════════════════════
    // 保存实体数据
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>保存实体数据（FileNovelStore 需手动开启写入事务）。</summary>
    private async Task SaveEntityAsync(Dictionary<string, object?> values)
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        if (store is FileNovelStore fns1)
        {
            using (fns1.BeginManualWrite())
                await SaveEntityCoreAsync(values);
        }
        else
        {
            await SaveEntityCoreAsync(values);
        }
    }

    /// <summary>
    /// 章节状态切换的唯一合法路径（直接改 Status 落盘会被 FileNovelStore 拒绝）：
    /// 草稿 → 已发布 = 发布事务（预览 → PublishAsync）；已发布 → 草稿 = 撤回（预览 → UnpublishAsync）。
    /// 用户在对话框里取消时保持原状态不变（字段修改已保存）。
    /// </summary>
    private async Task ApplyChapterStatusTransitionAsync(
        Chapter chapter,
        ChapterStatus from,
        ChapterStatus to
    )
    {
        var novelId = chapter.NovelId;
        if (string.IsNullOrEmpty(novelId))
            return;
        var service = new ChapterLifecycleService(store);
        var title = $"第{chapter.Order}章「{chapter.Title}」";

        if (from == ChapterStatus.Draft && to == ChapterStatus.Published)
        {
            var preview = await service.PreviewPublishAsync(novelId, chapter.Id);
            var options = await ChapterPublishDialog.ShowAsync(preview);
            if (options is null)
            {
                ToastService.Warning($"{title}状态保持草稿（已取消发布）");
                return;
            }
            var result = await service.PublishAsync(novelId, chapter.Id, options);
            if (!result.Success)
            {
                ToastService.Error($"发布失败: {result.Error}");
                return;
            }
            ToastService.Info($"已发布 {title}");
            return;
        }

        if (from == ChapterStatus.Published && to == ChapterStatus.Draft)
        {
            var preview = await service.PreviewUnpublishAsync(novelId, chapter.Id);
            if (!await ChapterUnpublishDialog.ShowAsync(title, preview))
            {
                ToastService.Warning($"{title}状态保持已发布（已取消撤回）");
                return;
            }
            var result = await service.UnpublishAsync(novelId, chapter.Id);
            if (!result.Success)
            {
                ToastService.Error($"撤回失败: {result.Error}");
                return;
            }
            ToastService.Info($"已撤回 {title}（回滚 {result.RevertedEntries} 条）");
        }
    }

    /// <summary>核心保存逻辑：根据实体类型分发到对应的保存方法。</summary>
    private async Task SaveEntityCoreAsync(Dictionary<string, object?> values)
    {
        switch (entityType)
        {
            case EntityType.Character:
            {
                var ch = entityId is not null
                    ? await store.GetCharacter(entityId)
                    : new Character { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (ch is null)
                    return;
                ch.Name = Val(values, "Name");
                ch.Gender = Val(values, "Gender");
                ch.Age = Val(values, "Age");
                ch.Role = Enum.TryParse<CharacterRole>(
                    Val(values, "Role"),
                    ignoreCase: true,
                    out var cr
                )
                    ? cr
                    : CharacterRole.Supporting;
                // FactionId: Select returns ID directly
                var factionId = Val(values, "FactionId");
                ch.FactionId = string.IsNullOrEmpty(factionId) ? null : factionId;
                var currentLocationId = Val(values, "CurrentLocationId");
                ch.CurrentLocationId = string.IsNullOrEmpty(currentLocationId)
                    ? null
                    : currentLocationId;
                ch.Personality = Val(values, "Personality");
                ch.Appearance = Val(values, "Appearance");
                ch.Habits = Val(values, "Habits");
                ch.Background = Val(values, "Background");
                ch.Motivation = Val(values, "Motivation");
                ch.Weakness = Val(values, "Weakness");
                ch.Tags = ParseTags(Val(values, "Tags"));
                ch.Abilities = Val(values, "Abilities");
                ch.Notes = Val(values, "Notes");
                await store.SaveCharacter(ch);
                break;
            }

            case EntityType.Faction:
            {
                var fa = entityId is not null
                    ? await store.GetFaction(entityId)
                    : new Faction { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (fa is null)
                    return;
                fa.Name = Val(values, "Name");
                fa.Type = Enum.TryParse<FactionType>(
                    Val(values, "Type"),
                    ignoreCase: true,
                    out var ft
                )
                    ? ft
                    : FactionType.Sect;
                fa.Status = Enum.TryParse<FactionStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var fs
                )
                    ? fs
                    : fa.Status;
                fa.Description = Val(values, "Description");
                // Leader: Select returns ID directly
                fa.Leader = Val(values, "Leader");
                // HeadquartersId: Select returns ID directly
                var hqId = Val(values, "HeadquartersId");
                fa.HeadquartersId = string.IsNullOrEmpty(hqId) ? null : hqId;
                fa.Goal = Val(values, "Goal");
                // Members: MultiSelect returns List<string> directly
                fa.Members = ValList(values, "Members");
                await store.SaveFaction(fa);
                break;
            }

            case EntityType.Location:
            {
                var lo = entityId is not null
                    ? await store.GetLocation(entityId)
                    : new Location { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (lo is null)
                    return;
                lo.Name = Val(values, "Name");
                lo.Type = Enum.TryParse<LocationType>(
                    Val(values, "Type"),
                    ignoreCase: true,
                    out var lt
                )
                    ? lt
                    : LocationType.City;
                lo.Status = Enum.TryParse<LocationStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var ls
                )
                    ? ls
                    : lo.Status;
                lo.Atmosphere = Val(values, "Atmosphere");
                lo.Description = Val(values, "Description");
                // ParentId: Select returns ID directly
                var parentId = Val(values, "ParentId");
                lo.ParentId = string.IsNullOrEmpty(parentId) ? null : parentId;
                await store.SaveLocation(lo);
                break;
            }

            case EntityType.Plotline:
            {
                var pl = entityId is not null
                    ? await store.GetPlotline(entityId)
                    : new Plotline { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (pl is null)
                    return;
                pl.Name = Val(values, "Name");
                pl.Description = Val(values, "Description");
                pl.Status = Enum.TryParse<PlotlineStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var pst
                )
                    ? pst
                    : pl.Status;
                pl.Type = Enum.TryParse<PlotlineType>(
                    Val(values, "Type"),
                    ignoreCase: true,
                    out var t
                )
                    ? t
                    : PlotlineType.Main;
                pl.Tension = Enum.TryParse<PlotlineTension>(
                    Val(values, "Tension"),
                    ignoreCase: true,
                    out var tn
                )
                    ? tn
                    : PlotlineTension.Medium;
                pl.PlannedRevealChapterId = string.IsNullOrWhiteSpace(
                    Val(values, "PlannedRevealChapterId")
                )
                    ? null
                    : Val(values, "PlannedRevealChapterId");
                pl.ResolvedAtChapterId = string.IsNullOrWhiteSpace(
                    Val(values, "ResolvedAtChapterId")
                )
                    ? null
                    : Val(values, "ResolvedAtChapterId");
                pl.Turnpoints = Val(values, "Turnpoints");
                await store.SavePlotline(pl);

                // 同步场景的 PlotlineIds：增删与此情节线关联的场景
                var oldLinkedSceneIds = entityId is not null
                    ? (await store.GetScenesByNovel(_novelId))
                        .Where(s => s.PlotlineIds.Contains(pl.Id))
                        .Select(s => s.Id)
                        .ToHashSet()
                    : [];
                var newLinkedSceneIds = ValList(values, "RelatedScenes").ToHashSet();
                var toAdd = newLinkedSceneIds.Except(oldLinkedSceneIds).ToArray();
                var toRemove = oldLinkedSceneIds.Except(newLinkedSceneIds).ToArray();

                if (toAdd.Length > 0 || toRemove.Length > 0)
                {
                    foreach (var sceneId in toAdd.Concat(toRemove))
                    {
                        var scene = await store.GetScene(sceneId);
                        if (scene is null)
                            continue;
                        var linked = scene.PlotlineIds.ToHashSet();
                        if (toAdd.Contains(sceneId))
                            linked.Add(pl.Id);
                        else
                            linked.Remove(pl.Id);
                        scene.PlotlineIds = [.. linked];
                        await store.SaveScene(scene);
                    }
                }
                break;
            }

            case EntityType.Item:
            {
                var it = entityId is not null
                    ? await store.GetItem(entityId)
                    : new Item { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (it is null)
                    return;
                it.Name = Val(values, "Name");
                it.Type = Enum.TryParse<ItemType>(
                    Val(values, "Type"),
                    ignoreCase: true,
                    out var itType
                )
                    ? itType
                    : ItemType.Artifact;
                it.Status = Enum.TryParse<ItemStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var ist
                )
                    ? ist
                    : it.Status;
                it.Description = Val(values, "Description");
                // OwnerId: Select returns ID directly
                var ownerId = Val(values, "OwnerId");
                it.OwnerId = string.IsNullOrEmpty(ownerId) ? null : ownerId;
                var itemLocationId = Val(values, "LocationId");
                it.LocationId = string.IsNullOrEmpty(itemLocationId) ? null : itemLocationId;
                await store.SaveItem(it);
                break;
            }

            case EntityType.Relationship:
            {
                var rel = entityId is not null
                    ? await store.GetRelationship(entityId)
                    : new Relationship
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        NovelId = _novelId,
                    };
                if (rel is null)
                    return;
                // CharacterAId/BId: Select returns ID directly
                rel.CharacterAId = Val(values, "CharacterAId");
                rel.CharacterBId = Val(values, "CharacterBId");
                rel.Type =
                    RelationshipTypeExtensions.ParseRelationshipType(Val(values, "Type"))
                    ?? RelationshipType.Friend;
                rel.Strength = Enum.TryParse<RelationshipStrength>(
                    Val(values, "Strength"),
                    ignoreCase: true,
                    out var rs
                )
                    ? rs
                    : RelationshipStrength.Medium;
                rel.Description = Val(values, "Description");
                rel.SinceChapterId = string.IsNullOrEmpty(Val(values, "SinceChapterId"))
                    ? null
                    : Val(values, "SinceChapterId");
                rel.UntilChapterId = string.IsNullOrEmpty(Val(values, "UntilChapterId"))
                    ? null
                    : Val(values, "UntilChapterId");
                await store.SaveRelationship(rel);
                break;
            }

            case EntityType.Scene:
            {
                var sc = entityId is not null
                    ? await store.GetScene(entityId)
                    : new SceneDefinition
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        NovelId = _novelId,
                    };
                if (sc is null)
                    return;
                sc.Title = Val(values, "Title");
                // ChapterId: Select returns ID directly (nullable — empty = no chapter)
                var chapterIdVal = Val(values, "ChapterId");
                sc.ChapterId = string.IsNullOrEmpty(chapterIdVal) ? null : chapterIdVal;
                // Order: removed from UI, keep original value on edit, auto-assign on create
                if (entityId is null)
                {
                    var existingScenes = await store.GetScenesByNovel(_novelId);
                    sc.Order = existingScenes.Count + 1;
                }
                // LocationId: Select returns ID directly
                var locId = Val(values, "LocationId");
                sc.LocationId = string.IsNullOrEmpty(locId) ? null : locId;
                sc.TimeDescription = Val(values, "TimeDescription");
                sc.SceneType = Enum.TryParse<SceneType>(
                    Val(values, "SceneType"),
                    ignoreCase: true,
                    out var st
                )
                    ? st
                    : SceneType.Dialogue;
                // ParticipantIds/PlotlineIds: MultiSelect returns List<string> directly
                sc.ParticipantIds = ValList(values, "ParticipantIds");
                sc.PlotlineIds = ValList(values, "PlotlineIds");
                sc.ItemIds = ValList(values, "ItemIds");
                sc.Conflict = Val(values, "Conflict");
                sc.Goal = Val(values, "Goal");
                sc.EmotionalArc = Val(values, "EmotionalArc");
                sc.Notes = Val(values, "Notes");
                await store.SaveScene(sc);
                break;
            }

            case EntityType.Chapter:
            {
                var cha = entityId is not null
                    ? await store.GetChapter(entityId)
                    : new Chapter { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (cha is null)
                    return;
                // 作用域表单（细纲 / 内容）：只回写本窗出现的字段，缺省字段保持原值——
                // 否则「内容」窗保存会把标题/卷/序号等未展示字段清空。Full 作用域下两者等价。
                var scoped = entityId is not null && _editScope != ChapterEditScope.Full;
                if (!scoped || values.ContainsKey("Title"))
                    cha.Title = Val(values, "Title");
                _ = int.TryParse(Val(values, "Order", "0"), out var chOrder);
                var volumeId = Val(values, "VolumeId");
                // 新建且未填序号 → 插入到所选卷末尾（卷尾号已被别卷占用时先全章重排）。
                if (entityId is null && chOrder <= 0)
                {
                    var all = await store.GetChapters(_novelId);
                    var plan = ChapterOrdering.PlanInsert(
                        all,
                        afterChapterId: null,
                        targetVolumeId: string.IsNullOrEmpty(volumeId) ? null : volumeId
                    );
                    chOrder = plan.Order;
                    if (plan.RequiresReorder)
                    {
                        var assignments = ChapterOrdering.PlanFullReorder(
                            all,
                            insertAfterChapterId: null,
                            targetGroupKey: ChapterOrdering.GroupKey(volumeId)
                        );
                        chOrder = assignments
                            .First(a => a.ChapterId == ChapterOrdering.InsertSlotId)
                            .Order;
                        await store.ReorderAllChapters(_novelId, assignments);
                    }
                }
                if (!scoped || values.ContainsKey("Order"))
                    cha.Order = chOrder;
                // 状态是发布事务的结果：本对话框只落盘其它字段（保留旧状态），
                // 用户改了状态时在保存之后走 ChapterLifecycleService 的发布/撤回流程。
                var oldStatus = cha.Status;
                var requestedStatus = Enum.TryParse<ChapterStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var cs
                )
                    ? cs
                    : oldStatus;
                cha.Status = oldStatus;
                if (!scoped || values.ContainsKey("VolumeId"))
                    cha.VolumeId = string.IsNullOrEmpty(volumeId) ? null : volumeId;
                if (!scoped || values.ContainsKey("Summary"))
                    cha.Summary = Val(values, "Summary");
                if (!scoped || values.ContainsKey("PlotlineIds"))
                    cha.PlotlineIds = ValList(values, "PlotlineIds");
                if (!scoped || values.ContainsKey("CharacterIds"))
                    cha.CharacterIds = ValList(values, "CharacterIds");
                if (!scoped || values.ContainsKey("Notes"))
                    cha.Notes = Val(values, "Notes");
                if (!scoped || values.ContainsKey("Content"))
                    cha.Content = Val(values, "Content");
                await store.SaveChapter(cha);
                if (requestedStatus != oldStatus)
                    await ApplyChapterStatusTransitionAsync(cha, oldStatus, requestedStatus);
                break;
            }

            case EntityType.Volume:
            {
                var vol = entityId is not null
                    ? await store.GetVolume(entityId)
                    : new Volume { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (vol is null)
                    return;
                vol.Name = Val(values, "Name");
                _ = int.TryParse(Val(values, "Order", "0"), out var volOrder);
                if (entityId is null && volOrder <= 0) // 新建且未填卷序 → 追加到末尾
                    volOrder = ChapterOrdering.NextVolumeOrder(await store.GetVolumes(_novelId));
                vol.Order = volOrder;
                vol.Summary = Val(values, "Summary");
                if (values.ContainsKey("Description"))
                    vol.Description = Val(values, "Description");
                vol.Goal = Val(values, "Goal");
                vol.PlotlineIds = ValList(values, "PlotlineIds");
                vol.CharacterIds = ValList(values, "CharacterIds");
                vol.Notes = Val(values, "Notes");
                await store.SaveVolume(vol);
                break;
            }

            case EntityType.Novel:
            {
                var nv = entityId is not null
                    ? await store.GetNovel(entityId)
                    : new Novel { Id = Guid.NewGuid().ToString("N")[..12], NovelId = _novelId };
                if (nv is null)
                    return;
                nv.Title = Val(values, "Title");
                nv.Genre = Val(values, "Genre");
                nv.Description = Val(values, "Description");
                nv.Outline = Val(values, "Outline");
                nv.WritingStyle = Val(values, "WritingStyle");
                if (values.ContainsKey("CompletionStatus"))
                    nv.CompletionStatus = Val(values, "CompletionStatus");
                await store.SaveNovel(nv);
                break;
            }

            case EntityType.TimelineEvent:
            {
                var teList = await store.GetTimelineEvents(_novelId);
                var te = entityId is not null
                    ? teList.FirstOrDefault(e => e.Id == entityId)
                    : new TimelineEvent
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        NovelId = _novelId,
                    };
                if (te is null)
                    return;
                te.Title = Val(values, "Title");
                te.Description = Val(values, "Description");
                te.ChapterId = string.IsNullOrWhiteSpace(Val(values, "ChapterId"))
                    ? null
                    : Val(values, "ChapterId");
                await store.SaveTimelineEvent(te);
                break;
            }

            case EntityType.FactionRelationship:
            {
                var frList = await store.GetFactionRelationships(_novelId);
                var frel = entityId is not null
                    ? frList.FirstOrDefault(fr => fr.Id == entityId)
                    : new FactionRelationship
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        NovelId = _novelId,
                    };
                if (frel is null)
                    return;
                // FactionAId/BId: Select returns ID directly
                frel.FactionAId = Val(values, "FactionAId");
                frel.FactionBId = Val(values, "FactionBId");
                frel.Type = Enum.TryParse<FactionRelationType>(
                    Val(values, "Type"),
                    ignoreCase: true,
                    out var frt
                )
                    ? frt
                    : FactionRelationType.Neutral;
                frel.Description = Val(values, "Description");
                await store.SaveFactionRelationship(frel);
                break;
            }

            case EntityType.Note:
            {
                var nt = entityId is not null
                    ? (await store.GetNotes(_novelId)).FirstOrDefault(n => n.Id == entityId)
                    : new AuthorNote
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        NovelId = _novelId,
                    };
                if (nt is null)
                    return;
                nt.Title = Val(values, "Title");
                nt.Body = Val(values, "Body");
                nt.Tags = ParseTags(Val(values, "Tags"));
                nt.Status = Enum.TryParse<NoteStatus>(
                    Val(values, "Status"),
                    ignoreCase: true,
                    out var nst
                )
                    ? nst
                    : nt.Status;
                await store.SaveNote(nt);
                break;
            }
        }
    }
}
