using System.Text;
using Core.Data;
using Core.Novels;
using Core.Shared;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;

namespace Tui.Controls;

/// <summary>
/// 大纲树（XenoAtom <see cref="TreeView"/>）：根 = 当前小说，二层 = 卷，三层 = 章。
///
/// 节点右侧挂层级动作（<see cref="TreeNode.RightVisuals"/>，Always 可见）：
///   小说：＋（新增分卷）· ⋯（详情 / 检查 / 删除）
///   卷  ：＋（本卷新增章节）· ⋯（详情 / 检查 / 删除）
///   章  ：⋯（细纲 / 内容 / 删除）
/// 检查分节内另有「▶ 运行」按钮（云端设定检查）；删除一律走宿主事件
/// （<c>OnDeleteNovel</c> / <c>OnDeleteVolume</c> / <c>OnDeleteChapter</c>）→ 确认弹窗，防误删。
///
/// 结构数据仍来自共享行模型 <see cref="OutlineRows.Build"/>（与 Desktop 同源：过滤/排序/摘要），
/// 本文件只做「行 → 树节点」映射；展开/选中/滚动交给 TreeView。
/// 章节点始终物化（模型的展开过滤关闭）——展开纯粹是控件行为，展开/折叠无需重建。
/// </summary>
public sealed partial class SetUpPanel
{
    /// <summary>大纲树控件。</summary>
    private readonly TreeView _outlineTree;

    /// <summary>卷 id → 树节点（重建时回收展开态 / reveal 定位用）。</summary>
    private readonly Dictionary<string, TreeNode> _volumeNodes = [];

    /// <summary>章节 id → 树节点（reveal 定位用）。</summary>
    private readonly Dictionary<string, TreeNode> _chapterNodes = [];

    /// <summary>小说根节点（展开态回收用）。</summary>
    private TreeNode? _novelNode;

    /// <summary>上次写到树上的展开态（用于区分「用户改的」与「我们设的」，见 <see cref="CaptureExpansionState"/>）。</summary>
    private bool _appliedNovelExpanded = true;

    private readonly Dictionary<string, bool> _appliedVolumeExpansion = [];

    /// <summary>
    /// 重建大纲树：根 = 当前小说，子项 = 书级摘要 / 检查结果 / 卷节点（卷下是章）。
    /// 内容未变时跳过（指纹含行序列 + 展开态 + 书级摘要 + 检查开关），避免重建把滚动/选中打回原位。
    /// </summary>
    private void RenderOutline(bool ensureNovelRootExpanded = false)
    {
        var structure = _structure;
        if (structure is null)
            return;

        // 重建前回收展开态（树控件自己维护 IsExpanded；重建节点会丢，需按 id 回填）
        CaptureExpansionState();

        // 总纲/检查结果是小说节点的子项；打开子项时，父节点必须同时可见。
        if (ensureNovelRootExpanded)
        {
            _novelExpanded = true;
            if (_novelNode is { } currentNovelNode)
            {
                currentNovelNode.IsExpanded = true;
                _appliedNovelExpanded = true;
            }
        }

        // 模型层「不过滤展开」：所有卷的章行都物化 → 展开/折叠纯属控件行为，无需重建；
        // 分页窗口关闭（TreeView 只渲染可视行，千章也走可视裁剪）。
        var allGroups = new HashSet<string>(StringComparer.Ordinal) { OutlineRows.UnassignedKey };
        foreach (var volume in structure.Volumes)
            allGroups.Add(volume.Id);
        var state = new OutlineViewState(
            allGroups,
            ExpandedChapters: null,
            _chapterFilter.ScopeVolumeId,
            _chapterFilter.KeywordText,
            _chapterFilter.Status,
            int.MaxValue,
            LoadedRows: null,
            RevealVolumeId: null,
            RevealChapterId: null,
            NovelExpanded: true
        );

        var result = OutlineRows.Build(
            structure,
            state,
            _plotlineNames,
            _characterNames,
            showOntology: false,
            showCheckResult: _lastCheckResult is not null
        );
        _bookSummary = result.Summary;
        _visibleChapterCount = result.Rows.Count(r => r.Kind == OutlineRowKind.Chapter);
        // 筛选命中数变了 → 工具带占位文案跟着变（仅大纲分区显示筛选语义）
        if (_sectionIndex == OutlineSectionIndex)
            ApplySectionChrome();

        var signature = OutlineRows.Signature(result.Rows) + ExtraOutlineStateFingerprint();
        if (string.Equals(signature, _outlineSignature, StringComparison.Ordinal))
        {
            ApplyPendingReveal();
            return;
        }
        _outlineSignature = signature;

        _volumeNodes.Clear();
        _chapterNodes.Clear();
        _outlineTree.Roots.Clear();

        var novelRow = result.Rows.FirstOrDefault(r => r.Kind == OutlineRowKind.Novel);
        if (novelRow is null)
            return;

        var novelNode = BuildNovelNode(novelRow);
        _novelNode = novelNode;
        AppendBookSections(novelNode, novelRow);

        TreeNode? group = null;
        foreach (var row in result.Rows)
        {
            switch (row.Kind)
            {
                case OutlineRowKind.VolumeHeader:
                    group = BuildVolumeNode(row);
                    novelNode.Children.Add(group);
                    break;
                case OutlineRowKind.UnassignedHeader:
                    group = BuildUnassignedNode(row);
                    novelNode.Children.Add(group);
                    break;
                case OutlineRowKind.Chapter:
                    if (group is not null)
                        group.Children.Add(BuildChapterNode(row));
                    break;
                case OutlineRowKind.Empty:
                    if (group is not null)
                        group.Children.Add(
                            new TreeNode(
                                new Markup($"[dim]  {ToolCallRenderer.Escape(row.Title)}[/]")
                                {
                                    IsSelectable = false,
                                }
                            )
                        );
                    break;
            }
        }
        _outlineTree.Roots.Add(novelNode);

        ApplyPendingReveal();
    }

