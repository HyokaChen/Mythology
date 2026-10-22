namespace Core.Data;

/// <summary>
/// 世界规则类型（<c>world-rules.yaml</c> 的 <c>type</c> 值）——单一来源。
///
/// 云端 <c>StpChecker</c> 按这些键启用精确检查（旅行速度 / 地点距离）；未声明的类型静默不判。
/// 值统一 kebab-case：历史 snake 值由一次性迁移 007 改写，云端比较亦做归一化以兼容旧客户端数据。
/// </summary>
public static class WorldRuleTypes
{
    /// <summary>规范键 + 展示标签 + 单位提示（TUI 与 Desktop 共用，避免两份清单漂移）。</summary>
    public static readonly (string Key, string Label, string UnitHint)[] All =
    [
        ("travel-speed", "旅行速度", "里/日"),
        ("location-distance", "地点距离", "里（描述填 地点A>地点B）"),
    ];

    /// <summary>归一化：去空白、小写、下划线 → 连字符（历史 snake 值与手输大小写都对齐规范键）。</summary>
    public static string Normalize(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "" : type.Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>规范键 → 展示标签；未知类型原样返回（规则类型为开放词汇，不丢信息）。</summary>
    public static string LabelFor(string? type)
    {
        var key = Normalize(type);
        foreach (var (canonical, label, _) in All)
        {
            if (canonical == key)
                return label;
        }
        return type ?? "";
    }
}
