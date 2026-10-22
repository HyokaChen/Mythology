namespace Desktop.Models;

/// <summary>实体筛选的一个维度：字段标签 + 选项（首项固定 ("全部", null)）。</summary>
public sealed record EntityFilterField(
    string Label,
    IReadOnlyList<(string Label, string? Value)> Options
);

/// <summary>
/// 实体列表筛选条件（纯数据 + 匹配规则，可单测）：<see cref="Primary" /> / <see cref="Secondary" />
/// 是该实体两个维度上的取值（null = 不限；各类型如何解释这两个槽位由构建选项的一方决定，
/// <see cref="Matches" /> 只做等值判断）。与 TUI EntityListFilter 同口径。
/// </summary>
public readonly record struct EntityListFilter(string? Primary, string? Secondary = null)
{
    /// <summary>空条件（显示全部）。</summary>
    public static EntityListFilter None => new(null, null);

    /// <summary>是否有任一维度生效。</summary>
    public bool IsActive => Primary is not null || Secondary is not null;

    /// <summary>实体在某维度上的取值是否命中条件（null 维度 = 不限）。</summary>
    public bool Matches(string? primary, string? secondary) =>
        (Primary is null || string.Equals(Primary, primary ?? "", StringComparison.Ordinal))
        && (
            Secondary is null || string.Equals(Secondary, secondary ?? "", StringComparison.Ordinal)
        );
}
