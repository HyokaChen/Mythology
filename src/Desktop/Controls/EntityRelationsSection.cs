using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Core.Data;
using Core.Evolution;

namespace Desktop.Controls;

/// <summary>
/// 「关联」Tab 的关联节（嵌于 <see cref="EntityEditWindow"/>，仅编辑模式）：
/// 全部索引可见关系 = 行级导航（点击即时打开对端，叠加于当前窗之上）+ ＋/× 双向编辑。
/// 改动仅暂存于 <see cref="PendingEdits"/>，由窗口「保存」时经 <see cref="RelationWriteThrough"/> 统一应用；
/// 取消/关窗 = 零副作用。暂存独立于渲染数据：<see cref="ReloadAsync"/> 保留暂存，已失效项在应用时跳过。
/// </summary>
public sealed class EntityRelationsSection
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly EntityType _type;
    private readonly string _entityId;
    private readonly Func<EntityType, string, Task>? _openCounterpart;

    private readonly StackPanel _root = new() { Spacing = 14 };
    private readonly List<PendingRelationEdit> _pending = [];

    /// <summary>暂存新增行的显示名（快照式；对端加载失败也不丢文案）。</summary>
    private readonly Dictionary<(SectionKey Key, string Id), string> _pendingNames = [];

    /// <summary>可编辑节 → 候选池（按对端类型缓存）。</summary>
    private readonly Dictionary<SectionKey, List<(string Name, string Id)>> _candidatePool = [];

    /// <summary>候选加载失败的节（隐藏「＋」区，行仍显示）。</summary>
    private readonly HashSet<SectionKey> _candidateLoadFailed = [];

    private readonly Dictionary<EntityType, List<(string Name, string Id)>> _poolsByType = [];
    private readonly HashSet<EntityType> _failedPoolTypes = [];

    /// <summary>人物关系对（无序 A|B）→ 关系实体 Id（多条取第一条）。</summary>
    private readonly Dictionary<string, string> _relationshipByPair = new(StringComparer.Ordinal);

    /// <summary>全量地点 ParentId（防环判定用；仅地点视图加载）。</summary>
    private Dictionary<string, string?>? _parents;

    private EntityRelationIndex? _index;
    private bool _navigating;

    public EntityRelationsSection(
        INovelStore store,
        string novelId,
        EntityType type,
        string entityId,
        Func<EntityType, string, Task>? openCounterpart
    )
    {
        _store = store;
        _novelId = novelId;
        _type = type;
        _entityId = entityId;
        _openCounterpart = openCounterpart;
        _root.Children.Add(Note("加载中…", "BrushSageMist"));
    }

    /// <summary>节根控件（由窗口包 ScrollViewer）。</summary>
    public Control Root => _root;

    /// <summary>挂起编辑列表（保存时应用；顺序=操作顺序）。</summary>
    public IReadOnlyList<PendingRelationEdit> PendingEdits => _pending;

    /// <summary>是否存在暂存改动。</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>暂存变化（窗口刷新底部「关联改动 N 项」）。</summary>
    public event Action? PendingChanged;

    /// <summary>首次加载（编辑窗 OnOpened 调用）。</summary>
    public Task LoadAsync() => LoadCoreAsync();

    /// <summary>嵌套窗关闭后重建数据；保留暂存。</summary>
    public Task ReloadAsync() => LoadCoreAsync();

    // ── 加载 ──────────────────────────────────────────────────────────

    private async Task LoadCoreAsync()
    {
        _index = null;
        _candidatePool.Clear();
        _candidateLoadFailed.Clear();
        _poolsByType.Clear();
        _failedPoolTypes.Clear();
        _parents = null;
        _relationshipByPair.Clear();

        EntityRelationIndex? index;
        try
        {
            index = await EntityRelationIndex.BuildAsync(_store, _novelId);
        }
        catch
        {
            index = null; // 降级：主 Tab 不受影响
        }

        _root.Children.Clear();
        if (index is null)
        {
            _root.Children.Add(Note("关联加载失败", "BrushSageMist"));
            return;
        }

        _index = index;
        await LoadAuxiliaryAsync();
        await LoadCandidatesAsync();
        Render();
    }

    private async Task LoadAuxiliaryAsync()
    {
        try
        {
            foreach (var relationship in await _store.GetRelationshipsByNovel(_novelId))
            {
                var a = relationship.CharacterAId;
                var b = relationship.CharacterBId;
                if (a.Length == 0 || b.Length == 0)
                    continue;
                var key = PairKey(a, b);
                if (!_relationshipByPair.ContainsKey(key))
                    _relationshipByPair[key] = relationship.Id;
            }
        }
        catch
        {
            // 导航降级：人物关系行回退为打开对端角色
        }

        if (_type != EntityType.Location)
            return;
        try
        {
            var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var location in await _store.GetLocations(_novelId))
                parents[location.Id] = string.IsNullOrEmpty(location.ParentId)
                    ? null
                    : location.ParentId;
            _parents = parents;
        }
        catch
        {
            _parents = null; // 防环交由应用层兜底
        }
    }

    private async Task LoadCandidatesAsync()
    {
        foreach (var def in RelationWriteThrough.EditableSectionsFor(_type))
        {
            var key = new SectionKey(def.EdgeType, def.OwnerIsSource, def.CounterpartType);
            try
            {
                _candidatePool[key] = await GetPoolAsync(def.CounterpartType);
            }
            catch
            {
                _candidateLoadFailed.Add(key);
            }
        }
    }

    private async Task<List<(string Name, string Id)>> GetPoolAsync(EntityType counterpartType)
    {
        if (_poolsByType.TryGetValue(counterpartType, out var cached))
            return cached;
        try
        {
            var list = new List<(string Name, string Id)>();
            switch (counterpartType)
            {
                case EntityType.Character:
                    foreach (var c in await _store.GetCharacters(_novelId))
                        list.Add((c.Name, c.Id));
                    break;
                case EntityType.Faction:
                    foreach (var f in await _store.GetFactions(_novelId))
                        list.Add((f.Name, f.Id));
                    break;
                case EntityType.Location:
                    foreach (var l in await _store.GetLocations(_novelId))
                        list.Add((l.Name, l.Id));
                    break;
                case EntityType.Scene:
                    foreach (var s in await _store.GetScenesByNovel(_novelId))
                        list.Add((s.Title, s.Id));
                    break;
                case EntityType.Item:
                    foreach (var i in await _store.GetItems(_novelId))
                        list.Add((i.Name, i.Id));
                    break;
                case EntityType.Plotline:
                    foreach (var p in await _store.GetPlotlines(_novelId))
                        list.Add((p.Name, p.Id));
                    break;
                case EntityType.Chapter:
                    foreach (var c in (await _store.GetChapters(_novelId)).OrderBy(ch => ch.Order))
                        list.Add(
                            (
                                c.Title.Length > 0 ? $"第{c.Order}章 {c.Title}" : $"第{c.Order}章",
                                c.Id
                            )
                        );
                    break;
                case EntityType.Volume:
                    foreach (var v in (await _store.GetVolumes(_novelId)).OrderBy(vol => vol.Order))
                        list.Add(($"第{v.Order}卷 {v.Name}", v.Id));
                    break;
            }
            _poolsByType[counterpartType] = list;
            return list;
        }
        catch
        {
            _failedPoolTypes.Add(counterpartType);
            throw;
        }
    }

    // ── 渲染 ──────────────────────────────────────────────────────────

    private void Render()
    {
        _root.Children.Clear();
        if (_index is null)
        {
            _root.Children.Add(Note("关联加载失败", "BrushSageMist"));
            return;
        }

        var sections = BuildSections(_index);
        ApplyPending(sections);

        var any = false;
        foreach (var section in sections)
        {
            if (section.Rows.Count == 0 && !section.Editable)
                continue;
            any = true;
            _root.Children.Add(RenderSection(section));
        }

        if (!any)
            _root.Children.Add(EmptyNote());
    }

    private List<SectionView> BuildSections(EntityRelationIndex index)
    {
        var defs = RelationWriteThrough.EditableSectionsFor(_type);
        var ranks = new Dictionary<SectionKey, int>();
        for (var i = 0; i < defs.Count; i++)
            ranks[
                new SectionKey(defs[i].EdgeType, defs[i].OwnerIsSource, defs[i].CounterpartType)
            ] = i;

        var list = new List<SectionView>();
        var byKey = new Dictionary<SectionKey, SectionView>();
        var seq = 0;

        foreach (var edge in index.GetRelations(_type, _entityId))
        {
            var rawIsSource =
                edge.SourceType == _type
                && string.Equals(edge.SourceId, _entityId, StringComparison.Ordinal);
            var counterpartType = rawIsSource ? edge.TargetType : edge.SourceType;
            var counterpartId = rawIsSource ? edge.TargetId : edge.SourceId;
            var isSource = IsSymmetric(edge.EdgeType) || rawIsSource; // 对称边方向归一（展示与暂存键共用）
            var key = new SectionKey(edge.EdgeType, isSource, counterpartType);

            if (!byKey.TryGetValue(key, out var section))
            {
                var isEditable = ranks.TryGetValue(key, out var rank);
                section = new SectionView
                {
                    Key = key,
                    Label = EntityRelationIndex.LabelFor(edge, _type, _entityId),
                    Editable = isEditable,
                    Rank = isEditable ? rank : int.MaxValue,
                    Seq = seq++,
                };
                byKey[key] = section;
                list.Add(section);
            }

            if (edge.EdgeType == "adjacent-to")
            {
                // 双端双写产生两条边：折叠为一行，优先本体侧记录
                var existing = section.Rows.Find(r =>
                    string.Equals(r.CounterpartId, counterpartId, StringComparison.Ordinal)
                );
                if (existing is not null)
                {
                    if (rawIsSource && !existing.RawIsSource)
                    {
                        existing.RawIsSource = true;
                        existing.Detail = edge.Detail;
                    }
                    continue;
                }
            }

            section.Rows.Add(
                new RowView
                {
                    CounterpartId = counterpartId,
                    DisplayName = index.NameOf(counterpartType, counterpartId),
                    Detail = edge.Detail,
                    RawIsSource = rawIsSource,
                }
            );
        }

        // 可编辑节：暂无一行的也渲染（保留「＋」入口）
        var syntheticSeq = 100_000;
        for (var i = 0; i < defs.Count; i++)
        {
            var key = new SectionKey(
                defs[i].EdgeType,
                defs[i].OwnerIsSource,
                defs[i].CounterpartType
            );
            if (byKey.ContainsKey(key))
                continue;
            list.Add(
                new SectionView
                {
                    Key = key,
                    Label = LabelForDef(defs[i]),
                    Editable = true,
                    Rank = i,
                    Seq = syntheticSeq++,
                }
            );
        }

        list.Sort(
            (a, b) =>
            {
                var byOrder = EntityRelationIndex
                    .OrderOf(a.Key.EdgeType)
                    .CompareTo(EntityRelationIndex.OrderOf(b.Key.EdgeType));
                if (byOrder != 0)
                    return byOrder;
                if (a.Rank != b.Rank)
                    return a.Rank.CompareTo(b.Rank);
                return a.Seq.CompareTo(b.Seq);
            }
        );
        return list;
    }

    private void ApplyPending(List<SectionView> sections)
    {
        var byKey = new Dictionary<SectionKey, SectionView>();
        foreach (var section in sections)
            byKey[section.Key] = section;

        foreach (var pending in _pending)
        {
            var key = new SectionKey(
                pending.EdgeType,
                pending.OwnerIsSource,
                pending.CounterpartType
            );
            if (!byKey.TryGetValue(key, out var section))
                continue; // 该节当前不可见（对端已删等）：应用时按悬空跳过

            if (pending.Kind == RelationEditKind.Remove)
            {
                var row = section.Rows.Find(r =>
                    !r.PendingAdd
                    && string.Equals(
                        r.CounterpartId,
                        pending.CounterpartId,
                        StringComparison.Ordinal
                    )
                );
                if (row is not null)
                    row.PendingRemove = true;
            }
            else
            {
                section.Rows.Add(
                    new RowView
                    {
                        CounterpartId = pending.CounterpartId,
                        DisplayName = _pendingNames.TryGetValue(
                            (key, pending.CounterpartId),
                            out var name
                        )
                            ? name
                            : _index?.NameOf(pending.CounterpartType, pending.CounterpartId)
                                ?? pending.CounterpartId,
                        Detail = FormatDetail(pending.EdgeType, pending.Detail),
                        PendingAdd = true,
                    }
                );
            }
        }
    }

    private Control RenderSection(SectionView section)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Classes = { "Eyebrow" }, Text = section.Label });
        foreach (var row in section.Rows)
            panel.Children.Add(RenderRow(section, row));
        if (section.Editable)
        {
            var addRow = RenderAddRow(section);
            if (addRow is not null)
                panel.Children.Add(addRow);
        }
        return panel;
    }

    private Control RenderRow(SectionView section, RowView row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var text = row.DisplayName ?? row.CounterpartId;
        if (row.PendingAdd)
            text = $"＋ {text}";
        if (!string.IsNullOrEmpty(row.Detail))
            text = $"{text}（{row.Detail}）";

        var textBlock = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (row.PendingRemove)
            textBlock.TextDecorations = TextDecorations.Strikethrough;

        var button = new Button
        {
            Classes = { "NavPill" },
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4),
            FontSize = 12,
            Opacity = row.PendingRemove ? 0.45 : 1,
            Content = textBlock,
        };
        if (_openCounterpart is not null && section.Key.CounterpartType != EntityType.TimelineEvent)
            button.Click += async (_, _) => await NavigateAsync(section.Key, row);
        Grid.SetColumn(button, 0);
        grid.Children.Add(button);

        if (section.Editable)
        {
            var close = new Button
            {
                Classes = { "IconButton", "Danger" },
                Content = "×",
                Padding = new Thickness(6, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            ToolTip.SetTip(close, row.PendingAdd ? "取消新增" : "移除关联");
            if (row.PendingAdd)
                close.Click += (_, _) => CancelPendingAdd(section.Key, row.CounterpartId);
            else
                close.Click += (_, _) => TogglePendingRemove(section.Key, row.CounterpartId);
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);
        }

        return grid;
    }

    private Control? RenderAddRow(SectionView section)
    {
        if (_candidateLoadFailed.Contains(section.Key))
            return null; // 候选加载失败：隐藏 ＋ 区，行仍显示
        if (!_candidatePool.TryGetValue(section.Key, out var pool))
            return null;

        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in section.Rows)
            if (!row.PendingAdd)
                existing.Add(row.CounterpartId);

        var entries = new List<(string Name, string Id)>();
        foreach (var candidate in pool)
        {
            if (IsSelf(section.Key, candidate.Id))
                continue;
            if (existing.Contains(candidate.Id))
                continue;
            if (HasPendingAdd(section.Key, candidate.Id))
                continue;
            if (!PassesCycleFilter(section.Key, candidate.Id))
                continue;
            entries.Add(candidate);
        }

        var combo = new ComboBox
        {
            Width = 180,
            FontSize = 12,
            PlaceholderText = entries.Count == 0 ? "无候选" : "选择…",
        };
        if (
            Application.Current?.TryFindResource("AdalineComboBox", out var theme) == true
            && theme is ControlTheme controlTheme
        )
            combo.Theme = controlTheme;
        combo.ItemsSource = entries.Select(e => e.Name).ToList();

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        rowPanel.Children.Add(combo);

        TextBox? distanceBox = null;
        if (section.Key.EdgeType == "adjacent-to")
        {
            distanceBox = new TextBox
            {
                Width = 64,
                FontSize = 12,
                PlaceholderText = "距离",
            };
            distanceBox.Classes.Add("Adaline");
            rowPanel.Children.Add(distanceBox);
            rowPanel.Children.Add(
                new TextBlock
                {
                    Text = "里",
                    FontSize = 10,
                    Foreground = GetBrush("BrushSageMist"),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        var addButton = new Button
        {
            Classes = { "NavPill" },
            Content = "＋ 添加",
            Padding = new Thickness(10, 4),
            FontSize = 12,
            IsEnabled = entries.Count > 0,
        };
        rowPanel.Children.Add(addButton);

        var error = new TextBlock
        {
            FontSize = 10,
            Foreground = GetBrush("BrushCrimsonSpecimen"),
            IsVisible = false,
        };

        addButton.Click += (_, _) =>
        {
            var index = combo.SelectedIndex;
            if (index < 0 || index >= entries.Count)
                return;
            string? detail = null;
            if (distanceBox is not null)
            {
                if (!TryParseDistance(distanceBox.Text, out var distance))
                {
                    error.Text = "距离需为正数（里）";
                    error.IsVisible = true;
                    return;
                }
                detail = distance.ToString("0.##", CultureInfo.InvariantCulture);
            }
            AddPending(section.Key, entries[index], detail);
        };

        var wrapper = new StackPanel { Spacing = 2 };
        wrapper.Children.Add(rowPanel);
        wrapper.Children.Add(error);
        return wrapper;
    }

    // ── 暂存编辑 ──────────────────────────────────────────────────────

    private void AddPending(SectionKey key, (string Name, string Id) candidate, string? detail)
    {
        if (
            RelationWriteThrough.IsSingleValue(
                _type,
                key.EdgeType,
                key.OwnerIsSource,
                key.CounterpartType
            )
        )
        {
            // 单值边：撤掉既有新增，再对现有对端全部挂移除（旧行划删线 + 新行）
            _pending.RemoveAll(p => SameKey(p, key) && p.Kind == RelationEditKind.Add);
            foreach (var currentId in CurrentCounterparts(key))
                EnsurePendingRemove(key, currentId);
        }
        else if (HasPendingAdd(key, candidate.Id))
        {
            return; // 同一 (边, 对端) 不重复暂存
        }

        _pending.Add(
            new PendingRelationEdit(
                key.EdgeType,
                key.OwnerIsSource,
                key.CounterpartType,
                candidate.Id,
                RelationEditKind.Add,
                detail
            )
        );
        _pendingNames[(key, candidate.Id)] = candidate.Name;
        PendingChanged?.Invoke();
        Render();
    }

    private void TogglePendingRemove(SectionKey key, string counterpartId)
    {
        var index = _pending.FindIndex(p =>
            SameKey(p, key)
            && p.Kind == RelationEditKind.Remove
            && string.Equals(p.CounterpartId, counterpartId, StringComparison.Ordinal)
        );
        if (index >= 0)
        {
            _pending.RemoveAt(index); // 再点恢复
        }
        else
        {
            _pending.Add(
                new PendingRelationEdit(
                    key.EdgeType,
                    key.OwnerIsSource,
                    key.CounterpartType,
                    counterpartId,
                    RelationEditKind.Remove,
                    null
                )
            );
        }
        PendingChanged?.Invoke();
        Render();
    }

    private void CancelPendingAdd(SectionKey key, string counterpartId)
    {
        _pending.RemoveAll(p =>
            SameKey(p, key)
            && p.Kind == RelationEditKind.Add
            && string.Equals(p.CounterpartId, counterpartId, StringComparison.Ordinal)
        );
        PendingChanged?.Invoke();
        Render();
    }

    private void EnsurePendingRemove(SectionKey key, string counterpartId)
    {
        if (
            _pending.Exists(p =>
                SameKey(p, key)
                && p.Kind == RelationEditKind.Remove
                && string.Equals(p.CounterpartId, counterpartId, StringComparison.Ordinal)
            )
        )
            return;
        _pending.Add(
            new PendingRelationEdit(
                key.EdgeType,
                key.OwnerIsSource,
                key.CounterpartType,
                counterpartId,
                RelationEditKind.Remove,
                null
            )
        );
    }

    private List<string> CurrentCounterparts(SectionKey key)
    {
        var ids = new List<string>();
        if (_index is null)
            return ids;
        foreach (var edge in _index.GetRelations(_type, _entityId))
        {
            var rawIsSource =
                edge.SourceType == _type
                && string.Equals(edge.SourceId, _entityId, StringComparison.Ordinal);
            var counterpartType = rawIsSource ? edge.TargetType : edge.SourceType;
            var counterpartId = rawIsSource ? edge.TargetId : edge.SourceId;
            var isSource = IsSymmetric(edge.EdgeType) || rawIsSource;
            if (
                edge.EdgeType != key.EdgeType
                || isSource != key.OwnerIsSource
                || counterpartType != key.CounterpartType
            )
                continue;
            if (!ids.Contains(counterpartId))
                ids.Add(counterpartId);
        }
        return ids;
    }

    // ── 导航 ──────────────────────────────────────────────────────────

    private async Task NavigateAsync(SectionKey key, RowView row)
    {
        if (_openCounterpart is null || _navigating)
            return;
        var (targetType, targetId) = ResolveTarget(key, row);
        if (targetType is null)
            return;
        _navigating = true;
        try
        {
            await _openCounterpart(targetType.Value, targetId);
        }
        finally
        {
            _navigating = false;
        }
    }

    private (EntityType? Type, string Id) ResolveTarget(SectionKey key, RowView row)
    {
        if (key.CounterpartType == EntityType.TimelineEvent)
            return (null, ""); // 无编辑路由：纯文本行
        if (key.EdgeType == "relationship")
        {
            // 人物关系：打开关系实体编辑窗；回查失败退化为打开对端角色
            var pairKey = PairKey(_entityId, row.CounterpartId);
            return _relationshipByPair.TryGetValue(pairKey, out var relationshipId)
                ? (EntityType.Relationship, relationshipId)
                : (EntityType.Character, row.CounterpartId);
        }
        return (key.CounterpartType, row.CounterpartId);
    }

    // ── 过滤 / 工具 ───────────────────────────────────────────────────

    /// <summary>对称边（方向无关展示与暂存；<see cref="RelationWriteThrough"/> 按同一口径写入）。</summary>
    private static bool IsSymmetric(string edgeType) =>
        edgeType is "adjacent-to" or "relationship" or "faction-relationship";

    private static bool SameKey(PendingRelationEdit edit, SectionKey key) =>
        edit.EdgeType == key.EdgeType
        && edit.OwnerIsSource == key.OwnerIsSource
        && edit.CounterpartType == key.CounterpartType;

    private bool IsSelf(SectionKey key, string candidateId) =>
        key.CounterpartType == _type
        && string.Equals(candidateId, _entityId, StringComparison.Ordinal);

    private bool HasPendingAdd(SectionKey key, string counterpartId) =>
        _pending.Exists(p =>
            SameKey(p, key)
            && p.Kind == RelationEditKind.Add
            && string.Equals(p.CounterpartId, counterpartId, StringComparison.Ordinal)
        );

    /// <summary>地点防环过滤：候选不得构成本体祖先/后代（自环一律拒绝）。</summary>
    private bool PassesCycleFilter(SectionKey key, string candidateId)
    {
        if (key.EdgeType != "contains")
            return true;
        if (_parents is null)
            return true; // 防环表不可用：交由应用层兜底
        return key.OwnerIsSource
            ? !RelationWriteThrough.WouldCycle(
                _parents,
                nodeToReparent: candidateId,
                newParentId: _entityId
            )
            : !RelationWriteThrough.WouldCycle(
                _parents,
                nodeToReparent: _entityId,
                newParentId: candidateId
            );
    }

    private static string? FormatDetail(string edgeType, string? detail) =>
        detail is null ? null
        : edgeType == "adjacent-to" ? $"{detail} 里"
        : detail;

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private string LabelForDef(RelationSectionDef def)
    {
        var relation = def.OwnerIsSource
            ? new EntityRelation(_type, _entityId, def.CounterpartType, "", def.EdgeType, null)
            : new EntityRelation(def.CounterpartType, "", _type, _entityId, def.EdgeType, null);
        return EntityRelationIndex.LabelFor(relation, _type, _entityId);
    }

    private static bool TryParseDistance(string? text, out double distance)
    {
        distance = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        )
            return false;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0)
            return false;
        distance = parsed;
        return true;
    }

    private static IBrush GetBrush(string key) =>
        Application.Current?.TryFindResource(key, out var brush) == true && brush is IBrush b
            ? b
            : Brushes.Gray;

    private static TextBlock Note(string text, string brushKey) =>
        new()
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetBrush(brushKey),
        };

    private static Control EmptyNote()
    {
        var border = new Border { Classes = { "EmptyNote" } };
        border.Child = new TextBlock { Classes = { "Tag" }, Text = "暂无关联记录" };
        return border;
    }

    // ── 内部类型 ──────────────────────────────────────────────────────

    private readonly record struct SectionKey(
        string EdgeType,
        bool OwnerIsSource,
        EntityType CounterpartType
    );

    private sealed class RowView
    {
        public required string CounterpartId { get; init; }

        public string? DisplayName { get; set; }

        public string? Detail { get; set; }

        public bool PendingAdd { get; set; }

        public bool PendingRemove { get; set; }

        /// <summary>索引边的原始方向（相邻折叠时优先本体侧记录）。</summary>
        public bool RawIsSource { get; set; }
    }

    private sealed class SectionView
    {
        public required SectionKey Key { get; init; }

        public required string Label { get; init; }

        public bool Editable { get; set; }

        /// <summary>可编辑节的定义序（同 OrderOf 内的稳定顺序）。</summary>
        public int Rank { get; set; } = int.MaxValue;

        public int Seq { get; set; }

        public List<RowView> Rows { get; } = [];
    }
}
