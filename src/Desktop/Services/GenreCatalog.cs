using Core.Data;

namespace Desktop.Services;

/// <summary>
/// 一个两级题材选择：Parent = 一级（如“轻小说”），Child = 二级（如“原生幻想”）。
/// 展示顺序固定为「二级 │ 一级」，与项目列表 / 编辑窗一致。
/// </summary>
public sealed record GenreSelection(string Parent, string Child)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Parent) && string.IsNullOrWhiteSpace(Child);

    public string Display =>
        string.IsNullOrWhiteSpace(Parent) ? Child
        : string.IsNullOrWhiteSpace(Child) ? Parent
        : $"{Child} │ {Parent}";

    public override string ToString() => Display;
}

/// <summary>一级题材分类及其全部二级题材。</summary>
public sealed record GenreCategory(string Name, IReadOnlyList<string> Subcategories);

/// <summary>
/// 运行时题材目录（云端 / 本地 YAML 缓存 / 内置 GenreDefinitions 的统一投影）。
/// 界面只读本类型，不再直接触碰静态 GenreDefinitions，以便云端目录热替换。
/// </summary>
public sealed class GenreCatalog
{
    public string Version { get; init; } = GenreDefinitions.CatalogVersion;

    /// <summary>cloud / cache / local，用于设置页/诊断显示。</summary>
    public string Source { get; init; } = "local";

    public IReadOnlyList<GenreCategory> Categories { get; init; } = [];

    /// <summary>内置目录：云端不可达且没有缓存时的最后兜底。</summary>
    public static GenreCatalog Local { get; } = BuildLocal();

    /// <summary>默认选项：第一个一级分类下的第一个二级分类。</summary>
    public GenreSelection? DefaultSelection
    {
        get
        {
            foreach (var category in Categories)
            {
                if (category.Subcategories.Count > 0)
                    return new GenreSelection(category.Name, category.Subcategories[0]);
            }
            return null;
        }
    }

    /// <summary>按二级名查找（parent 为空时跨一级搜索；同名二级以第一个命中为准）。</summary>
    public GenreSelection? Find(string? parent, string? child)
    {
        child = child?.Trim() ?? "";
        if (child.Length == 0)
            return null;

        foreach (var category in Categories)
        {
            if (
                !string.IsNullOrWhiteSpace(parent)
                && !string.Equals(category.Name, parent.Trim(), StringComparison.Ordinal)
            )
                continue;

            foreach (var subcategory in category.Subcategories)
            {
                if (string.Equals(subcategory, child, StringComparison.Ordinal))
                    return new GenreSelection(category.Name, subcategory);
            }
        }

        return null;
    }

    /// <summary>
    /// 把持久化的 parent/child 解析成目录中的选项。
    /// 旧数据只有二级名时按二级名反查一级；无法识别时保留原值（不静默改写用户数据）。
    /// </summary>
    public GenreSelection Resolve(string? parent, string? child)
    {
        parent = parent?.Trim() ?? "";
        child = child?.Trim() ?? "";

        var exact = Find(parent, child);
        if (exact is not null)
            return exact;

        // parent 有效但 child 为空：落到该一级分类的第一个二级，方便用户继续选择。
        if (child.Length == 0 && parent.Length > 0)
        {
            var category = FindCategory(parent);
            if (category is { Subcategories.Count: > 0 })
                return new GenreSelection(category.Name, category.Subcategories[0]);
        }

        if (parent.Length == 0 && child.Length == 0)
            return DefaultSelection ?? new GenreSelection("", "");

        return new GenreSelection(parent, child);
    }

    public GenreCategory? FindCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Categories.FirstOrDefault(category =>
            string.Equals(category.Name, name.Trim(), StringComparison.Ordinal)
        );
    }

    private static GenreCatalog BuildLocal() =>
        new()
        {
            Version = GenreDefinitions.CatalogVersion,
            Source = "local",
            Categories =
            [
                .. GenreDefinitions.All.Select(category => new GenreCategory(
                    category.Name,
                    [.. category.Subcategories.Select(subcategory => subcategory.Name)]
                )),
            ],
        };
}
