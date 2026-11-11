using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Core.Data;
using Core.Evolution;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 章节预览的阅读范围：全书（默认）/ 指定卷 / 未分卷。
/// 范围只影响上一章 / 下一章的可达集合，不影响编辑与保存。
/// </summary>
public enum ChapterPreviewScope
{
    Novel,
    Volume,
    Unassigned,
}

/// <summary>
/// 章节内容窗口：默认就是可编辑的正文编辑器（无阅读 / 编辑模式切换），
/// 顶部「上一章 / 下一章」按预览范围翻页；有未保存修改时切换章节前，
/// 弹「保存 / 不保存 / 取消」。
/// 保存经 FileNovelStore.BeginManualWrite 包裹（与 AI 写入/冲突检测区分）。
/// 顶部「细纲」信息带展示大纲三层数据（梗概/计划关系/实际出场与状态变化）。
///
/// 预览范围由 <c>scope</c> / <c>volumeId</c> 决定：
/// 书 = 全书、卷 = 本卷、未分卷 = 未归属章节；章预览 = 全书从该章起。
/// </summary>
public partial class ChapterReaderWindow : Window
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly ChapterPreviewScope _scope;
    private readonly string? _volumeId;
    private string? _chapterId;
    private Chapter? _chapter;
    private List<Chapter> _chapters = [];
    private bool _outlineExpanded;

    /// <summary>本体关系索引（名称解析 + 计划关系胶囊）。</summary>
    private EntityRelationIndex? _relations;

    /// <summary>演化时间线（实际出场/状态变化）。</summary>
    private EntityTimelineView? _timeline;

    /// <summary>全实体时间线（变化行的待发布标记 + 地点 / 情节线摘要）。</summary>
    private EntityLogView? _logView;

    public ChapterReaderWindow(
        INovelStore store,
        string novelId,
        string? chapterId,
        ChapterPreviewScope scope = ChapterPreviewScope.Novel,
        string? volumeId = null
    )
    {
        _store = store;
        _novelId = novelId;
        _chapterId = chapterId;
        _scope = scope;
        _volumeId = volumeId;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "章节内容", 820, 720);
        // ApplyWindowStyle 会把最小尺寸压到 460×340；内容窗需要更稳的编辑区。
        MinWidth = 640;
        MinHeight = 540;

        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();
        PrevButton.Click += async (_, _) => await NavigateAsync(-1);
        NextButton.Click += async (_, _) => await NavigateAsync(1);
        ContentEditor.TextChanged += (_, _) => UpdateDirtyState();
        KeyDown += OnWindowKeyDown;

        _ = LoadAsync();
    }

    /// <summary>正文是否有未落盘修改（与当前章快照逐字比较）。</summary>
    private bool IsDirty =>
        _chapter is not null
        && !string.Equals(ContentEditor.Text ?? "", _chapter.Content, StringComparison.Ordinal);

    /// <summary>Esc 关闭窗口；Cmd/Ctrl+S 保存。</summary>
    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
        {
            e.Handled = true;
            await SaveAsync();
        }
    }

    /// <summary>
    /// 有未保存修改时询问：保存（失败则返回 false）/ 不保存 / 取消。
    /// 无修改直接放行。
    /// </summary>
    private async Task<bool> ConfirmLeaveAsync(string action, string saveText)
    {
        if (!IsDirty)
            return true;

        var prompt = new SaveChangesWindow(
            $"「{_chapter!.Title}」有未保存的修改",
            $"{action}前是否保存？",
            saveText
        );
        var choice = await prompt.ShowAsync(this);
        return choice switch
        {
            SaveChangesChoice.Save => await SaveAsync(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            _chapters = await LoadScopedChaptersAsync();
            if (_chapters.Count == 0)
            {
                ShowUnavailable(
                    _scope switch
                    {
                        ChapterPreviewScope.Volume => "本卷还没有章节",
                        ChapterPreviewScope.Unassigned => "未分卷还没有章节",
                        _ => "全书还没有章节",
                    }
                );
                return;
            }

            try
            {
                _relations = await EntityRelationIndex.BuildAsync(_store, _novelId);
            }
            catch
            {
                _relations = null;
            }
            try
            {
                _timeline = await EntityTimelineView.BuildAsync(_store, _novelId);
            }
            catch
            {
                _timeline = null;
            }
            try
            {
                _logView = await EntityLogView.BuildAsync(_store, _novelId);
            }
            catch
            {
                _logView = null;
            }

            // 指定章不在预览范围内（被删 / 换卷）→ 从范围首章开始，而不是空窗。
            var targetId =
                _chapterId is { Length: > 0 } id && _chapters.Exists(c => c.Id == id)
                    ? id
                    : _chapters[0].Id;
            await LoadChapterAsync(targetId);
        }
        catch (Exception ex)
        {
            Log().LogWarning(ex, "章节加载失败 novelId={NovelId}", _novelId);
            TitleBlock.Text = "章节加载失败";
        }
    }

    /// <summary>按预览范围读取章节（保持全书 Order 升序）。</summary>
    private async Task<List<Chapter>> LoadScopedChaptersAsync()
    {
        var chapters = (await _store.GetChapters(_novelId)).OrderBy(c => c.Order).ToList();
        if (_scope == ChapterPreviewScope.Novel)
            return chapters;

        // 未分卷 = VolumeId 为空或指向已删除的卷；需要卷集合才能判定悬挂 id。
        var knownVolumeIds =
            _scope == ChapterPreviewScope.Unassigned
                ? (await _store.GetVolumes(_novelId))
                    .Select(v => v.Id)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        return SelectChapters(chapters, _scope, _volumeId, knownVolumeIds);
    }

    /// <summary>
    /// 预览范围筛选（纯函数，供测试锁定）：不重排，只决定哪些章节可达。
    /// </summary>
    internal static List<Chapter> SelectChapters(
        IReadOnlyList<Chapter> chapters,
        ChapterPreviewScope scope,
        string? volumeId,
        IReadOnlySet<string> knownVolumeIds
    ) =>
        scope switch
        {
            ChapterPreviewScope.Volume when string.IsNullOrEmpty(volumeId) => [],
            ChapterPreviewScope.Volume => chapters
                .Where(c => string.Equals(c.VolumeId, volumeId, StringComparison.Ordinal))
                .ToList(),
            ChapterPreviewScope.Unassigned => chapters
                .Where(c =>
                    string.IsNullOrEmpty(c.VolumeId) || !knownVolumeIds.Contains(c.VolumeId)
                )
                .ToList(),
            _ => [.. chapters],
        };

    /// <summary>范围内没有章节 / 章节已删除：清空旧快照并禁用编辑、翻页、细纲。</summary>
    private void ShowUnavailable(string message)
    {
        _chapter = null;
        _chapterId = null;
        Title = "章节内容";
        EyebrowText.Text = "CHAPTER";
        TitleBlock.Text = message;
        StatusTag.Text = "";
        WordCountBlock.Text = "";
        ScopeTag.IsVisible = false;
        DirtyTag.IsVisible = false;
        ContentEditor.Text = "";
        ContentEditor.IsVisible = false;
        SaveButton.IsEnabled = false;
        SaveButton.IsVisible = false;
        OutlinePanel.IsVisible = false;
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
    }

    private async Task LoadChapterAsync(string chapterId)
    {
        var chapter = await _store.GetChapter(_novelId, chapterId);
        if (chapter is null)
        {
            // 不能留着上一章的快照：否则保存会把旧章内容写回新章 id。
            ShowUnavailable("章节不存在（可能已被删除）");
            return;
        }

        _chapter = chapter;
        _chapterId = chapterId;

        Title = $"章节内容 · {chapter.Title}";
        EyebrowText.Text = $"CHAPTER {chapter.Order:D2}";
        TitleBlock.Text = string.IsNullOrWhiteSpace(chapter.Title)
            ? "（未命名章节）"
            : chapter.Title;
        StatusTag.Text = chapter.Status switch
        {
            ChapterStatus.Draft => "草稿",
            ChapterStatus.Published => "发布",
            _ => chapter.Status.ToString(),
        };
        WordCountBlock.Text = $"{chapter.Content.Length:N0} 字";

        ContentEditor.Text = chapter.Content;
        ContentEditor.CaretIndex = 0;
        ContentEditor.IsVisible = true;
        ContentEditor.Focus();
        SaveButton.IsVisible = true;
        UpdateDirtyState();

        // 上/下章可用性 + 范围位置
        var idx = _chapters.FindIndex(c => c.Id == chapterId);
        PrevButton.IsEnabled = idx > 0;
        NextButton.IsEnabled = idx >= 0 && idx < _chapters.Count - 1;
        ScopeTag.Text = $"{ScopeName} · {idx + 1}/{_chapters.Count}";
        ScopeTag.IsVisible = idx >= 0;
        ToolTip.SetTip(PrevButton, idx > 0 ? $"上一章 · {_chapters[idx - 1].Title}" : "已是第一章");
        ToolTip.SetTip(
            NextButton,
            idx >= 0 && idx < _chapters.Count - 1
                ? $"下一章 · {_chapters[idx + 1].Title}"
                : "已是最后一章"
        );

        RenderOutlineInfo(chapter);
    }

    /// <summary>范围名（位置指示 + 空态标题共用）。</summary>
    private string ScopeName =>
        _scope switch
        {
            ChapterPreviewScope.Volume => "本卷",
            ChapterPreviewScope.Unassigned => "未分卷",
            _ => "全书",
        };

    /// <summary>刷新「未保存」提示、保存按钮可用性与实时字数。</summary>
    private void UpdateDirtyState()
    {
        var dirty = IsDirty;
        SaveButton.IsEnabled = dirty;
        DirtyTag.IsVisible = dirty;
        if (_chapter is not null)
            WordCountBlock.Text = $"{ContentEditor.Text?.Length ?? 0:N0} 字";
    }

    /// <summary>
    /// 渲染细纲信息带：梗概（细纲）/ 计划关系胶囊（所属分卷·计划推进情节线·计划出场角色）/
    /// 实际记录（出场章节模式 + 状态变化）。全部为空时整带隐藏。
    /// </summary>
    private void RenderOutlineInfo(Chapter chapter)
    {
        SummaryText.Text = chapter.Summary;
        SummaryText.IsVisible = !string.IsNullOrWhiteSpace(chapter.Summary);

        // 计划关系胶囊：先收集文本再封顶（大章计划关系可数十条，防信息带吞掉正文区）
        const int maxChips = 8;
        var chipTexts = new List<string>();
        if (chapter.VolumeId is not null)
            // 关系索引的分卷名已含「第N卷」前缀，此处不再重复加「第」
            chipTexts.Add(NameOf(EntityType.Volume, chapter.VolumeId));
        chipTexts.AddRange(
            chapter.PlotlineIds.Select(id => $"推进 {NameOf(EntityType.Plotline, id)}")
        );
        chipTexts.AddRange(
            chapter.CharacterIds.Select(id => $"出场 {NameOf(EntityType.Character, id)}")
        );

        ChipsPanel.Children.Clear();
        foreach (var text in chipTexts.Take(maxChips))
            ChipsPanel.Children.Add(MakeChip(text));
        if (chipTexts.Count > maxChips)
            ChipsPanel.Children.Add(MakeChip($"+{chipTexts.Count - maxChips}"));

        var appearances = _timeline?.GetChapterAppearances(chapter.Id) ?? [];
        var changes = _timeline?.GetChapterStateChanges(chapter.Id) ?? [];
        var logEntries = _logView?.GetByChapter(chapter.Id) ?? [];
        var recordParts = new List<string>();
        if (appearances.Count > 0)
        {
            var grouped = appearances
                .GroupBy(a => (a.EntityType, a.EntityId))
                .Select(g =>
                    $"{NameOf(g.Key.EntityType, g.Key.EntityId)}({(g.Any(x => x.Mode == AppearanceMode.Present) ? "在场" : "提及")})"
                );
            recordParts.Add("出场 " + string.Join(" ", grouped));
        }
        foreach (var change in changes)
        {
            var summary = string.IsNullOrWhiteSpace(change.Summary)
                ? $"{change.FromSummary ?? "?"} → {change.ToSummary ?? "?"}"
                : change.Summary;
            var pending = change.Lifecycle == ChangeLifecycle.Pending ? "（待发布）" : "";
            recordParts.Add(
                $"变化{pending} {NameOf(change.TargetEntityType, change.TargetId)} {summary}"
            );
        }
        var locations = logEntries
            .Select(e => e.LocationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (locations.Count > 0)
            recordParts.Add("地点 " + string.Join("/", locations));
        var plotlines = logEntries
            .SelectMany(e => e.PlotlineNames)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (plotlines.Count > 0)
            recordParts.Add("情节线 " + string.Join("/", plotlines));
        if (changes.Any(c => c.IsCascade))
            recordParts.Add($"🔗联动 {changes.Count(c => c.IsCascade)} 项");
        RecordsText.Text = string.Join(" · ", recordParts);
        RecordsText.IsVisible = recordParts.Count > 0;

        // 折叠态也能读到数据量：梗概 / 计划 / 出场 / 变化；正文首行做 teaser。
        var metaParts = new List<string>();
        if (SummaryText.IsVisible)
            metaParts.Add("梗概");
        var planCount = chapter.PlotlineIds.Count + chapter.CharacterIds.Count;
        if (planCount > 0)
            metaParts.Add($"计划 {planCount}");
        if (appearances.Count > 0)
            metaParts.Add($"出场 {appearances.Count}");
        if (changes.Count > 0)
            metaParts.Add($"变化 {changes.Count}");
        OutlineMeta.Text = string.Join(" · ", metaParts);

        var teaser = FirstLine(chapter.Summary);
        if (teaser.Length == 0)
            teaser = string.Join(" · ", chipTexts.Take(3));
        if (teaser.Length == 0 && recordParts.Count > 0)
            teaser = recordParts[0];
        OutlineTeaser.Text = teaser;

        OutlinePanel.IsVisible =
            SummaryText.IsVisible || ChipsPanel.Children.Count > 0 || RecordsText.IsVisible;
        UpdateOutlineExpansion();
    }

    /// <summary>展开 / 收起细纲；展开态在切换章节后保持。</summary>
    private void OnOutlineToggle_Click(object? sender, RoutedEventArgs e)
    {
        _outlineExpanded = !_outlineExpanded;
        UpdateOutlineExpansion();
        e.Handled = true;
    }

    private void UpdateOutlineExpansion()
    {
        OutlineDetails.IsVisible = _outlineExpanded;
        OutlineChevron.Kind = _outlineExpanded
            ? LucideIconKind.ChevronDown
            : LucideIconKind.ChevronRight;
        OutlineTeaser.IsVisible =
            !_outlineExpanded && !string.IsNullOrWhiteSpace(OutlineTeaser.Text);
    }

    /// <summary>取正文首行（细纲折叠态的 teaser）。</summary>
    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        return "";
    }

    /// <summary>关系胶囊（TagPill + mono 文本）。</summary>
    private static Border MakeChip(string text) =>
        new()
        {
            Classes = { "TagPill" },
            Padding = new Thickness(8, 2),
            Child = new TextBlock
            {
                Classes = { "Tag" },
                FontSize = 10,
                Text = text,
            },
        };

    /// <summary>实体显示名（关系索引解析；索引不可用时原样返回 id）。</summary>
    private string NameOf(EntityType type, string id) => _relations?.NameOf(type, id) ?? id;

    /// <summary>
    /// 上一章 / 下一章：有未保存正文时先询问保存 / 不保存 / 取消。
    /// 保存失败或取消则留在本章。
    /// </summary>
    private async Task NavigateAsync(int direction)
    {
        var idx = _chapters.FindIndex(c => c.Id == _chapterId);
        if (idx < 0)
            return;
        var target = idx + direction;
        if (target < 0 || target >= _chapters.Count)
            return;
        if (!await ConfirmLeaveAsync("切换章节", "保存并切换"))
            return;
        await LoadChapterAsync(_chapters[target].Id);
    }

    /// <summary>保存正文；返回 false 表示失败（调用方应阻止翻页 / 关闭 / 进入细纲）。</summary>
    private async Task<bool> SaveAsync()
    {
        if (_chapter is null)
            return false;

        if (!IsDirty)
        {
            UpdateDirtyState();
            return true;
        }

        var content = ContentEditor.Text ?? "";
        var previous = _chapter.Content;
        _chapter.Content = content;
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        try
        {
            if (_store is FileNovelStore fns)
            {
                using (fns.BeginManualWrite())
                    await _store.SaveChapter(_chapter);
            }
            else
            {
                await _store.SaveChapter(_chapter);
            }
            WordCountBlock.Text = $"{_chapter.Content.Length:N0} 字";
            TitleBlock.Text = $"{_chapter.Order}. {_chapter.Title}（已保存）";
            UpdateDirtyState();
            ContentEditor.Focus();
            return true;
        }
        catch (Exception ex)
        {
            // 内存快照回滚：未落盘正文不会被后续翻页 / 关闭当作已保存内容。
            _chapter.Content = previous;
            Log().LogWarning(ex, "章节保存失败 chapterId={ChapterId}", _chapterId);
            TitleBlock.Text = $"{_chapter.Order}. {_chapter.Title}（保存失败）";
            UpdateDirtyState();
            return false;
        }
    }

    private ILogger<ChapterReaderWindow> Log() =>
        (ILogger<ChapterReaderWindow>?)
            Program.Services?.GetService(typeof(ILogger<ChapterReaderWindow>))
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChapterReaderWindow>.Instance;
}
