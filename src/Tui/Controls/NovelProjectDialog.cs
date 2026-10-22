using Core.Data;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 小说项目对话框 — 项目选择器 + 新建项目表单。
/// 流程: 项目选择器 → 会话选择器 → 新建项目表单。
/// 题材选择为两级级联：先选一级分类（如"玄幻"），再选二级分类（如"东方玄幻"）。
/// Novel.Genre 存储二级分类中文名。
/// </summary>
public sealed class NovelProjectDialog(
    INovelStore store, // 小说存储服务
    FileSessionStore sessionStore, // 会话存储服务
    Action<string, string?>? onOpenNovel = null // 打开小说回调（参数：novelId, sessionId）
)
{
    /// <summary>
    /// 项目目录清单（FileNovelStore 内部持有）：列表 = 各目录并集，Desktop 与 TUI 共用同一份
    /// ~/.mythology/workspaces.json。null = 非 FileNovelStore（测试桩），跳过目录管理。
    /// </summary>
    private readonly NovelWorkspace? _workspace = store is FileNovelStore fns
        ? fns.Workspace
        : null;

    /// <summary>一级分类列表（引用 GenreDefinitions）。</summary>
    private static readonly GenreDefinitions.Category[] Categories = GenreDefinitions.All;

    /// <summary>
    /// 显示小说项目对话框。
    /// </summary>
    public void Show()
    {
        // fire-and-forget: DispatcherSynchronizationContext 保证 await continuation 回 UI 线程
        _ = ShowAsync();
    }

    /// <summary>
    /// 异步显示小说项目对话框。
    /// </summary>
    private async Task ShowAsync()
    {
        try
        {
            var novels = await store.GetAllNovels();
            if (novels.Count == 0)
            {
                ShowNewForm();
                return;
            }
            ShowProjectPicker(novels);
        }
        catch (Exception ex)
        {
            ToastService.Error($"加载项目失败: {ex.Message}");
            ShowNewForm();
        }
    }

    // ── 项目选择器 ──

    /// <summary>
    /// 显示项目选择器。
    /// </summary>
    /// <param name="novels">小说列表。</param>
    private void ShowProjectPicker(List<Novel> novels)
    {
        // 首项为"新建项目"，次项为添加目录（清单管理入口），后续为最近更新的小说（最多 20 条）
        var recent = novels.OrderByDescending(n => n.UpdatedAt).Take(20).ToList();
        var items = new List<NovelItem> { new(null, "新建项目", "", 0, "", null) };
        if (_workspace is not null)
            items.Add(new NovelItem(AddDirectorySentinel, "添加小说目录…", "", 0, "", null));
        foreach (var n in recent)
        {
            var title = string.IsNullOrEmpty(n.Title) ? n.Id : n.Title;
            var genre = string.IsNullOrEmpty(n.Genre) ? "其他" : n.Genre;
            var ch = n.Chapters?.Count ?? 0;
            var date = n.UpdatedAt.ToString("MM-dd");
            // 书所在的项目目录（清单里可能不止一个）→ 角标，多目录时同名书可分辨
            var dir = _workspace is null
                ? null
                : Shorten(_workspace.FindRootForNovel(n.Id) ?? _workspace.ActiveRoot);
            items.Add(new NovelItem(n.Id, title, genre, ch, date, dir));
        }

        var list = new OptionList<NovelItem>()
            .Items(items)
            .ItemSearchText(item => item.Title)
            .ItemTemplate(
                new DataTemplate<NovelItem>(
                    Display: (value, in _) =>
                    {
                        var item = value.GetValue();
                        if (item.NovelId == AddDirectorySentinel)
                        {
                            return new Markup("[primary]📂 添加小说目录…[/]");
                        }
                        if (item.NovelId is null)
                        {
                            return new Markup("[bold primary]➕ 新建项目[/]");
                        }
                        // 下面插值进 new Markup($"…")：取值由插值处理器自动转义，勿再手工 Escape
                        var detail = item.Directory is null
                            ? $"{item.Genre} · {item.Chapters}章 · {item.Date}"
                            : $"{item.Genre} · {item.Chapters}章 · {item.Date} · {item.Directory}";
                        return new HStack(
                            new Markup($"[bold]{item.Title}[/]"),
                            new Markup($"[#C7B299]{detail}[/]")
                        ).Spacing(1);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title("📚 小说项目")
            .BottomLeftText(new Markup("[#C7B299]↑↓ 导航 · Enter 确认 · Esc 关闭[/]"))
            .Width(60)
            .MinHeight(14)
            .Padding(new Thickness(2, 1, 2, 1));

        list.ItemActivated(OnActivated);
        dialog.Content(PanelThemes.CreateDialogContent(list));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
        dialog.App?.Focus(list);
        return;

        void OnActivated(object? _, ItemActivatedEventArgs e)
        {
            if (e.Index < 0 || e.Index >= items.Count)
                return;
            var selected = items[e.Index];
            dialog.Close();
            if (selected.NovelId == AddDirectorySentinel)
            {
                ShowAddDirectoryForm();
            }
            else if (selected.NovelId is null)
                ShowNewForm();
            else
            {
                // 打开哪本目录里的书，写入目标就跟着切过去（新建/云端拉取落到该书目录）
                if (_workspace?.FindRootForNovel(selected.NovelId) is { } root)
                    _workspace.SetActive(root);
                ShowSessionPicker(selected.NovelId, selected.Title);
            }
        }
    }

    /// <summary>「添加小说目录」列表项的哨兵值（区别于新建项目 / 打开小说）。</summary>
    private const string AddDirectorySentinel = "__add_directory__";

    /// <summary>
    /// 添加小说目录表单：手输路径（TUI 无原生目录选择器）。只登记，不移动/删除任何文件；
    /// 登记后立即回到项目选择器，列表即覆盖新目录。
    /// </summary>
    private void ShowAddDirectoryForm()
    {
        if (_workspace is null)
        {
            ToastService.Warning("当前存储不支持目录管理");
            return;
        }

        var pathState = new State<string?>("");
        var input = new TextBox()
            .Text(pathState)
            .Placeholder("/path/to/novels")
            .HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📂 添加小说目录")
            .BottomLeftText(new Markup("[#C7B299]Enter 添加 · Esc 取消[/]"))
            .Width(60)
            .Padding(new Thickness(2, 1, 2, 1));
        dialog.Content(
            PanelThemes.CreateDialogContent(
                new VStack(
                    new Markup(
                        "[#C7B299]目录里每个子文件夹是一本书。列表会汇总所有已登记目录。[/]"
                    ),
                    input
                ).Spacing(1)
            )
        );
        dialog.BottomRightText(
            new HStack(new Button(new Markup("[dim]取消[/]")).Click(dialog.Close)).Spacing(2)
        );
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );

        input.KeyDown(
            (_, e) =>
            {
                if (e.Key != TerminalKey.Enter)
                    return;
                e.Handled = true;
                var path = pathState.Value?.Trim();
                if (string.IsNullOrEmpty(path))
                    return;
                TuiSafeFire.SafeFire(
                    async () =>
                    {
                        if (!_workspace.Add(path))
                        {
                            ToastService.Warning("目录不存在，或已在列表里");
                            return;
                        }
                        ToastService.Success($"已添加：{Shorten(path)}");
                        dialog.Close();
                        Show();
                    },
                    "add novels directory",
                    errorMessage: "添加目录失败"
                );
            }
        );
        dialog.Show();
        dialog.App?.Focus(input);
    }

    /// <summary>目录显示名：家目录收成 ~，过长则只留末两级。</summary>
    private static string? Shorten(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? text : string.Join('/', segments[^2..]);
    }

    /// <summary>
    /// 显示会话选择器。
    /// </summary>
    /// <param name="novelId">小说 ID。</param>
    /// <param name="novelTitle">小说标题。</param>
    private void ShowSessionPicker(string novelId, string novelTitle)
    {
        new SessionPickerDialog(store, sessionStore, onOpenNovel, novelId, novelTitle).Show();
    }

    // ── 新建项目表单 ──

    /// <summary>
    /// 显示新建项目表单。
    /// 题材选择为两级级联：先选一级分类，再选二级分类。
    /// </summary>
    private void ShowNewForm()
    {
        // 表单状态：书名、作者、一级分类索引、二级分类索引、简介、总纲、目标字数
        var titleState = new State<string?>("");
        var authorState = new State<string?>("");
        var categoryIdx = new State<int>(0);
        var subcategoryIdx = new State<int>(0);
        var descState = new State<string?>("");
        var outlineState = new State<string?>("");
        var wordCountState = new State<string?>("50000");

        // 一级分类 Select
        var categoryNames = Categories.Select(c => c.Name).ToArray();
        var categorySelect = new Select<string>()
            .Items(categoryNames)
            .SelectedIndex(categoryIdx)
            .Style(SelectStyle.NoBorder);
        categorySelect.HorizontalAlignment = Align.Stretch;

        // 二级分类 Select — 根据一级分类联动
        var initialSubs = Categories[0].Subcategories.Select(s => s.Name).ToArray();
        var subcategorySelect = new Select<string>()
            .Items(initialSubs)
            .SelectedIndex(subcategoryIdx)
            .Style(SelectStyle.NoBorder);
        subcategorySelect.HorizontalAlignment = Align.Stretch;

        // 一级分类变化时更新二级分类列表
        categorySelect.SelectionChanged(
            (_, _) =>
            {
                var idx = categorySelect.SelectedIndex;
                if (idx >= 0 && idx < Categories.Length)
                {
                    var subs = Categories[idx].Subcategories.Select(s => s.Name).ToArray();
                    subcategorySelect.Items(subs);
                    subcategoryIdx.Value = 0;
                }
            }
        );

        var form = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(12) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(new Markup("[#C7B299]书名 *[/]"), 0, 0)
            .Cell(
                new TextBox()
                    .Text(titleState)
                    .Placeholder("必填")
                    .HorizontalAlignment(Align.Stretch),
                0,
                1
            )
            .Cell(new Markup("[#C7B299]作者[/]"), 1, 0)
            .Cell(
                new TextBox()
                    .Text(authorState)
                    .Placeholder("选填")
                    .HorizontalAlignment(Align.Stretch),
                1,
                1
            )
            .Cell(new Markup("[#C7B299]大类[/]"), 2, 0)
            .Cell(categorySelect, 2, 1)
            .Cell(new Markup("[#C7B299]题材[/]"), 3, 0)
            .Cell(subcategorySelect, 3, 1)
            .Cell(new Markup("[#C7B299]简介[/]"), 4, 0)
            .Cell(
                new TextBox()
                    .Text(descState)
                    .Placeholder("选填，AI 可生成")
                    .HorizontalAlignment(Align.Stretch),
                4,
                1
            )
            .Cell(new Markup("[#C7B299]总纲[/]"), 5, 0)
            .Cell(
                new TextArea()
                    .Text(outlineState)
                    .Placeholder("选填：全书走向 / 主题 / 核心冲突 / 结局方向")
                    .AutoSizeMode(TextEditorAutoSizeMode.Height)
                    .MinHeight(3)
                    .MaxHeight(6)
                    .AcceptTab(false)
                    .Scrollable()
                    .HorizontalAlignment(Align.Stretch),
                5,
                1
            )
            .Cell(new Markup("[#C7B299]目标字数[/]"), 6, 0)
            .Cell(
                new TextBox()
                    .Text(wordCountState)
                    .Placeholder("50000")
                    .HorizontalAlignment(Align.Stretch),
                6,
                1
            )
            .HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title("📚 新建小说项目")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(60)
            .MinHeight(28)
            .Padding(new Thickness(2, 1, 2, 1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);

        var createBtn = new Button(new Markup("[bold]创建[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(OnCreate);
        var backBtn = new Button(new Markup("[dim]返回[/]")).Click(OnBack);

        dialog.Content(PanelThemes.CreateScrollableDialogContent(form));
        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(backBtn, createBtn).Spacing(2));

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
        return;

        // UI 事件处理器：异常处理收敛到 TuiSafeFire（Toast），核心逻辑内新增 await 无需扩 try。
        // SaveNovel 是唯一 await；dialog.Close 在保存成功后调用。
        void OnCreate()
        {
            TuiSafeFire.SafeFire(
                async () =>
                {
                    var title = titleState.Value?.Trim();
                    if (string.IsNullOrEmpty(title))
                    {
                        ToastService.Warning("书名不能为空");
                        return;
                    }

                    // 取二级分类名作为 Genre（Novel.Genre 存储二级分类中文名）
                    var catIdx = categoryIdx.Value;
                    var subIdx = subcategoryIdx.Value;
                    var genre = "东方玄幻"; // 默认 fallback
                    if (catIdx >= 0 && catIdx < Categories.Length)
                    {
                        var subs = Categories[catIdx].Subcategories;
                        if (subIdx >= 0 && subIdx < subs.Length)
                            genre = subs[subIdx].Name;
                    }

                    var novel = new Novel
                    {
                        Title = title,
                        Genre = genre,
                        Author = authorState.Value?.Trim() ?? "",
                        TargetWords =
                            int.TryParse(wordCountState.Value?.Trim(), out var tw) && tw > 0
                                ? tw
                                : 0,
                        Description = descState.Value ?? "",
                        Outline = outlineState.Value?.Trim() ?? "",
                        WritingStyle = "",
                    };

                    // FileNovelStore 需要手动开启写入事务
                    using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
                    if (store is FileNovelStore fns)
                    {
                        using (fns.BeginManualWrite())
                            await store.SaveNovel(novel);
                    }
                    else
                    {
                        await store.SaveNovel(novel);
                    }
                    ToastService.Success($"已创建：{title}（{genre}）");
                    dialog.Close();
                    onOpenNovel?.Invoke(novel.Id, null);
                },
                "create novel",
                errorMessage: "创建失败"
            );
        }

        void OnBack()
        {
            dialog.Close();
            Show();
        }
    }

    /// <summary>项目列表项（用于 OptionList 展示；Directory = 书所在的项目目录角标）。</summary>
    private sealed record NovelItem(
        string? NovelId,
        string Title,
        string Genre,
        int Chapters,
        string Date,
        string? Directory
    );
}
