using System.Xml.Linq;
using Core.Data;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定面板 XAML 结构回归锁（列表宿主的虚拟化前提）。
///
/// **历史 bug**：`ScrollViewer → StackPanel → ItemsControl` 链里，VSP 从 StackPanel 拿到无限高度，
/// 因而不做虚拟化、全量实例化（上千章时卡顿）——注释声称「虚拟化」但实际没有。
/// 正确形态是 `ScrollViewer` 的**直接子级**就是 `ItemsControl` + `VirtualizingStackPanel`。
///
/// 断言放在 XAML 结构上：运行期（布局期 realized index）断言需要 Avalonia 平台，
/// 进程内无头驱动不稳定（实测会挂起），故用结构锁 + 应用内冒烟覆盖运行期。
/// </summary>
public sealed class SetUpPanelViewStructureTests
{
    private const string AvaloniaNs = "https://github.com/avaloniaui";

    private static string Find(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }

    private static XElement LoadSetUpPanelView()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Desktop",
                "Views",
                "SetUpPanelView.axaml"
            );
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("未找到 src/Desktop/Views/SetUpPanelView.axaml");
    }

    private static List<XElement> Descendants(XElement root, string localName) =>
        [.. root.Descendants().Where(e => e.Name.LocalName == localName)];

    private static bool HasAttribute(XElement element, string name) =>
        element.Attributes().Any(a => a.Name.LocalName == name);

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    [Fact]
    public void OutlineTree_UsesTreeNodeTreeView_WithNodeTemplate()
    {
        var root = LoadSetUpPanelView();

        // 大纲结构树 = TreeView（节点常驻，展开由控件维护）
        var tree = Descendants(root, "TreeView")
            .Single(e => Attr(e, "ItemsSource").Contains("OutlineNodes", StringComparison.Ordinal));

        var template = Descendants(tree, "TreeDataTemplate").Single();
        Assert.Contains("Children", Attr(template, "ItemsSource"), StringComparison.Ordinal);
        Assert.Contains("OutlineNodeVm", Attr(template, "DataType"), StringComparison.Ordinal);

        // 展开态双向绑定（Avalonia TreeView how-to：TreeViewItem.IsExpanded ↔ 节点 VM）
        Assert.Contains("TreeView.Styles", tree.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Property=\"IsExpanded\" Value=\"{Binding IsExpanded, Mode=TwoWay}\"",
            tree.ToString(),
            StringComparison.Ordinal
        );
        // TreeView 自己滚动：祖先链上不得再有 ScrollViewer（会双重滚动）
        for (var ancestor = tree.Parent; ancestor is not null; ancestor = ancestor.Parent)
            Assert.NotEqual("ScrollViewer", ancestor.Name.LocalName);

        // 行上只有两个动作：＋（下一层新建）与 ⋯（菜单）——
        // 详情 / 删除不再并排挂在行上（用户反馈：四个图标太抽象太乱）
        var markup = tree.ToString();
        Assert.Contains("OutlineNodeAdd_OnClick", markup, StringComparison.Ordinal);
        Assert.Contains("OutlineNodeMore_OnClick", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("OutlineNodeDetail_OnClick", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("OutlineNodeDelete_OnClick", markup, StringComparison.Ordinal);

        // 图标一律 Lucide 线性图标（不用 emoji 文本）
        Assert.DoesNotContain("📄", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("❌", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityList_PutsVirtualizingItemsControlDirectlyUnderScrollViewer()
    {
        var root = LoadSetUpPanelView();

        var entityHost = Descendants(root, "ScrollViewer")
            .Single(e => Attr(e, "IsVisible").Contains("HasNoProgress", StringComparison.Ordinal));
        var child = entityHost.Elements().Single();

        Assert.Equal("ItemsControl", child.Name.LocalName);
        Assert.Single(Descendants(child, "VirtualizingStackPanel"));
    }

    [Fact]
    public void EntityList_HasNoStackPanelBetweenScrollViewerAndItemsControl()
    {
        var root = LoadSetUpPanelView();

        foreach (
            var host in Descendants(root, "ScrollViewer")
                .Where(e =>
                    Attr(e, "IsVisible").Contains("HasNoProgress", StringComparison.Ordinal)
                )
        )
        {
            // 直接子级不得是 StackPanel（历史 bug 形态）；也不得再包一层任意 Panel。
            foreach (var child in host.Elements())
            {
                Assert.Equal("ItemsControl", child.Name.LocalName);
            }
        }
    }

    [Fact]
    public void OutlineNodeMenu_CoversLevelActions_ByKind()
    {
        var root = LoadSetUpPanelView();
        var tree = Descendants(root, "TreeView")
            .Single(e => Attr(e, "ItemsSource").Contains("OutlineNodes", StringComparison.Ordinal));
        var markup = tree.ToString();

        // 层级菜单收敛：小说/卷 = 详情 · 检查 · 删除；章 = 细纲 · 内容 · 删除
        Assert.Equal(2, markup.Split("Header=\"详情\"").Length - 1); // 小说 / 卷各一条
        Assert.Equal(2, markup.Split("Header=\"检查\"").Length - 1); // 书 / 卷各一条
        Assert.Equal(3, markup.Split("Header=\"删除\"").Length - 1); // 书 / 卷 / 章各一条
        Assert.Equal(1, markup.Split("Header=\"细纲\"").Length - 1); // 章节一条
        Assert.Equal(1, markup.Split("Header=\"内容\"").Length - 1); // 章节一条

        // 旧菜单项已全部下线：新建/重命名/换序/移动到卷/打开正文/状态/检查运行/规则/完结状态
        foreach (
            var gone in new[]
            {
                "新建分卷",
                "新建章节",
                "本卷新建章节",
                "重命名（行内）",
                "上移",
                "下移",
                "打开正文",
                "切换状态",
                "上一章",
                "下一章",
                "设定检查",
                "世界规则",
                "切换完结状态",
                "删除分卷",
                "删除章节",
                "移动到卷",
            }
        )
            Assert.DoesNotContain(gone, markup, StringComparison.Ordinal);

        // 删除项带 Lucide Trash 图标（不是 emoji），且各层级删除都收在菜单里
        Assert.Contains("Kind=\"Trash\"", markup, StringComparison.Ordinal);
        // 层级判定绑定（菜单按节点类型显示）
        Assert.Contains("IsVisible=\"{Binding IsNovel}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsVolume}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsChapter}\"", markup, StringComparison.Ordinal);

        // 新入口处理器（细纲 / 内容 / 书级删除）已接线
        Assert.Contains("ChapterOutline_OnClick", markup, StringComparison.Ordinal);
        Assert.Contains("ChapterContent_OnClick", markup, StringComparison.Ordinal);
        Assert.Contains("NovelDelete_OnClick", markup, StringComparison.Ordinal);

        // 大纲工具行已收敛为「范围/状态 + 云同步」两行（book 级重复按钮全部上树）
        var outlineChrome =
            Descendants(root, "Grid")
                .Single(e =>
                    Attr(e, "Grid.Row") == "1" && Attr(e, "IsVisible") == "{Binding HasProgress}"
                )
                .ToString()
            + Descendants(root, "StackPanel")
                .Single(e =>
                    Attr(e, "Grid.Row") == "2"
                    && e.ToString().Contains("IsCloudConfigured", StringComparison.Ordinal)
                )
                .ToString();
        foreach (var gone in new[] { "设定检查", "世界规则", "连载中", "新建章节", "新建分卷" })
            Assert.DoesNotContain(gone, outlineChrome, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchBox_LivesInsideTabContent_AndBindsSearchText_OnceOnly()
    {
        var root = LoadSetUpPanelView();

        // 搜索框在 tab 内容区（tab strip 下方），且绑定同一 VM 属性
        var tab = Descendants(root, "TabControl").Single();
        Assert.Contains(
            "{Binding Parent.SearchText, Mode=TwoWay}",
            tab.ToString(),
            StringComparison.Ordinal
        );
        Assert.Contains("ClearSearch_OnClick", tab.ToString(), StringComparison.Ordinal);

        // **只允许一个搜索框**：旧大纲工具行里那个「搜索章节」是同一属性的第二个输入框，
        // 两个框绑同一状态 = 同一件事两处输入（已删）。搜索框数量回归锁：
        Assert.Equal(
            1,
            Descendants(tab, "TextBox")
                .Count(e => Attr(e, "Text").Contains("Parent.SearchText", StringComparison.Ordinal))
        );

        // 跨分区结果视图已下线（搜索作用于当前 tab 内容）
        var view = root.ToString();
        Assert.DoesNotContain("SearchResults", view, StringComparison.Ordinal);
        Assert.DoesNotContain("HasSearchQuery", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ChapterTitleFilter", view, StringComparison.Ordinal);
    }

    [Fact]
    public void TabContent_HasSingleToolbarBand_AndNoDuplicateTitleRow()
    {
        var root = LoadSetUpPanelView();
        var tab = Descendants(root, "TabControl").Single();
        var content = Descendants(tab, "DataTemplate")
            .First(e => e.ToString().Contains("Parent.SearchText", StringComparison.Ordinal));

        // 内容模板 = 4 行（工具带 / 大纲范围行 / 大纲云同步行 / 内容），旧版是 5 行
        var grid = content.DescendantsAndSelf().First(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,Auto,Auto,*", Attr(grid, "RowDefinitions"));

        // 标题行（Eyebrow + Header）不再重复 tab 带上的标签
        Assert.DoesNotContain("Eyebrow", grid.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Header}", grid.ToString(), StringComparison.Ordinal);

        // 进度卡下线：进度条并入摘要条（与 BookSummary 计数同一个 Border）
        Assert.DoesNotContain("ProgressText", root.ToString(), StringComparison.Ordinal);
        var bar = Descendants(grid, "ProgressBar").Single();
        Assert.Contains(
            "BookSummary.ChapterCount",
            bar.Parent!.Parent!.ToString(),
            StringComparison.Ordinal
        );

        // 空态区分「还没有 X」与「未命中 X」
        Assert.Contains(
            "{Binding Parent.SearchEmptyHint}",
            root.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// 搜索字段占满整行：TextBox 是工具带 Grid 的直接子级（无并列列），右端只内嵌「清空」
    /// 图标按钮（有词才显示；同一 Grid、右对齐），字段右侧留出内边距——图标永不压住文字。
    /// 放大镜只出现在元信息行的「筛选」入口（TUI 同口径，占位文案不含放大镜）；
    /// 计数 + 「添加」「筛选」在元信息行（Grid.Row=1），不与搜索框抢宽度。
    /// </summary>
    [Fact]
    public void SearchField_SpansFullWidth_WithInlineIconButtons()
    {
        var root = LoadSetUpPanelView();
        var tab = Descendants(root, "TabControl").Single();
        var content = Descendants(tab, "DataTemplate")
            .First(e => e.ToString().Contains("Parent.SearchText", StringComparison.Ordinal));

        var box = Descendants(content, "TextBox")
            .Single(e => Attr(e, "Text").Contains("Parent.SearchText", StringComparison.Ordinal));
        var host = box.Parent!;
        Assert.Equal("Grid", host.Name.LocalName);
        Assert.False(HasAttribute(host, "ColumnDefinitions")); // 单列 = 字段本身占满整行

        // 右侧内嵌图标：只剩清除（有词才显示）；旧的「聚焦 magnifier」已删（假的提交入口）
        var icons = Descendants(host, "Button").ToList();
        Assert.Single(icons);
        Assert.Equal("ClearSearch_OnClick", Attr(icons[0], "Click"));
        Assert.Equal("Right", Attr(icons[0].Parent!, "HorizontalAlignment"));
        Assert.DoesNotContain("SearchIcon_OnClick", content.ToString(), StringComparison.Ordinal);
        // 文本框右侧留白给图标（10,4,34,4）
        Assert.Equal("10,4,34,4", Attr(box, "Padding"));
        // 占位文案按分区（含筛选命中数）由 VM 给出
        Assert.Contains("Parent.SearchPlaceholder", content.ToString(), StringComparison.Ordinal);

        // 计数 + 添加 + 筛选在元信息行（Grid.Row=1, 仅实体 tab）
        var metaRow = Descendants(content, "Grid")
            .Single(e =>
                Attr(e, "Grid.Row") == "1"
                && Attr(e, "IsVisible") == "{Binding HasNoProgress}"
                && e.ToString().Contains("AddEntity_OnClick", StringComparison.Ordinal)
            );
        Assert.Contains("{Binding Items.Count", metaRow.ToString(), StringComparison.Ordinal);
        Assert.Contains("FilterEntity_OnClick", metaRow.ToString(), StringComparison.Ordinal);
        var codeBehind = File.ReadAllText(
            Find("src", "Desktop", "Views", "SetUpPanelView.axaml.cs")
        );
        Assert.Contains("FilterEntity_OnClick", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchIcon_OnClick", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// 分区顺序（对齐 TUI SetUpPanel）：大纲在首位——VM 的 SectionTypes / Tabs 与视图默认
    /// SelectedIndex=0 三处必须一致（顺序靠约定，翻车无编译期报错）。
    /// </summary>
    [Fact]
    public void SectionOrder_OutlineFirst_MatchesVmAndView()
    {
        Assert.Equal(EntityType.Chapter, SetUpPanelViewModel.SectionTypes[0]);
        Assert.Equal(8, SetUpPanelViewModel.SectionTypes.Length);

        var vm = new SetUpPanelViewModel(); // 设计时构造：只建 Tabs，不碰存储
        Assert.Equal(8, vm.Tabs.Count);
        Assert.True(vm.Tabs[0].HasProgress); // 首 tab = 大纲（进度卡专用）
        Assert.Equal(
            ["大纲", "角色", "组织", "地点", "情节", "场景", "道具", "关系"],
            vm.Tabs.Select(t => t.Header)
        );

        var root = LoadSetUpPanelView();
        var tab = Descendants(root, "TabControl").Single();
        Assert.Equal("0", Attr(tab, "SelectedIndex"));
    }

    [Fact]
    public void TabStrip_IsWrappingPillRow_NotUniformGrid()
    {
        var root = LoadSetUpPanelView();
        var tab = Descendants(root, "TabControl").Single();
        var panel = Descendants(tab, "ItemsPanelTemplate")
            .First(e => Descendants(e, "WrapPanel").Count > 0);

        Assert.Single(Descendants(panel, "WrapPanel"));
        Assert.DoesNotContain("UniformGrid", tab.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OutlinePane_HasDockedInlineCreateBar()
    {
        var root = LoadSetUpPanelView();
        // 输入条停靠在树脚下（Auto 行，隐藏时 0 高，不占树高）
        var bar = Descendants(root, "Border")
            .Single(e => Attr(e, "IsVisible") == "{Binding Parent.InlineCreateActive}");
        Assert.Equal("2", Attr(bar, "Grid.Row"));

        var grid = bar.Parent!;
        Assert.Equal("Auto,*,Auto", Attr(grid, "RowDefinitions"));
        Assert.Equal("3", Attr(grid, "Grid.Row")); // Tab 模板里的内容行（工具带 + 大纲两行之后）

        // 输入框：KeyDown 接线 + Enter/Esc 提示（与 TUI 同口径）
        var box = bar.Descendants()
            .Single(e => e.Name.LocalName == "TextBox" && Attr(e, "Name") == "InlineCreateBox");
        Assert.Equal("InlineCreateBox_OnKeyDown", Attr(box, "KeyDown"));
        Assert.Contains("Enter 新建", Attr(box, "PlaceholderText"), StringComparison.Ordinal);
        Assert.Contains("Esc", Attr(box, "PlaceholderText"), StringComparison.Ordinal);
    }
}
