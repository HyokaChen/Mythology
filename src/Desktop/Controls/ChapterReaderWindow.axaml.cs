using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Data;
using Core.Evolution;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 章节正文读/写窗口（Desktop 补齐 TUI 的章节正文缺口）：
/// 阅读态 = LiveMarkdown 渲染（对齐 ConversationView），编辑态 = TextArea，
/// 保存经 FileNovelStore.BeginManualWrite 包裹（与 AI 写入/冲突检测区分）。
/// 顶部「细纲」信息带展示大纲三层数据（梗概/计划关系/实际出场与状态变化）。
/// </summary>
public partial class ChapterReaderWindow : Window
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private string _chapterId;
    private Chapter? _chapter;
    private List<Chapter> _chapters = [];
    private bool _editing;

    /// <summary>本体关系索引（名称解析 + 计划关系胶囊）。</summary>
    private EntityRelationIndex? _relations;

    /// <summary>演化时间线（实际出场/状态变化）。</summary>
    private EntityTimelineView? _timeline;

    /// <summary>全实体时间线（变化行的待发布标记 + 地点 / 情节线摘要）。</summary>
    private EntityLogView? _logView;

    public ChapterReaderWindow(INovelStore store, string novelId, string chapterId)
    {
        _store = store;
        _novelId = novelId;
        _chapterId = chapterId;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "章节", 780, 680);

        EditToggleButton.Click += OnEditToggle_Click;
        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();
        PrevButton.Click += (_, _) => Navigate(1);
        NextButton.Click += (_, _) => Navigate(-1);
        OutlineButton.Click += (_, _) =>
        {
            var window = new EntityEditWindow(_store, EntityType.Chapter, _novelId, _chapterId);
            // 细纲窗可能改状态（发布 / 撤回）或换序：整窗数据重建（时间线 / 待发布标记同步）
            window.Closed += async (_, _) => await LoadAsync();
            window.ShowDialog(this);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                Close();
        };

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _chapters = (await _store.GetChapters(_novelId)).OrderBy(c => c.Order).ToList();
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
            await LoadChapterAsync(_chapterId);
        }
        catch (Exception ex)
        {
            Log().LogWarning(ex, "章节加载失败 novelId={NovelId}", _novelId);
            TitleBlock.Text = "章节加载失败";
        }
    }

    private async Task LoadChapterAsync(string chapterId)
    {
        var chapter = await _store.GetChapter(_novelId, chapterId);
        if (chapter is null)
        {
            TitleBlock.Text = "章节不存在（可能已被删除）";
            return;
        }
        _chapter = chapter;
        _chapterId = chapterId;

        TitleBlock.Text = $"{chapter.Order}. {chapter.Title}";
        StatusTag.Text = chapter.Status switch
        {
            ChapterStatus.Draft => "草稿",
            ChapterStatus.Published => "发布",
            _ => chapter.Status.ToString(),
        };
        WordCountBlock.Text = $"{chapter.Content.Length:N0} 字";

        var builder = new ObservableStringBuilder();
        builder.Append(chapter.Content);
        MarkdownView.MarkdownBuilder = builder;
        ContentEditor.Text = chapter.Content;
        SetEditing(false);

        // 上/下章可用性
        var idx = _chapters.FindIndex(c => c.Id == chapterId);
        PrevButton.IsEnabled = idx > 0;
        NextButton.IsEnabled = idx >= 0 && idx < _chapters.Count - 1;

        RenderOutlineInfo(chapter);
    }

    /// <summary>
    /// 渲染细纲信息带：梗概（细纲）/ 计划关系胶囊（所属分卷·推进情节线·计划出场角色）/
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

        OutlinePanel.IsVisible =
            SummaryText.IsVisible || ChipsPanel.Children.Count > 0 || RecordsText.IsVisible;
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

    private void Navigate(int direction)
    {
        var idx = _chapters.FindIndex(c => c.Id == _chapterId);
        if (idx < 0)
            return;
        var target = idx + direction;
        if (target < 0 || target >= _chapters.Count)
            return;
        _ = LoadChapterAsync(_chapters[target].Id);
    }

    private void OnEditToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_chapter is null)
            return;
        if (_editing)
        {
            // 编辑态 → 保存按钮可见；再次点编辑视为取消回到阅读态
            SetEditing(false);
            ContentEditor.Text = _chapter.Content;
        }
        else
        {
            SetEditing(true);
        }
    }

    private void SetEditing(bool editing)
    {
        _editing = editing;
        ReadPanel.IsVisible = !editing;
        ContentEditor.IsVisible = editing;
        SaveButton.IsVisible = editing;
        EditToggleButton.Content = editing ? "取消" : "编辑";
    }

    private async Task SaveAsync()
    {
        if (_chapter is null)
            return;
        _chapter.Content = ContentEditor.Text ?? "";
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
            var b = new ObservableStringBuilder();
            b.Append(_chapter.Content);
            MarkdownView.MarkdownBuilder = b;
            SetEditing(false);
            TitleBlock.Text = $"{_chapter.Order}. {_chapter.Title}（已保存）";
        }
        catch (Exception ex)
        {
            Log().LogWarning(ex, "章节保存失败 chapterId={ChapterId}", _chapterId);
            TitleBlock.Text = $"{_chapter.Order}. {_chapter.Title}（保存失败）";
        }
    }

    private ILogger<ChapterReaderWindow> Log() =>
        (ILogger<ChapterReaderWindow>?)
            Program.Services?.GetService(typeof(ILogger<ChapterReaderWindow>))
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChapterReaderWindow>.Instance;
}
