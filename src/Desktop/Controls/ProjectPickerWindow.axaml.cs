using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 小说项目选择窗口（移植 TUI NovelProjectDialog）：
/// 项目列表（最近更新在前，汇总全部已登记项目目录）→ 选中打开；「新建项目」切到表单（题材两级级联）；
/// 「打开其他目录…」把电脑上的另一个小说目录加入清单。
/// 为什么需要：Desktop 的 cwd 不可控（打包后双击 = /），TUI 靠 cwd/novels 的约定在这里不成立，
/// 目录必须由用户指定并持久化在 ~/.mythology/workspaces.json（与 TUI 共用同一份清单）。
/// 布局对齐 DESIGN.md（Adaline）：Linen 底 · Card 列表 · 字段标签 · Primary/Ghost 按钮。
/// </summary>
public partial class ProjectPickerWindow : Window
{
    private readonly INovelStore _store;
    private readonly NovelWorkspace _workspace;
    private readonly Action<string> _onOpenNovel;
    private readonly bool _showLlmHint;
    private readonly string? _currentNovelId;

    /// <summary>防「按钮激活 + 窗口 KeyDown」双通道重复打开（打开后窗口即将关闭）。</summary>
    private bool _opened;

    private readonly string[] _categories = GenreDefinitions.All.Select(c => c.Name).ToArray();

    /// <summary>
    /// 创建项目选择/新建窗口。
    /// </summary>
    /// <param name="workspace">项目目录清单（读取 = 并集，写入 = ActiveRoot）。</param>
    /// <param name="showLlmHint">LLM 连接器未配置时，主面板顶部展示配置提醒（onboarding 衔接）。</param>
    /// <param name="currentNovelId">当前已打开的小说：列表标「当前」并预选（回车即切回）。</param>
    public ProjectPickerWindow(
        INovelStore store,
        Action<string> onOpenNovel,
        NovelWorkspace workspace,
        bool showLlmHint = false,
        string? currentNovelId = null
    )
    {
        _store = store;
        _workspace = workspace;
        _onOpenNovel = onOpenNovel;
        _showLlmHint = showLlmHint;
        _currentNovelId = currentNovelId;

        InitializeComponent();

        // 设置 LLM 提示横幅
        LlmHintBanner.IsVisible = showLlmHint;

        // 列表事件
        NovelList.SelectionChanged += (_, _) =>
        {
            OpenButton.IsEnabled = NovelList.SelectedItem is not null;
        };
        NovelList.DoubleTapped += (_, _) => OpenSelected();
        OpenButton.Click += (_, _) => OpenSelected();
        NewButton.Click += (_, _) => ShowForm();
        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        OpenEmptyFolderButton.Click += async (_, _) => await AddFolderAsync();

        // 表单事件
        BackButton.Click += (_, _) => BackToMain();
        CreateButton.Click += async (_, _) => await CreateNovelAsync();

        // 分类级联
        CategoryBox.ItemsSource = _categories;
        CategoryBox.SelectedIndex = 0;
        CategoryBox.SelectionChanged += (_, _) => UpdateSubcategories();
        UpdateSubcategories();

        KeyDown += (_, e) =>
        {
            // Esc 分层：表单 → 列表；列表 → 关窗。Enter：仅列表层打开（表单层留给输入框）
            if (e.Key == Key.Escape)
            {
                if (FormPanel.IsVisible)
                    BackToMain();
                else
                    Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && MainPanel.IsVisible)
            {
                OpenSelected();
                e.Handled = true;
            }
        };

        _ = LoadNovelsAsync();
    }

    /// <summary>一级分类变化 → 联动二级分类列表。</summary>
    private void UpdateSubcategories()
    {
        var idx = Math.Max(CategoryBox.SelectedIndex, 0);
        if (idx >= GenreDefinitions.All.Length)
            return;
        var subs = GenreDefinitions.All[idx].Subcategories.Select(s => s.Name).ToArray();
        SubcategoryBox.ItemsSource = subs;
        SubcategoryBox.SelectedIndex = 0;
    }

    private void ShowForm()
    {
        MainPanel.IsVisible = false;
        FormPanel.IsVisible = true;
        TitleBox.Focus();
    }

    /// <summary>切回列表层（重载项目列表；表单字段保留）。</summary>
    private void BackToMain()
    {
        FormPanel.IsVisible = false;
        MainPanel.IsVisible = true;
        _ = LoadNovelsAsync();
    }

    private void OpenSelected()
    {
        if (_opened || NovelList.SelectedItem is not NovelItem item)
            return;
        _opened = true;

        // 打开哪本目录里的书，写入目标就跟着切过去：新建章节、云端拉取都落在该书所在目录
        var root = _workspace.FindRootForNovel(item.Id);
        if (root is not null)
            _workspace.SetActive(root);

        Close();
        _onOpenNovel(item.Id);
    }

