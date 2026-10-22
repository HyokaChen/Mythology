namespace Desktop.Models;

using Lucide.Avalonia;

/// <summary>设定面板实体项（右侧 8-Tab，UI.md §3）。</summary>
public sealed class EntityItem
{
    /// <summary>实体 ID。</summary>
    public string Id { get; init; } = "";

    /// <summary>实体名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>摘要（一行描述）。</summary>
    public string Summary { get; init; } = "";

    /// <summary>类型图标（Lucide 单色描边，按实体类型区分）。</summary>
    public LucideIconKind IconKind { get; init; } = LucideIconKind.CircleDot;

    /// <summary>状态标记（如 情节 "[!]伏笔";章节 "✓"）。</summary>
    public string Marker { get; init; } = "";

    /// <summary>状态标记图标（情节专用，如 [!] [=] [?] [*] [✓]）。</summary>
    public string MarkerIcon { get; init; } = "";

    /// <summary>是否有状态标记。</summary>
    public bool HasMarker => Marker.Length > 0;

    /// <summary>标记单行文本（图标 + 文字，与名字同行的 pill；无标记为空）。</summary>
    public string MarkerText =>
        !HasMarker ? ""
        : MarkerIcon.Length > 0 ? $"{MarkerIcon} {Marker}"
        : Marker;

    /// <summary>引用前缀（点击实体插入 `类型「名称」`）。</summary>
    public string ReferenceKind { get; init; } = "实体";

    /// <summary>详情行（展开显示）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = [];

    /// <summary>关系标签（卡面 Chip 行：由本体关系索引生成，如「所属分卷 第1卷」「所属组织 青云门」）。</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>元数据行（mono 小字：状态 · 字数 · 出场/变化计数；章节专用）。</summary>
    public string MetaLine { get; init; } = "";

    /// <summary>
    /// 筛选维度一（与编辑窗枚举值同口径：角色 = 定位 / 组织·地点·道具 = 类型 /
    /// 情节 = 类型 / 场景 = 所属卷 id（未分卷 = ""）/ 关系 = 类型）。
    /// </summary>
    public string FilterPrimary { get; init; } = "";

    /// <summary>筛选维度二（角色 = 性别标签 / 情节 = 紧张度 / 关系 = 强度；无 = null）。</summary>
    public string? FilterSecondary { get; init; }

    /// <summary>是否显示进度（大纲 Tab 用）。</summary>
    public double Progress { get; init; }
}