    /// <summary>重建前把树上的展开态回收进状态集合（id 维度，跨重建稳定）。</summary>
    private void CaptureExpansionState()
    {
        // 只回收「用户在树上改过」的节点：我们设置过的状态仍以集合为准（否则 reveal 刚设的展开会被旧节点覆盖）
        if (_novelNode is { } novel && novel.IsExpanded != _appliedNovelExpanded)
            _novelExpanded = novel.IsExpanded;
        foreach (var (id, node) in _volumeNodes)
        {
            if (node.IsExpanded == _appliedVolumeExpansion.GetValueOrDefault(id, false))
                continue;
            if (node.IsExpanded)
                _expandedVolumes.Add(id);
            else
                _expandedVolumes.Remove(id);
        }
    }

    /// <summary>把 reveal 目标展开 + 选中（TreeView 的 TrySelectNode 会同步滚动）。</summary>
    private void ApplyPendingReveal()
    {
        if (
            _revealChapterId is { Length: > 0 } chapterId
            && _chapterNodes.TryGetValue(chapterId, out var chapterNode)
        )
        {
            if (chapterNode.Parent is { } parent)
                parent.IsExpanded = true;
            _outlineTree.TrySelectNode(chapterNode);
        }
        else if (
            _revealVolumeId is { Length: > 0 } volumeId
            && _volumeNodes.TryGetValue(volumeId, out var volumeNode)
        )
        {
            volumeNode.IsExpanded = true;
            _outlineTree.TrySelectNode(volumeNode);
        }
        _revealVolumeId = null;
        _revealChapterId = null;
    }