    /// <summary>
    /// 「打开本地文件夹…」：选任意目录 → 登记进清单并设为写入目录（新建/云端拉取落点），
    /// 然后自动加载该目录下的所有小说。重复选择同一目录也视为「打开」，切换写入目标并刷新。
    /// </summary>
    private async Task AddFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择小说项目目录（其下每个子文件夹是一本书）",
                    AllowMultiple = false,
                }
            );
            if (folders.Count == 0)
                return;

            var path = folders[0].Path.LocalPath;
            // 选中即打开：无论是否首次登记，都设为当前写入目录
            _workspace.Add(path);
            _workspace.SetActive(path);
            await LoadNovelsAsync();
        }
        catch (Exception ex)
        {
            Program
                .Services?.GetRequiredService<ILogger<ProjectPickerWindow>>()
                .LogWarning(ex, "打开小说目录失败");
        }
    }

    private async Task LoadNovelsAsync()
    {
        try
        {
            var novels = await _store.GetAllNovels();
            var items = novels
                .OrderByDescending(n => n.UpdatedAt)
                .Select(n => new NovelItem(
                    n.Id,
                    string.IsNullOrEmpty(n.Title) ? n.Id : n.Title,
                    string.IsNullOrEmpty(n.Genre) ? "其他" : n.Genre,
                    n.Chapters.Count,
                    n.UpdatedAt.ToString("MM-dd"),
                    // 书所在的项目目录（清单里可能不止一个）→ 卡片角标，避免同名书分不清
                    Shorten(_store is FileNovelStore fns ? fns.FindNovelDirectory(n.Id) : null),
                    IsCurrent: n.Id == _currentNovelId
                ))
                .ToList();

            NovelList.ItemsSource = items;
            CountNote.Text = $"共 {items.Count} 部";
            // 预选当前小说：打开按钮即时可用，回车即切回
            var currentIdx = items.FindIndex(i => i.IsCurrent);
            if (currentIdx >= 0)
            {
                NovelList.SelectedIndex = currentIdx;
                NovelList.ScrollIntoView(currentIdx);
            }
            ApplyListState(items.Count > 0);
        }
        catch
        {
            // 加载失败：列表为空，用户仍可新建
            CountNote.Text = "共 0 部";
            ApplyListState(false);
        }
    }

    /// <summary>列表是否有内容 → 切换空状态提示（打开按钮可用性由选中项决定）。</summary>
    private void ApplyListState(bool hasNovels)
    {
        EmptyNote.IsVisible = !hasNovels;
        NovelList.IsVisible = hasNovels;
        ActiveDirNote.Text =
            $"新建与拉取落到：{Shorten(_workspace.ActiveRoot) ?? _workspace.ActiveRoot}";
    }

    /// <summary>目录显示名：家目录收成 ~，过长则只留末两级（列表里够辨认即可）。</summary>
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

    private async Task CreateNovelAsync()
    {
        var title = TitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title))
        {
            TitleBox.Focus();
            return;
        }

        var catIdx = Math.Max(CategoryBox.SelectedIndex, 0);
        var subIdx = Math.Max(SubcategoryBox.SelectedIndex, 0);
        var genre = "东方玄幻";
        if (catIdx < GenreDefinitions.All.Length)
        {
            var subs = GenreDefinitions.All[catIdx].Subcategories;
            if (subIdx < subs.Length)
                genre = subs[subIdx].Name;
        }

        var novel = new Novel
        {
            Title = title,
            Genre = genre,
            Author = AuthorBox.Text?.Trim() ?? "",
            TargetWords = int.TryParse(TargetWordsBox.Text?.Trim(), out var tw) && tw > 0 ? tw : 0,
            Description = DescBox.Text?.Trim() ?? "",
            Outline = OutlineBox.Text?.Trim() ?? "",
            WritingStyle = "",
        };

        try
        {
            using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
            if (_store is FileNovelStore fns)
            {
                using (fns.BeginManualWrite())
                    await _store.SaveNovel(novel);
            }
            else
            {
                await _store.SaveNovel(novel);
            }
            Close();
            _onOpenNovel(novel.Id);
        }
        catch (Exception ex)
        {
            Program
                .Services?.GetRequiredService<ILogger<ProjectPickerWindow>>()
                .LogWarning(ex, "创建小说失败");
        }
    }
}

// 顶级 internal record：XAML x:DataType 无法引用嵌套私有类型
internal sealed record NovelItem(
    string Id,
    string Title,
    string Genre,
    int Chapters,
    string Date,
    string? Directory,
    bool IsCurrent = false
)
{
    public override string ToString() => $"{Title}  ·  {Genre} {Chapters}章 · {Date}";
}
