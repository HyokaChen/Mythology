using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Data;
using Core.Evolution;

namespace Desktop.Controls;

/// <summary>
/// 本体关系窗（任何实体类型可用）：三节展示该实体在本体图中的关联——
/// 关系（结构边，可点击跳转）/ 出场章节（从 EntityTimelineView 派生，可跳转）/ 状态变化（只读文本）。
/// </summary>
public partial class EntityRelationsWindow : Window
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly EntityType _type;
    private readonly string _entityId;
    private readonly string _entityName;
    private readonly Action<EntityType, string>? _onNavigate;

    public EntityRelationsWindow(
        INovelStore store,
        string novelId,
        EntityType type,
        string entityId,
        string entityName,
        Action<EntityType, string>? onNavigate
    )
    {
        _store = store;
        _novelId = novelId;
        _type = type;
        _entityId = entityId;
        _entityName = entityName;
        _onNavigate = onNavigate;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "本体关系", 520, 640);

        TitleBlock.Text = $"{EntityFieldRegistry.Label(type)}「{entityName}」";
        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
                Close();
        };

        _ = BuildSectionsAsync();
    }

    private async Task BuildSectionsAsync()
    {
        try
        {
            var relations = await EntityRelationIndex.BuildAsync(_store, _novelId);
            EntityTimelineView? timeline = null;
            try
            {
                timeline = await EntityTimelineView.BuildAsync(_store, _novelId);
            }
            catch
            {
                // 演化视图不可用 → 出场/变化两节为空
            }

            var chapters = await _store.GetChapters(_novelId);
            var chapterOrder = chapters.ToDictionary(c => c.Id, c => c.Order);

            // ── 关系 ──
            var relationRows = relations
                .GetRelations(_type, _entityId)
                .Select(r =>
                {
                    var isSource = r.SourceType == _type && r.SourceId == _entityId;
                    var counterpartType = isSource ? r.TargetType : r.SourceType;
                    var counterpartId = isSource ? r.TargetId : r.SourceId;
                    var label = EntityRelationIndex.LabelFor(r, _type, _entityId);
                    var name = relations.NameOf(counterpartType, counterpartId);
                    var text = string.IsNullOrEmpty(r.Detail)
                        ? $"{label} {name}"
                        : $"{label} {name}（{r.Detail}）";
                    return MakeNavigationButton(text, counterpartType, counterpartId);
                })
                .ToList();
            AddSection("关系", relationRows);

            // ── 出场章节 ──
            var appearanceRows = new List<Control>();
            if (timeline is not null)
            {
                var rows = timeline
                    .AllAppearances.Where(a => a.EntityType == _type && a.EntityId == _entityId)
                    .Where(a => chapterOrder.ContainsKey(a.ChapterId))
                    .GroupBy(a => a.ChapterId)
                    .OrderBy(g => chapterOrder[g.Key])
                    .Select(g =>
                    {
                        var modes = string.Join(
                            "/",
                            g.Select(m => m.Mode == AppearanceMode.Present ? "在场" : "提及")
                                .Distinct()
                        );
                        return MakeNavigationButton(
                            $"第{chapterOrder[g.Key]}章 {modes}",
                            EntityType.Chapter,
                            g.Key
                        );
                    });
                appearanceRows.AddRange(rows);
            }
            AddSection("出场章节", appearanceRows);

            // ── 状态变化 ──
            var changeRows = new List<Control>();
            if (timeline is not null)
            {
                changeRows.AddRange(
                    timeline
                        .AllStateChanges.Where(c =>
                            c.TargetEntityType == _type
                            && c.TargetId == _entityId
                            // 关系窗展示「已生效」的权威状态：待发布变化（草稿章）不参与
                            && c.Lifecycle == ChangeLifecycle.Committed
                        )
                        .OrderBy(c =>
                            chapterOrder.TryGetValue(c.ChapterId, out var order) ? order : 0
                        )
                        .Select(c =>
                        {
                            var chapterLabel = chapterOrder.TryGetValue(c.ChapterId, out var order)
                                ? $"第{order}章"
                                : "(未知章)";
                            var summary = string.IsNullOrWhiteSpace(c.Summary)
                                ? $"{c.FromSummary ?? "?"} → {c.ToSummary ?? "?"}"
                                : c.Summary;
                            return (Control)
                                new TextBlock
                                {
                                    Text = $"{chapterLabel} {c.Aspect}: {summary}",
                                    FontSize = 11,
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = GetBrush("BrushSageGray"),
                                };
                        })
                );
            }
            AddSection("状态变化", changeRows);

            if (SectionsPanel.Children.Count == 0)
            {
                SectionsPanel.Children.Add(
                    new Border
                    {
                        Classes = { "EmptyNote" },
                        Child = new TextBlock { Classes = { "Tag" }, Text = "暂无关系记录" },
                    }
                );
            }
        }
        catch (Exception ex)
        {
            SectionsPanel.Children.Add(
                new TextBlock
                {
                    Text = $"加载关系失败：{ex.Message}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = GetBrush("BrushCrimsonSpecimen"),
                }
            );
        }
    }

    /// <summary>追加一节（空节跳过；标题用 Eyebrow，行用 NavPill 按钮）。</summary>
    private void AddSection(string title, IReadOnlyList<Control> rows)
    {
        if (rows.Count == 0)
            return;

        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock { Classes = { "Eyebrow" }, Text = title });
        foreach (var row in rows)
            section.Children.Add(row);
        SectionsPanel.Children.Add(section);
    }

    /// <summary>可跳转行：点击后切 tab 并打开目标实体。</summary>
    private Button MakeNavigationButton(string text, EntityType targetType, string targetId)
    {
        var button = new Button
        {
            Classes = { "NavPill" },
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4),
            FontSize = 12,
        };
        button.Click += (_, _) =>
        {
            _onNavigate?.Invoke(targetType, targetId);
            Close();
        };
        return button;
    }

    private static IBrush? GetBrush(string key) =>
        Application.Current?.TryFindResource(key, out var brush) == true
            ? brush as IBrush
            : Brushes.Gray;
}