    /// <summary>根节点书级摘要行与可独立展开的检查结果分节。</summary>
    private void AppendBookSections(TreeNode novelNode, OutlineRow row)
    {
        var escape = ToolCallRenderer.Escape;
        var summary = _bookSummary;
        var counts =
            $"{summary.VolumeCount} 卷 · {summary.ChapterCount} 章"
            + (summary.DoneCount > 0 ? $" · 发布 {summary.DoneCount}" : "");
        novelNode.Children.Add(
            new TreeNode(
                new Markup($"[dim]总纲：{escape(ChromeText.ForTitle(row.SummaryLine, 16))}[/]")
                {
                    IsSelectable = false,
                }
            )
        );
        novelNode.Children.Add(
            new TreeNode(new Markup($"[dim]{counts}[/]") { IsSelectable = false })
        );
        if (row.MetaLine.Length > 0)
            novelNode.Children.Add(
                new TreeNode(
                    new Markup($"[dim]{escape(ChromeText.ForTitle(row.MetaLine, 20))}[/]")
                    {
                        IsSelectable = false,
                    }
                )
            );

        if (_checkResultsExpanded)
        {
            var checkNode = new TreeNode(new Markup("[dim]🔍 检查结果[/]") { IsSelectable = false })
            {
                IsExpanded = true,
            };
            // ▶ 运行：云端设定检查（点子菜单、落到分节内；novelId 空 = 未选小说 => 按钮禁用）
            checkNode.AddRightVisual(
                ActionButton(
                    "▶ 运行",
                    () =>
                    {
                        if (CurrentNovelId is { Length: > 0 } novelId)
                            OnCloudSetupCheck?.Invoke(novelId);
                    },
                    accent: true
                )
            );
            checkNode.AddRightVisual(MenuButton(row));
            foreach (var visual in BuildSetupCheckDetail())
            {
                if (visual is Markup markup)
                    checkNode.Children.Add(new TreeNode(markup));
            }
            novelNode.Children.Add(checkNode);
        }
    }

    /// <summary>小说根行只显示书名；书级摘要另作子行，避免窄侧栏裁掉总纲值；
    /// 右侧 ＋（新增分卷）/ ⋯（详情 / 检查 / 删除，书级操作全在溢出面）。</summary>
    private TreeNode BuildNovelNode(OutlineRow row)
    {
        var escape = ToolCallRenderer.Escape;
        // 先拼成普通字符串再交给 Markup：插值重载会把取值再转义一次（双层 [[[[）
        // 图标不进文本：由 IconResolver 按行类型放进框架的节点图标槽（一行一个图标）
        var headerText = $"[bold]{escape(row.Title)}[/]";
        var header = new Markup(headerText) { IsSelectable = false };

        var node = new TreeNode(header) { IsExpanded = _novelExpanded, Data = row };
        _appliedNovelExpanded = _novelExpanded;
        // 行右侧只有两个动作：＋（新建分卷）与 ⋯（详情 / 检查 / 删除）——
        // 详情这类动作进菜单，不在行上堆图标（用户报障：图标抽象、小、辨认困难）
        node.AddRightVisual(
            ActionButton("＋", () => OnAddVolume?.Invoke(CurrentNovelId, null), accent: true)
        );
        node.AddRightVisual(MenuButton(row));
        return node;
    }

    /// <summary>卷节点：第N卷 · 名 · 计数（层级图标 📦 走图标槽）；
    /// 右侧 ＋（新增章节）/ ⋯（详情 · 检查 · 删除）。</summary>
    private TreeNode BuildVolumeNode(OutlineRow row)
    {
        var escape = ToolCallRenderer.Escape;
        var meta =
            $" [dim]({escape(row.MetaLine)}"
            + (row.ProgressLine.Length > 0 ? $" · {escape(row.ProgressLine)}" : "")
            + ")[/]";
        var headerText = $"[bold]{escape(row.Title)}[/]{meta}";
        var header = new Markup(headerText) { IsSelectable = false };
        var node = new TreeNode(header)
        {
            IsExpanded = _expandedVolumes.Contains(row.Id),
            Data = row,
        };
        _volumeNodes[row.Id] = node;
        _appliedVolumeExpansion[row.Id] = node.IsExpanded;
        node.AddRightVisual(
            ActionButton("＋", () => OnAddChapter?.Invoke(CurrentNovelId, row.Id), accent: true)
        );
        node.AddRightVisual(MenuButton(row));
        return node;
    }

