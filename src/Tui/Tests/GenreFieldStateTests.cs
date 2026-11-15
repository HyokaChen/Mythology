using ClientPlatform;
using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 小说编辑器的两级题材状态：旧数据反查一级、云端目录热替换时保留当前选择。
/// </summary>
public sealed class GenreFieldStateTests
{
    private static readonly GenreCatalog Catalog = new()
    {
        Categories =
        [
            new GenreCategory("轻小说", ["原生幻想", "青春日常"]),
            new GenreCategory("玄幻", ["东方玄幻"]),
        ],
    };

    [Fact]
    public void PreservesTwoLevelSelection()
    {
        var state = new EntityEditDialog.GenreFieldState(
            Catalog,
            new GenreSelection("轻小说", "原生幻想")
        );

        Assert.Equal("原生幻想 │ 轻小说", state.Selection.Display);
    }

    [Fact]
    public void LegacyChildInfersParentFromCatalog()
    {
        var resolved = Catalog.Resolve(null, "原生幻想");
        var state = new EntityEditDialog.GenreFieldState(Catalog, resolved);

        Assert.Equal("轻小说", state.Selection.Parent);
        Assert.Equal("原生幻想", state.Selection.Child);
    }

    [Fact]
    public void UnknownLegacyChild_PreservesEmptyParent()
    {
        var resolved = Catalog.Resolve(null, "未知题材");
        var state = new EntityEditDialog.GenreFieldState(Catalog, resolved);

        Assert.Equal("", state.Selection.Parent);
        Assert.Equal("未知题材", state.Selection.Child);
    }

    [Fact]
    public void CatalogRefreshKeepsCurrentSelection()
    {
        var state = new EntityEditDialog.GenreFieldState(
            Catalog,
            new GenreSelection("轻小说", "原生幻想")
        );
        var refreshed = new GenreCatalog
        {
            Categories = [new GenreCategory("轻小说", ["原生幻想", "青春日常", "新分类"])],
        };

        state.ApplyCatalog(refreshed);

        Assert.Equal("原生幻想 │ 轻小说", state.Selection.Display);
        Assert.Contains("新分类", state.CurrentSubcategories());
    }
}
