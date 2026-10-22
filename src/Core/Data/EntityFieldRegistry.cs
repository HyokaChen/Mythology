namespace Core.Data;

/// <summary>字段类型（与 TUI EntityEditDialog 同构）。</summary>
public enum FieldType
{
    TextBox,
    TextArea,
    Select,
    FreeTag,
    MultiSelect,
    CheckBox,
    ReadOnly, // 只读单行信息（如章节号/字数/更新时间：展示用，不参与保存）
}

/// <summary>字段定义（属性名 / 标签 / 类型 / 选项 / 提示）。</summary>
public sealed record FieldDef(
    string Prop,
    string Label,
    FieldType Type,
    string[]? Options = null,
    string[]? OptionValues = null,
    string? Placeholder = null
);

/// <summary>标签页定义（实体字段分组）。</summary>
public sealed record TabDef(string Name, FieldDef[] Fields);

/// <summary>
/// 章节编辑作用域（大纲行 ⋯ 菜单的「细纲 / 内容」两个入口）：
/// Full = 全部页签；Outline = 除正文外的页签；Content = 只留正文页签。
/// </summary>
public enum ChapterEditScope
{
    Full,
    Outline,
    Content,
}

/// <summary>设定面板编辑窗字段注册表。</summary>
public static class EntityFieldRegistry
{
    public static readonly TabDef[] CharacterTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "姓名", FieldType.TextBox),
                new(
                    "Gender",
                    "性别",
                    FieldType.Select,
                    ["男", "女", "其他"],
                    // 存英文枚举值：与 Role / Faction.Type / Location.Type 等字段同一约定，
                    // 且 HarnessContextBuilder 会把它原样送进云端契约（CloudCharacterInput）、
                    // DocumentBuilder 会把它拼进向量文档——是线协议值，不能改成中文。
                    // 旧数据里的中文值由展示 / 回填侧归一兜底（EntityEditWindow.PopulateFields +
                    // SetUpPanelViewModel.GenderLabel）。
                    ["Male", "Female", "Other"]
                ),
                new("Age", "年龄", FieldType.TextBox),
                new(
                    "Role",
                    "定位",
                    FieldType.Select,
                    ["主角", "配角", "龙套", "反派"],
                    ["Protagonist", "Supporting", "Minor", "Antagonist"]
                ),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["活跃", "已死亡", "失踪", "已转变"],
                    ["Active", "Deceased", "Missing", "Transformed"]
                ),
                new("FactionId", "所属组织", FieldType.Select),
                new("CurrentLocationId", "当前地点", FieldType.Select),
            ]
        ),
        new(
            "性格描写",
            [
                new("Personality", "性格", FieldType.TextArea),
                new("Appearance", "外貌", FieldType.TextArea),
                new("Habits", "习惯", FieldType.TextArea),
                new("Background", "背景", FieldType.TextArea),
                new("Motivation", "动机", FieldType.TextArea),
                new("Weakness", "弱点", FieldType.TextArea),
            ]
        ),
        new(
            "备注",
            [
                new("Tags", "标签", FieldType.FreeTag, Placeholder: "每行一个标签"),
                new("Abilities", "能力", FieldType.TextArea),
                new("Notes", "备注", FieldType.TextArea),
            ]
        ),
    ];

    public static readonly TabDef[] PlotlineTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "名称", FieldType.TextBox),
                new(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["主线", "支线", "隐藏", "伏笔"],
                    ["Main", "Side", "Hidden", "Foreshadowing"]
                ),
                new(
                    "Tension",
                    "紧张度",
                    FieldType.Select,
                    ["低", "中", "高", "临界"],
                    ["Low", "Medium", "High", "Critical"]
                ),
                new("PlannedRevealChapterId", "计划揭示章节", FieldType.Select),
                new("ResolvedAtChapterId", "解决章节", FieldType.Select),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["进行中", "已解决", "放弃"],
                    ["Active", "Resolved", "Abandoned"]
                ),
            ]
        ),
        new(
            "叙事内容",
            [
                new("Description", "描述", FieldType.TextArea),
                new("Turnpoints", "关键转折", FieldType.TextArea, Placeholder: "每行一个转折点"),
            ]
        ),
    ];

    public static readonly TabDef[] FactionTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "名称", FieldType.TextBox),
                new(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["门派", "公司", "家族", "帮会", "政府", "其他"],
                    ["Sect", "Company", "Family", "Gang", "Government", "Other"]
                ),
                new("Leader", "首领", FieldType.Select),
                new("HeadquartersId", "总部", FieldType.Select),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["活跃", "蛰伏", "覆灭"],
                    ["Active", "Dormant", "Dissolved"]
                ),
                new("Description", "描述", FieldType.TextArea),
                new("Goal", "目标", FieldType.TextArea),
            ]
        ),
        new("成员", [new("Members", "成员", FieldType.MultiSelect)]),
    ];

    public static readonly TabDef[] LocationTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "名称", FieldType.TextBox),
                new(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["城市", "乡村", "室内", "荒野", "虚构", "其他"],
                    ["City", "Rural", "Indoor", "Wilderness", "Fictional", "Other"]
                ),
                new("Atmosphere", "氛围", FieldType.TextBox),
                new("Description", "描述", FieldType.TextArea),
                new("ParentId", "所属区域", FieldType.Select),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["存在", "毁灭", "封印", "废弃"],
                    ["Active", "Destroyed", "Sealed", "Abandoned"]
                ),
            ]
        ),
    ];

    public static readonly TabDef[] ItemTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "名称", FieldType.TextBox),
                new(
                    "Type",
                    "类型",
                    FieldType.Select,
                    ["武器", "法器", "信物", "消耗品", "其他"],
                    ["Weapon", "Artifact", "Token", "Consumable", "Other"]
                ),
                new("Description", "描述", FieldType.TextArea),
                new("OwnerId", "持有者", FieldType.Select),
                new("LocationId", "所在地", FieldType.Select),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["完好", "遗失", "损毁"],
                    ["Active", "Lost", "Destroyed"]
                ),
            ]
        ),
    ];

    public static readonly TabDef[] RelationshipTabs =
    [
        new(
            "基本信息",
            [
                // 字段序对齐 TUI EntityEditDialog：先选人 → 再定时间范围 → 再定类型 / 强度
                new("CharacterAId", "角色A", FieldType.Select),
                new("CharacterBId", "角色B", FieldType.Select),
                new("SinceChapterId", "起始章节", FieldType.Select),
                new("UntilChapterId", "结束章节", FieldType.Select),
                new(
                    "Type",
                    "类型",
                    FieldType.Select,
                    [
                        "朋友",
                        "对手",
                        "家人",
                        "恋人",
                        "敌人",
                        "导师",
                        "主仆",
                        "同门",
                        "父母",
                        "子女",
                        "兄弟姐妹",
                        "夫妻 / 道侣",
                        "结拜",
                        "徒弟",
                        "盟友",
                    ],
                    [
                        "Friend",
                        "Rival",
                        "Family",
                        "Love",
                        "Enemy",
                        "Mentor",
                        "MasterServant",
                        "FellowDisciple",
                        "ParentOf",
                        "ChildOf",
                        "Sibling",
                        "Spouse",
                        "Sworn",
                        "Disciple",
                        "Ally",
                    ]
                ),
                new(
                    "Strength",
                    "强度",
                    FieldType.Select,
                    ["弱", "中", "强", "极强"],
                    ["Weak", "Medium", "Strong", "Extreme"]
                ),
                new("Description", "描述", FieldType.TextArea),
            ]
        ),
    ];

    public static readonly TabDef[] SceneTabs =
    [
        new(
            "基本信息",
            [
                new("Title", "标题", FieldType.TextBox),
                new("ChapterId", "所属章节", FieldType.Select),
                new("LocationId", "地点", FieldType.Select),
                new("TimeDescription", "时间", FieldType.TextBox),
                new(
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
                new("ParticipantIds", "参与角色", FieldType.MultiSelect),
                new("PlotlineIds", "推进情节线", FieldType.MultiSelect),
                new("ItemIds", "出场道具", FieldType.MultiSelect),
                new("Conflict", "冲突", FieldType.TextArea),
                new("Goal", "目标", FieldType.TextArea),
                new("EmotionalArc", "情绪弧线", FieldType.TextBox),
                new("Notes", "备注", FieldType.TextArea),
            ]
        ),
    ];

    public static readonly TabDef[] ChapterTabs =
    [
        new(
            "基本信息",
            [
                new("Title", "标题", FieldType.TextBox),
                // 只读信息（卷内序号 / 字数 / 更新时间）：由编辑窗按章预计算后展示，不参与保存
                // ——与全书全局 Order 区分（展示用卷内号，落库 / 排序仍用 Order）。
                new("ChapterNumber", "章节号", FieldType.ReadOnly),
                new("CharCount", "字数", FieldType.ReadOnly),
                new("UpdatedAt", "更新时间", FieldType.ReadOnly),
                new("Order", "全书序号", FieldType.TextBox),
                new("Status", "状态", FieldType.Select, ["草稿", "发布"], ["Draft", "Published"]),
                new("VolumeId", "所属分卷", FieldType.Select),
                new("Summary", "细纲·梗概", FieldType.TextArea, Placeholder: "本章发生什么"),
            ]
        ),
        new(
            "细纲",
            [
                new("PlotlineIds", "计划推进情节线", FieldType.MultiSelect),
                new("CharacterIds", "计划出场角色", FieldType.MultiSelect),
                new("Notes", "备注", FieldType.TextArea, Placeholder: "节奏/伏笔提示"),
            ]
        ),
        new("正文", [new("Content", "内容", FieldType.TextArea)]),
    ];

    public static readonly TabDef[] VolumeTabs =
    [
        new(
            "基本信息",
            [
                new("Name", "卷名", FieldType.TextBox),
                new("Order", "卷序号", FieldType.TextBox),
                new("Summary", "卷纲", FieldType.TextArea, Placeholder: "本卷剧情走向 / 关键转折"),
                new("Description", "卷描述", FieldType.TextArea),
                new("Goal", "本卷目标 / 核心冲突", FieldType.TextArea),
                new("Notes", "备注", FieldType.TextArea, Placeholder: "节奏/伏笔/卷末钩子"),
            ]
        ),
        new(
            "本卷规划",
            [
                new("PlotlineIds", "推进情节线", FieldType.MultiSelect),
                new("CharacterIds", "关键角色", FieldType.MultiSelect),
            ]
        ),
    ];

    public static readonly TabDef[] NoteTabs =
    [
        new(
            "笔记",
            [
                new("Title", "标题", FieldType.TextBox),
                new(
                    "Body",
                    "正文",
                    FieldType.TextArea,
                    Placeholder: "随手记：设定 / 桥段 / 伏笔 / 灵感"
                ),
                new("Tags", "标签", FieldType.FreeTag),
                new(
                    "Status",
                    "状态",
                    FieldType.Select,
                    ["收件箱", "已确认", "已落地", "已废弃"],
                    ["Inbox", "Active", "Done", "Dropped"]
                ),
                new("CapturedChapterOrder", "记录于第N章", FieldType.ReadOnly),
            ]
        ),
    ];

    public static readonly TabDef[] NovelTabs =
    [
        new(
            "小说定义",
            [
                new("Title", "标题", FieldType.TextBox),
                new("Genre", "类型", FieldType.TextBox),
                new("Author", "作者", FieldType.TextBox),
                new("TargetWords", "目标字数", FieldType.TextBox, Placeholder: "字（如 50000）"),
                new("Description", "简介", FieldType.TextArea),
                new(
                    "Outline",
                    "总纲",
                    FieldType.TextArea,
                    Placeholder: "全书走向 / 主题 / 核心冲突 / 结局方向"
                ),
                new("WritingStyle", "写作风格", FieldType.TextBox),
                new("TargetAudience", "目标读者", FieldType.TextBox),
                new(
                    "EnhancementProfile",
                    "增强配置",
                    FieldType.Select,
                    ["商业向", "文学向", "均衡向", "自定义"],
                    ["commercial", "literary", "balanced", "custom"]
                ),
                new("AutoDiagnose", "自动诊断", FieldType.CheckBox),
                new(
                    "AutoEnhance",
                    "自动增强",
                    FieldType.Select,
                    ["关闭", "半自动", "全自动"],
                    ["none", "semi", "full"]
                ),
                new(
                    "CompletionStatus",
                    "连载状态",
                    FieldType.Select,
                    ["连载中", "已完结"],
                    ["ongoing", "completed"]
                ),
            ]
        ),
    ];

    /// <summary>按编辑作用域取章节页签（细纲 = 去正文；内容 = 只留正文；Full = 全部）。</summary>
    public static TabDef[] GetChapterTabs(ChapterEditScope scope) =>
        scope switch
        {
            ChapterEditScope.Outline => [.. ChapterTabs.Where(t => t.Name != "正文")],
            ChapterEditScope.Content => [.. ChapterTabs.Where(t => t.Name == "正文")],
            _ => ChapterTabs,
        };

    public static TabDef[] GetTabs(EntityType type) =>
        type switch
        {
            EntityType.Character => CharacterTabs,
            EntityType.Faction => FactionTabs,
            EntityType.Location => LocationTabs,
            EntityType.Plotline => PlotlineTabs,
            EntityType.Scene => SceneTabs,
            EntityType.Item => ItemTabs,
            EntityType.Relationship => RelationshipTabs,
            EntityType.Chapter => ChapterTabs,
            EntityType.Volume => VolumeTabs,
            EntityType.Novel => NovelTabs,
            EntityType.Note => NoteTabs,
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "此实体类型没有设定编辑窗"
            ),
        };

    public static string Label(EntityType type) =>
        type switch
        {
            EntityType.Character => "角色",
            EntityType.Plotline => "情节",
            EntityType.Faction => "组织",
            EntityType.Location => "地点",
            EntityType.Scene => "场景",
            EntityType.Item => "道具",
            EntityType.Relationship => "关系",
            EntityType.Chapter => "章节",
            EntityType.Volume => "分卷",
            EntityType.Novel => "小说",
            EntityType.TimelineEvent => "时间线事件",
            EntityType.FactionRelationship => "组织关系",
            EntityType.WorldRule => "世界规则",
            EntityType.Note => "笔记",
            _ => "实体",
        };

    public static EntityType? SelectSource(string prop) =>
        prop switch
        {
            "FactionId" => EntityType.Faction,
            "Leader" or "OwnerId" or "CharacterAId" or "CharacterBId" => EntityType.Character,
            "HeadquartersId" or "ParentId" or "LocationId" or "CurrentLocationId" =>
                EntityType.Location,
            "PlannedRevealChapterId"
            or "ResolvedAtChapterId"
            or "ChapterId"
            or "SinceChapterId"
            or "UntilChapterId" => EntityType.Chapter,
            "VolumeId" => EntityType.Volume,
            _ => null,
        };

    public static EntityType? MultiSelectSource(string prop) =>
        prop switch
        {
            "PlotlineIds" => EntityType.Plotline,
            "CharacterIds" or "ParticipantIds" or "Members" => EntityType.Character,
            "ItemIds" => EntityType.Item,
            _ => null,
        };

    /// <summary>可写字段（排除 ReadOnly 展示字段）。</summary>
    public static IReadOnlyList<FieldDef> WritableFields(EntityType type) =>
        [.. GetTabs(type).SelectMany(t => t.Fields).Where(f => f.Type is not FieldType.ReadOnly)];

    /// <summary>按字段名查找可写字段：Prop 或 kebab-case 均可，忽略大小写与 - / _ 分隔符。</summary>
    public static FieldDef? FindWritableField(EntityType type, string field)
    {
        static string Norm(string s) => s.Replace("-", "").Replace("_", "").ToLowerInvariant();
        var key = Norm(field);
        return WritableFields(type).FirstOrDefault(f => Norm(f.Prop) == key);
    }
}