    /// <summary>未分卷组头：未分卷 · 计数（层级图标 📄 走图标槽）；右侧只有 ＋（新建章节）。</summary>
    private TreeNode BuildUnassignedNode(OutlineRow row)
    {
        var escape = ToolCallRenderer.Escape;
        var headerText = $"[dim]{escape(row.Title)} · {escape(row.MetaLine)}[/]";
        var node = new TreeNode(new Markup(headerText) { IsSelectable = false })
        {
            IsExpanded = _expandedVolumes.Contains(row.GroupKey),
            Data = row,
        };
        // 必须登记进 _volumeNodes：CaptureExpansionState 只遍历这张表来回收展开态，
        // 漏登记 → 任何触发重建的操作（改章节状态 → 保存 → 刷新）都会把未分卷组缩回去
        // （用户报障：改完状态展开的卷自动缩进去了）。
        _volumeNodes[row.GroupKey] = node;
        _appliedVolumeExpansion[row.GroupKey] = node.IsExpanded;
        node.AddRightVisual(
            ActionButton("＋", () => OnAddChapter?.Invoke(CurrentNovelId, null), accent: true)
        );
        return node;
    }

    /// <summary>章节点：序号 + 章名 + 字数（状态图标 ⚪/🟡/🟢 走图标槽）；
    /// 右侧 ⋯（细纲 / 内容 / 删除）。</summary>
    private TreeNode BuildChapterNode(OutlineRow row)
    {
        var escape = ToolCallRenderer.Escape;
        var words = row.WordsText.Length > 0 ? $" [dim]· {escape(row.WordsText)}[/]" : "";
        var headerText = $"[dim]{row.OrderText}[/]  [bold]{escape(row.Name)}[/]{words}";
        var header = new Markup(headerText) { IsSelectable = false };
        var node = new TreeNode(header) { Data = row };
        // 章节点没有下一层：右侧只留 ⋯（细纲 / 内容 / 删除）
        node.AddRightVisual(MenuButton(row));
        _chapterNodes[row.Id] = node;
        return node;
    }

    /// <summary>
    /// 层级图标（框架的节点图标槽；一行一个图标）：
    /// 小说 📖 / 卷 📦 / 未分卷组头 📄 / 章 = 状态圆点（⚪ 草稿 · 🟡 审核 · 🟢 终稿）。
    /// 图标由行类型唯一决定，不再往标题文本里拼 emoji（也就不会再和框架默认图标叠成两个）。
    /// </summary>
    internal static Rune OutlineIcon(OutlineRow row) =>
        row.Kind switch
        {
            OutlineRowKind.Novel => new Rune(0x1F4D6), // 📖
            // 卷 / 未分卷都是「装章节的容器」→ 统一 folder 图标（用户报障：卷该用 folder）
            OutlineRowKind.VolumeHeader => new Rune(0x1F4C1), // 📁
            OutlineRowKind.UnassignedHeader => new Rune(0x1F4C1), // 📁
            // 章：按发布状态分图标（📢 已发布 / 📝 草稿 / 🟡 审核 / 🟢 终稿）
            OutlineRowKind.Chapter => RuneOf(row.StatusIcon),
            _ => new Rune(' '),
        };

    /// <summary>取字符串首字形（空 / 非法 → 空格）。</summary>
    private static Rune RuneOf(string text) =>
        !string.IsNullOrEmpty(text) && Rune.TryGetRuneAt(text, 0, out var rune)
            ? rune
            : new Rune(' ');

    /// <summary>节点动作按钮（Always 可见；危险操作用 danger 色，新增用强调色）。</summary>
    private Button ActionButton(
        string label,
        Action action,
        bool accent = false,
        bool danger = false
    )
    {
        var style =
            danger ? ThemeStyles.BuildButtonDanger(_colors)
            : accent ? ThemeStyles.BuildButtonAccent(_colors)
            : ThemeStyles.BuildButton(_colors);
        var button = new Button(new Markup(label)).Style(style);
        button.Click(action);
        return button;
    }

    /// <summary>节点 ⋯ 菜单按钮（弹出该层级的操作菜单，复用 AnchoredPopup 生命周期）。</summary>
    private Button MenuButton(OutlineRow row)
    {
        var button = new Button(new Markup("⋯")).Style(ThemeStyles.BuildButton(_colors));
        button.Click(() => ShowOutlineRowMenu(row, button));
        return button;
    }
}
