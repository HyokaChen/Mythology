using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Core.Data;
using Core.Evolution;
using Core.Novels;
using Desktop.Controls;
using Desktop.Models;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using OutlineNodeVm = Desktop.ViewModels.OutlineNodeVm;

namespace Desktop.Views;

public partial class SetUpPanelView : UserControl
{
    /// <summary>分区序号 → 实体类型（唯一真源 = VM.SectionTypes；Chapter = 大纲分区，卷/章在大纲内编辑）。</summary>
    private static EntityType[] SectionTypes => ViewModels.SetUpPanelViewModel.SectionTypes;

    private EntityType CurrentTabType =>
        SectionTypes[Math.Clamp(MainTabControl.SelectedIndex, 0, SectionTypes.Length - 1)];

    public SetUpPanelView()
    {
        InitializeComponent();
        // 分区切换 → 回写 VM（搜索框占位文案按分区切换）
        MainTabControl.SelectionChanged += (_, _) =>
        {
            if (DataContext is ViewModels.SetUpPanelViewModel vm)
                vm.ActiveSection = MainTabControl.SelectedIndex;
        };
        // 附加到可视化树即自加载：不依赖外部调用方时序，避免“永久空白”
        Loaded += OnLoaded;
    }

    /// <summary>附加后刷新面板（幂等：无当前小说时清空并返回）。</summary>
    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
        {
            // 新建行出现 → 尽力滚动到可见（虚拟化容器未实现时静默跳过）
            vm.OutlineRows.CollectionChanged -= OutlineRows_OnCollectionChanged;
            vm.OutlineRows.CollectionChanged += OutlineRows_OnCollectionChanged;
            vm.ActiveSection = MainTabControl.SelectedIndex; // 初始分区（大纲在首位）
            await vm.RefreshAsync();
            ScrollToNewlyCreatedRow();
        }
    }

    /// <summary>行集合变化 → 若出现「刚创建」行则滚动到可见。</summary>
    private void OutlineRows_OnCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    ) => ScrollToNewlyCreatedRow();

    /// <summary>
    /// 尽力滚动到刚创建的行（虚拟化容器可能未实现 → 静默跳过；
    /// 保底 = 已展开所在分组 + Amber 包边高亮，新行至少在其结构位置可见）。
    /// </summary>
    private void ScrollToNewlyCreatedRow()
    {
        if (DataContext is not ViewModels.SetUpPanelViewModel { OutlineRows: { } rows })
            return;

        var index = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsNewlyCreated)
                continue;
            index = i;
            break;
        }
        if (index < 0)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => ScrollToRow(index),
            Avalonia.Threading.DispatcherPriority.Background
        );
    }

    /// <summary>
    /// 定位到指定行（虚拟化下 <c>ContainerFromIndex</c> 对未实现行返回 null）：
    /// 已实现 → 直接 <c>BringIntoView</c>；未实现 → 先按「已实现行高均值」粗估偏移滚到附近，
    /// 下一帧该行被实现后再精确定位。两步都失败则静默放弃（保底仍有展开 + Amber 高亮）。
    /// </summary>
    private void ScrollToRow(int index)
    {
        if (
            this.GetVisualDescendants()
                .OfType<ItemsControl>()
                .FirstOrDefault(c => c.Name == "OutlineItems")
            is not { } items
        )
            return;
        if (items.ContainerFromIndex(index) is { } realized)
        {
            realized.BringIntoView();
            return;
        }

        var realizedHeight = 0.0;
        var realizedCount = 0;
        for (var i = 0; i < items.ItemCount; i++)
        {
            if (items.ContainerFromIndex(i) is not { } container)
                break;
            realizedHeight += container.Bounds.Height;
            realizedCount++;
        }
        if (realizedCount == 0 || items.FindAncestorOfType<ScrollViewer>() is not { } viewer)
            return;

        var estimated = index * (realizedHeight / realizedCount);
        viewer.Offset = new Avalonia.Vector(viewer.Offset.X, Math.Max(0, estimated));

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => items.ContainerFromIndex(index)?.BringIntoView(),
            Avalonia.Threading.DispatcherPriority.Background
        );
    }

    private string? CurrentNovelId => (DataContext as ViewModels.SetUpPanelViewModel)?.NovelId;

    /// <summary>以宿主窗口为所有者打开对话框（本控件是 UserControl，不在 Window 层级）。</summary>
    private void ShowDialog(Window dialog)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    /// <summary>宿主窗口（等待子对话框结果 / 消息框所有者；设计时可能为 null）。</summary>
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private INovelStore? Store => Program.Services?.GetService<INovelStore>();

    /// <summary>点击实体 → 打开编辑窗口（插入引用仅通过「...」菜单）。</summary>
    private void EntityItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
            return; // 操作按钮点击不触发
        if (sender is ContentControl { DataContext: EntityItem item })
        {
            OpenEditor(item.Id);
        }
    }

    /// <summary>⋯ 按钮 → 打开上下文菜单（SukiUI 容器）。</summary>
    private void EntityMore_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button)
            button.ContextMenu?.Open(button);
        e.Handled = true;
    }

    /// <summary>菜单：插入引用。</summary>
    private void EntityInsert_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: EntityItem item }
            && DataContext is ViewModels.SetUpPanelViewModel vm
        )
        {
            vm.RequestInsert(item);
        }
    }

    /// <summary>实体编辑按钮 → 打开编辑窗口。</summary>
    private void EntityEdit_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: EntityItem item })
            OpenEditor(item.Id);
        e.Handled = true;
    }

    /// <summary>本体关系按钮 → 打开本体关系窗（任何实体类型）。</summary>
    private void EntityRelations_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: EntityItem item }
            && Store is { } store
            && CurrentNovelId is { Length: > 0 } novelId
        )
        {
            var window = new EntityRelationsWindow(
                store,
                novelId,
                CurrentTabType,
                item.Id,
                item.Name,
                NavigateToEntity
            );
            ShowDialog(window);
        }
        e.Handled = true;
    }

    /// <summary>关系窗跳转：切到目标分区并打开其编辑窗（索引 = SectionTypes 顺序；分卷归大纲分区）。</summary>
    private void NavigateToEntity(EntityType type, string entityId)
    {
        if (type == EntityType.Volume)
        {
            // 卷没有独立分区（在大纲内）：切大纲 + 直接开卷编辑窗（此前误落到章节阅读器）
            var outlineIndex = Array.IndexOf(SectionTypes, EntityType.Chapter);
            if (outlineIndex >= 0)
                MainTabControl.SelectedIndex = outlineIndex;
            OpenVolumeEditor(entityId);
            return;
        }
        var tabIndex = Array.IndexOf(SectionTypes, type);
        if (tabIndex >= 0)
            MainTabControl.SelectedIndex = tabIndex;
        OpenEditor(entityId);
    }

    /// <summary>「小说信息」→ 打开小说信息编辑器（含总纲字段；关闭后刷新面板）。</summary>
    private void OnNovelInfo_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Store is { } store && CurrentNovelId is { Length: > 0 } novelId)
        {
            var window = new EntityEditWindow(store, EntityType.Novel, novelId, novelId);
            window.Closed += async (_, _) => await RefreshPanelAsync();
            ShowDialog(window);
        }
    }

    /// <summary>「检查」→ 打开设定检查窗（深链跳转复用 NavigateToEntity）。</summary>
    private void OnSetupCheck_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
            ShowDialog(new SetupCheckWindow(vm, NavigateToEntity));
    }

    /// <summary>书级「删除」→ 确认后删除整本小说（含全部卷/章/实体；章节按小说目录整桶删除）。</summary>
    private void NovelDelete_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (
            sender is not MenuItem { DataContext: OutlineRow row }
            || Store is not { } store
            || CurrentNovelId is not { Length: > 0 } novelId
        )
            return;

        var dialog = new ConfirmDeleteWindow(
            "小说",
            row.Title,
            async () =>
            {
                await store.DeleteNovel(novelId);
                if (DataContext is ViewModels.SetUpPanelViewModel vm)
                    vm.NotifyNovelDeleted(novelId);
            }
        );
        ShowDialog(dialog);
    }

    /// <summary>「📦 分卷」→ 新建分卷（快速窗口：只填卷名）。</summary>
    private void AddVolume_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CurrentNovelId is not { Length: > 0 })
            return;
        ShowDialog(new VolumeQuickCreateWindow(name => _ = CreateVolumeAsync(name)));
    }

    /// <summary>新建分卷（只填卷名；卷纲 / 目标等重字段走 ⋯ → 查看 / 编辑）。</summary>
    private async Task CreateVolumeAsync(string name)
    {
        if (Store is not { } store || CurrentNovelId is not { Length: > 0 } novelId)
            return;

        var volume = new Volume
        {
            Id = Ulid.NewUlid().ToString(),
            NovelId = novelId,
            Name = name,
            Order = ChapterOrdering.NextVolumeOrder(await store.GetVolumes(novelId)),
        };
        await SaveVolumeAsync(volume);
        await RefreshPanelAsync();
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
        {
            vm.RevealVolume(volume.Id);
            ScrollToNewlyCreatedRow();
        }
    }

    /// <summary>新建章节（只填标题 + 所属卷；细纲 / 正文后补）。</summary>
    private async Task CreateChapterAsync(
        string title,
        string? volumeId,
        string? afterChapterId = null
    )
    {
        if (Store is not { } store || CurrentNovelId is not { Length: > 0 } novelId)
            return;

        var chapters = await store.GetChapters(novelId);
        var plan = ChapterOrdering.PlanInsert(chapters, afterChapterId, volumeId);
        var order = plan.Order;

        // ③ 该处无可用空号 → 先全章重排腾出位置（新章号由哨兵行预留）。
        if (plan.RequiresReorder)
        {
            var assignments = ChapterOrdering.PlanFullReorder(
                chapters,
                afterChapterId,
                ChapterOrdering.GroupKey(volumeId)
            );
            order = assignments.First(a => a.ChapterId == ChapterOrdering.InsertSlotId).Order;
            await store.ReorderAllChapters(novelId, assignments);
        }

        var chapter = new Chapter
        {
            Id = Ulid.NewUlid().ToString(),
            NovelId = novelId,
            Title = string.IsNullOrWhiteSpace(title) ? "新章节" : title.Trim(),
            Order = order,
            Status = ChapterStatus.Draft,
            VolumeId = string.IsNullOrEmpty(volumeId) ? null : volumeId,
        };
        await SaveChapterAsync(chapter);
        await RefreshPanelAsync();
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
        {
            vm.RevealChapter(chapter.Id);
            ScrollToNewlyCreatedRow();
        }
    }

    /// <summary>打开新建章节编辑窗（presetVolumeId 决定预选所属卷）。</summary>
    private void OpenNewChapterEditor(string? presetVolumeId) =>
        _ = OpenNewChapterEditorAsync(presetVolumeId);

    /// <summary>
    /// 打开新建章节编辑窗。
    /// </summary>
    /// <param name="presetVolumeId">预选所属卷。</param>
    /// <param name="afterChapterId">插入到该章之后（null → 追加到预选卷末尾）。</param>
    private async Task OpenNewChapterEditorAsync(
        string? presetVolumeId,
        string? afterChapterId = null
    )
    {
        if (Store is not { } store || CurrentNovelId is not { Length: > 0 } novelId)
            return;
        var volumes = ChapterOrdering.SortVolumes(await store.GetVolumes(novelId));
        ShowDialog(
            new ChapterQuickCreateWindow(
                volumes,
                presetVolumeId,
                (title, volumeId) => _ = CreateChapterAsync(title, volumeId, afterChapterId)
            )
        );
    }

    /// <summary>行首折叠箭头 → 展开 / 折叠该行（卷/未分卷组/前言块）。</summary>
    private void OutlineRowToggle_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (
            sender is Button { DataContext: OutlineRow row }
            && DataContext is ViewModels.SetUpPanelViewModel vm
        )
            vm.ToggleOutlineRow(row);
    }

    /// <summary>
    /// 行首（标题/卡片）点击：章行 → 打开只读正文（最高频行为）；卷 / 组头 / 前言块 → 切换展开。
    /// 展开 / 折叠始终另有独立 caret 命中区，避免单击语义过载。
    /// </summary>
    private void OutlineRow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
            return;
        if (sender is not Border { DataContext: OutlineRow row })
            return;

        if (row.Kind == OutlineRowKind.Chapter)
            OpenChapterReader(row.Id);
        else if (DataContext is ViewModels.SetUpPanelViewModel vm)
            vm.ToggleOutlineRow(row);
    }

    /// <summary>打开只读章节阅读器（列表永不渲染正文）。</summary>
    private void OpenChapterReader(string chapterId)
    {
        if (Store is { } store && CurrentNovelId is { Length: > 0 } novelId)
            ShowDialog(new ChapterReaderWindow(store, novelId, chapterId));
    }

    /// <summary>卷头 ⋯ → 打开上下文菜单。</summary>
    private void VolumeRowMore_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button)
            button.ContextMenu?.Open(button);
        e.Handled = true;
    }

    /// <summary>章行「⋯ → 细纲」→ 章节细纲编辑窗（章序/状态/所属卷 + 细纲规划，不含正文）。</summary>
    private void ChapterOutline_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: OutlineRow row }
            && Store is { } store
            && CurrentNovelId is { Length: > 0 } novelId
        )
        {
            var window = new EntityEditWindow(
                store,
                EntityType.Chapter,
                novelId,
                row.Id,
                chapterScope: ChapterEditScope.Outline
            );
            window.Closed += async (_, _) => await RefreshPanelAsync();
            ShowDialog(window);
        }
        e.Handled = true;
    }

    /// <summary>章行「⋯ → 内容」→ 章节正文编辑窗（只含正文字段）。</summary>
    private void ChapterContent_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: OutlineRow row }
            && Store is { } store
            && CurrentNovelId is { Length: > 0 } novelId
        )
        {
            var window = new EntityEditWindow(
                store,
                EntityType.Chapter,
                novelId,
                row.Id,
                chapterScope: ChapterEditScope.Content
            );
            window.Closed += async (_, _) => await RefreshPanelAsync();
            ShowDialog(window);
        }
        e.Handled = true;
    }

    private void VolumeGroupEdit_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OutlineRow row })
            OpenVolumeEditor(row.Id);
        e.Handled = true;
    }

    /// <summary>卷头删除 → 确认后删除（级联清空章节归属）并刷新。</summary>
    private void VolumeGroupDelete_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: OutlineRow row }
            && Store is { } store
            && CurrentNovelId is { Length: > 0 } novelId
        )
        {
            var dialog = new ConfirmDeleteWindow(
                "分卷",
                row.Title,
                async () =>
                {
                    await store.DeleteVolume(novelId, row.Id);
                    if (DataContext is ViewModels.SetUpPanelViewModel vm)
                        await vm.RefreshAsync();
                }
            );
            ShowDialog(dialog);
        }
        e.Handled = true;
    }

    private void OpenVolumeEditor(string volumeId)
    {
        if (Store is { } store && CurrentNovelId is { Length: > 0 } novelId)
        {
            var window = new EntityEditWindow(store, EntityType.Volume, novelId, volumeId);
            window.Closed += async (_, _) =>
            {
                if (DataContext is ViewModels.SetUpPanelViewModel vm)
                    await vm.RefreshAsync();
            };
            ShowDialog(window);
        }
    }

    /// <summary>实体删除按钮 → 确认后删除并刷新。</summary>
    private void EntityDelete_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: EntityItem item } && Store is { } store)
        {
            var type = CurrentTabType;
            var dialog = new ConfirmDeleteWindow(
                EntityFieldRegistry.Label(type),
                item.Name,
                async () =>
                {
                    await DeleteEntityAsync(store, CurrentNovelId ?? "", type, item.Id);
                    if (DataContext is ViewModels.SetUpPanelViewModel vm)
                        await vm.RefreshAsync();
                }
            );
            ShowDialog(dialog);
        }
        e.Handled = true;
    }

    /// <summary>Tab 顶部「＋ 添加」按钮 → 新建当前类型实体。</summary>
    private void AddEntity_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Store is { } store && CurrentNovelId is { Length: > 0 } novelId)
        {
            var window = new EntityEditWindow(store, CurrentTabType, novelId, null);
            window.Closed += async (_, _) =>
            {
                if (DataContext is ViewModels.SetUpPanelViewModel vm)
                    await vm.RefreshAsync();
            };
            ShowDialog(window);
        }
    }

    /// <summary>打开实体编辑窗口（编辑模式）；Chapter 类型走章节正文读写窗。</summary>
    private void OpenEditor(string entityId)
    {
        if (Store is { } store && CurrentNovelId is { Length: > 0 } novelId)
        {
            if (CurrentTabType == EntityType.Chapter)
            {
                var reader = new ChapterReaderWindow(store, novelId, entityId);
                ShowDialog(reader);
                return;
            }
            if (CurrentTabType == EntityType.Volume)
            {
                OpenVolumeEditor(entityId);
                return;
            }
            var window = new EntityEditWindow(store, CurrentTabType, novelId, entityId);
            window.Closed += async (_, _) =>
            {
                if (DataContext is ViewModels.SetUpPanelViewModel vm)
                    await vm.RefreshAsync();
            };
            ShowDialog(window);
        }
    }

    // ══════════ 大纲三层：卷 → 章 管理（新建 / 改名 / 换序 / 移动 / 删除） ══════════

    /// <summary>章节 / 分卷手动写入（跳过 AI 冲突门禁，与 EntityEditWindow.SaveEntityAsync 一致）；只写不刷新。</summary>
    private async Task SaveChapterAsync(Chapter chapter)
    {
        if (Store is not { } store)
            return;
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        if (store is FileNovelStore fns)
        {
            using (fns.BeginManualWrite())
                await store.SaveChapter(chapter);
        }
        else
        {
            await store.SaveChapter(chapter);
        }
    }

    private async Task SaveVolumeAsync(Volume volume)
    {
        if (Store is not { } store)
            return;
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        if (store is FileNovelStore fns)
        {
            using (fns.BeginManualWrite())
                await store.SaveVolume(volume);
        }
        else
        {
            await store.SaveVolume(volume);
        }
    }

    /// <summary>刷新面板（写操作末尾统一调一次）。</summary>
    private async Task RefreshPanelAsync()
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
            await vm.RefreshAsync();
    }

    /// <summary>卷头「＋ → 新建章节」→ 行内输入条（落在该卷；未分卷组头 → 未分卷）。</summary>
    private void AddChapterToVolume_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        var row = sender switch
        {
            Button { DataContext: OutlineRow b } => b,
            MenuItem { DataContext: OutlineRow m } => m,
            _ => null,
        };
        if (row is not null && DataContext is ViewModels.SetUpPanelViewModel vm)
            OpenInlineCreateBar(vm, row.GroupKey);
    }

    /// <summary>空态行「＋ 新建章节」→ 行内输入条（落在该空卷；未分卷/全书空态 → 未分卷组）。</summary>
    private void EmptyRowAddChapter_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        e.Handled = true;
        if (
            sender is Button { DataContext: OutlineRow row }
            && row.Kind == OutlineRowKind.Empty
            && DataContext is ViewModels.SetUpPanelViewModel vm
        )
        {
            OpenInlineCreateBar(vm, row.GroupKey);
            return;
        }
        OpenNewChapterEditor(null);
    }

    /// <summary>
    /// 打开行内新建章节输入条（Enter 连打；不弹窗）。目标分组键缺失时退回轻量对话框。
    /// </summary>
    private void OpenInlineCreateBar(ViewModels.SetUpPanelViewModel vm, string groupKey)
    {
        if (groupKey is not { Length: > 0 })
        {
            OpenNewChapterEditor(null);
            return;
        }
        // 切换分组时清空未提交草稿；同组重开保留草稿（与 TUI 口径一致）。
        if (
            FindInlineCreateBox() is { } box
            && !string.Equals(vm.InlineCreateGroupKey, groupKey, StringComparison.Ordinal)
        )
            box.Text = "";
        vm.OpenInlineCreate(groupKey);
        // 输入条从隐藏转可见后再聚焦（同组重开时焦点已在，重复调用无副作用）。
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => FindInlineCreateBox()?.Focus(),
            Avalonia.Threading.DispatcherPriority.Background
        );
    }

    /// <summary>
    /// 行内输入条实例：模板内容里的 <c>x:Name</c> 不生成字段，
    /// 按本文件既有约定从可视树按 Name 查找（同 <c>OutlineItems</c>）。
    /// </summary>
    private TextBox? FindInlineCreateBox() =>
        this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "InlineCreateBox");

    /// <summary>行内输入条键盘：Enter 新建（清空续打）· Esc 收起。</summary>
    private async void InlineCreateBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || DataContext is not ViewModels.SetUpPanelViewModel vm)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CloseInlineCreate();
            return;
        }
        if (e.Key != Key.Enter || vm.InlineCreateGroupKey is not { Length: > 0 } groupKey)
            return;
        e.Handled = true;
        var title = box.Text?.Trim() ?? "";
        box.Text = "";
        await CreateChapterAsync(
            title,
            string.Equals(groupKey, OutlineRows.UnassignedKey, StringComparison.Ordinal)
                ? null
                : groupKey
        );
        box.Focus(); // 连打：刷新面板后焦点留在输入条
    }

    /// <summary>章节卡 ⋯ → 打开上下文菜单。</summary>
    private void ChapterMore_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button)
            button.ContextMenu?.Open(button);
        e.Handled = true;
    }

    /// <summary>「删除」章节 → 确认后删除（已发布章先按变更集撤回）并刷新。</summary>
    private void ChapterDelete_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is MenuItem { DataContext: OutlineRow row }
            && Store is { } store
            && CurrentNovelId is { Length: > 0 } novelId
        )
        {
            var dialog = new ConfirmDeleteWindow(
                EntityFieldRegistry.Label(EntityType.Chapter),
                row.Title,
                async () =>
                {
                    // 已发布章必须先撤回（回滚实体与变更集）再删除：只能走生命周期服务
                    var result = await new ChapterLifecycleService(store).DeleteChapterAsync(
                        novelId,
                        row.Id
                    );
                    if (!result.Success)
                    {
                        if (OwnerWindow is { } owner)
                            await new MessageBox(
                                "删除失败",
                                result.Error ?? "删除章节失败"
                            ).ShowDialogAsync(owner);
                        return;
                    }
                    await RefreshPanelAsync();
                }
            );
            ShowDialog(dialog);
        }
        e.Handled = true;
    }

    /// <summary>章节「发布」→ 发布预览（检测死亡 / 联动建议可勾选）→ 生命周期服务发布。</summary>
    private async void ChapterPublish_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        e.Handled = true;
        if (
            sender is not MenuItem { DataContext: OutlineRow row }
            || Store is not { } store
            || CurrentNovelId is not { Length: > 0 } novelId
            || OwnerWindow is not { } owner
        )
            return;

        var service = new ChapterLifecycleService(store);
        var preview = await service.PreviewPublishAsync(novelId, row.Id);
        var nameOf = DataContext is ViewModels.SetUpPanelViewModel vm
            ? vm.NameOfEntity
            : (Func<EntityType, string, string>?)null;
        var window = new ChapterPublishWindow(preview, nameOf);
        var options = await window.ShowAsync(owner);
        if (preview.HasBlockingIssues)
        {
            await new MessageBox(
                "无法发布",
                string.Join(
                    "\n",
                    preview
                        .ValidationIssues.Where(i => i.BlocksPublish)
                        .Take(5)
                        .Select(i => i.Message)
                )
            ).ShowDialogAsync(owner);
            return;
        }
        if (options is null)
            return; // 取消：保持草稿
        var result = await service.PublishAsync(novelId, row.Id, options);
        if (!result.Success)
        {
            await new MessageBox("发布失败", result.Error ?? "发布失败").ShowDialogAsync(owner);
            return;
        }
        await RefreshPanelAsync();
    }

    /// <summary>章节「撤回」→ 撤回预览（后续已发布章 / 冲突只读）→ 生命周期服务撤回。</summary>
    private async void ChapterUnpublish_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        e.Handled = true;
        if (
            sender is not MenuItem { DataContext: OutlineRow row }
            || Store is not { } store
            || CurrentNovelId is not { Length: > 0 } novelId
            || OwnerWindow is not { } owner
        )
            return;

        var service = new ChapterLifecycleService(store);
        var preview = await service.PreviewUnpublishAsync(novelId, row.Id);
        var window = new ChapterUnpublishWindow(row.Title, preview);
        var confirmed = await window.ShowAsync(owner);
        if (!preview.CanUnpublish)
        {
            await new MessageBox(
                "无法撤回",
                "后续章节已发布或实体在发布后被修改，请按提示处理后重试。"
                    + "\n"
                    + ChapterUnpublishWindow.DescribeBlockers(preview)
            ).ShowDialogAsync(owner);
            return;
        }
        if (!confirmed)
            return; // 取消：保持发布
        var result = await service.UnpublishAsync(novelId, row.Id);
        if (!result.Success)
        {
            await new MessageBox("撤回失败", result.Error ?? "撤回失败").ShowDialogAsync(owner);
            return;
        }
        await RefreshPanelAsync();
    }

    /// <summary>节点「时间线」（书 / 卷 / 章）→ 实体时间线 Markdown 窗口。</summary>
    private async void OutlineTimeline_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        e.Handled = true;
        if (
            sender is not MenuItem { DataContext: OutlineRow row }
            || DataContext is not ViewModels.SetUpPanelViewModel vm
        )
            return;
        if (OutlineEntityType(row.Kind) is not { } type)
            return;

        var markdown = await vm.BuildAuditMarkdownAsync(type, row.Id);
        ShowDialog(new MarkdownDialog("时间线 · " + row.Title, markdown));
    }

    /// <summary>大纲行 → 本体实体类型（无对应实体的行返回 null）。</summary>
    private static EntityType? OutlineEntityType(OutlineRowKind kind) =>
        kind switch
        {
            OutlineRowKind.Novel => EntityType.Novel,
            OutlineRowKind.VolumeHeader => EntityType.Volume,
            OutlineRowKind.Chapter => EntityType.Chapter,
            _ => null,
        };

    // ══════════ 面板内云端同步 ══════════

    /// <summary>面板内「清除」→ 清空模糊搜索（实体列表与大纲恢复全量）。</summary>
    private void ClearSearch_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
            vm.SearchText = "";
        e.Handled = true;
    }

    /// <summary>
    /// 元信息行「筛选」→ 打开当前类型的筛选对话框（1~2 个维度；选项由 VM 按类型与当前数据构建，
    /// 与编辑窗字段同源）；确认 / 清除后由 VM 重填列表并刷新占位里的命中数。
    /// </summary>
    private void FilterEntity_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ViewModels.SetUpPanelViewModel vm)
            return;
        var type = CurrentTabType;
        var (primary, secondary) = vm.BuildEntityFilterFields(type);
        if (primary is null)
            return;
        var dialog = new EntityFilterDialog(
            EntityFieldRegistry.Label(type),
            primary,
            secondary,
            vm.GetEntityFilter(type),
            filter => vm.ApplyEntityFilter(type, filter)
        );
        ShowDialog(dialog);
    }

    // ── 大纲树节点动作（行上只有 ＋（书→分卷 / 卷→章节）与 ⋯ 菜单：详情/检查/删除）──

    /// <summary>节点「＋」：小说 → 新建分卷；卷/未分卷组 → 该分组新建章节（行内输入条）。</summary>
    private void OutlineNodeAdd_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: OutlineNodeVm node })
            return;
        switch (node.Kind)
        {
            case OutlineRowKind.Novel:
                AddVolume_OnClick(sender, e);
                break;
            case OutlineRowKind.VolumeHeader:
            case OutlineRowKind.UnassignedHeader:
                if (DataContext is ViewModels.SetUpPanelViewModel vm)
                    OpenInlineCreateBar(vm, node.Row.GroupKey);
                break;
        }
    }

    /// <summary>
    /// 节点「⋯」→ 打开上下文菜单（菜单 DataContext = 该节点原始行，复用既有菜单处理器）。
    /// 发布 / 撤回按章状态显隐（菜单项 Tag 标记，XAML 内不绑状态以免与行 DataContext 冲突）。
    /// </summary>
    private void OutlineNodeMore_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            if (button.ContextMenu is { } menu && button.DataContext is OutlineNodeVm node)
            {
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    switch (item.Tag as string)
                    {
                        case "publish":
                            item.IsVisible = node.IsDraft;
                            break;
                        case "unpublish":
                            item.IsVisible = node.IsPublished;
                            break;
                    }
                }
            }
            button.ContextMenu?.Open(button);
        }
        e.Handled = true;
    }

    /// <summary>面板内「⇅ 同步」→ 双向同步当前小说（结果由 MainWindowViewModel 回填状态行）。</summary>
    private void CloudSync_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
            vm.RequestSync(pullOnly: false);
        e.Handled = true;
    }

    /// <summary>面板内「⬇ 拉取」→ 仅拉取当前小说。</summary>
    private void CloudPull_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SetUpPanelViewModel vm)
            vm.RequestSync(pullOnly: true);
        e.Handled = true;
    }

    /// <summary>按类型删除实体。</summary>
    private static async Task DeleteEntityAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string id
    )
    {
        switch (type)
        {
            case EntityType.Character:
                await store.DeleteCharacter(id);
                break;
            case EntityType.Faction:
                await store.DeleteFaction(id);
                break;
            case EntityType.Location:
                await store.DeleteLocation(id);
                break;
            case EntityType.Plotline:
                await store.DeletePlotline(id);
                break;
            case EntityType.Scene:
                await store.DeleteScene(id);
                break;
            case EntityType.Item:
                await store.DeleteItem(id);
                break;
            case EntityType.Relationship:
                await store.DeleteRelationship(id);
                break;
            // 章节删除走 ChapterLifecycleService（已发布章需先按变更集撤回），不在此旁路
            // 分卷走 novelId 重载：级联清空章节 VolumeId
            case EntityType.Volume:
                await store.DeleteVolume(novelId, id);
                break;
        }
    }

    /// <summary>📜 历史按钮 → 查看实体时间线（状态变化 + 本体动作 + 旧字段审计）。</summary>
    private async void EntityHistory_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (
            sender is MenuItem { DataContext: EntityItem item }
            && DataContext is ViewModels.SetUpPanelViewModel vm
        )
        {
            var md = await vm.BuildAuditMarkdownAsync(CurrentTabType, item.Id);
            var window = new MarkdownDialog("时间线 · " + item.Name, md);
            ShowDialog(window);
        }
        e.Handled = true;
    }
}
