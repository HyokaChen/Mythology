using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Data;
using Core.Evolution;

namespace Desktop.Controls;

/// <summary>
/// 实体编辑窗口（移植自 TUI EntityEditDialog）：
/// 字段定义驱动 → 动态表单（Tab 分组）→ 反射写回 → INovelStore 保存。
/// </summary>
public partial class EntityEditWindow : Window
{
    private readonly INovelStore _store;
    private readonly EntityType _type;
    private readonly string _novelId;
    private readonly string? _entityId;
    private readonly TabDef[] _tabs;

    /// <summary>prop → 控件。</summary>
    private readonly Dictionary<string, Control> _fields = [];

    /// <summary>Select 的显示名 → 值（枚举字符串或实体 ID）。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _selectMaps = [];

    /// <summary>ID 引用下拉的候选加载任务（回填前必须等待）。</summary>
    private readonly List<Task> _selectLoadTasks = [];

    /// <summary>MultiSelect：prop → 复选框容器。</summary>
    private readonly Dictionary<string, StackPanel> _multiSelectPanels = [];

    /// <summary>MultiSelect：prop → 复选框列表（Id, Box）。</summary>
    private readonly Dictionary<string, List<(string Id, CheckBox Box)>> _multiSelectBoxes = [];

    /// <summary>MultiSelect：prop → 实体当前已选 ID（候选加载时用于勾选）。</summary>
    private readonly Dictionary<string, List<string>> _selectedMultiSelectIds = [];

    private Entity? _entity;

    /// <summary>编辑模式：关联节（挂起编辑 → 保存时写穿透）。</summary>
    private EntityRelationsSection? _relationsSection;

    /// <summary>编辑模式：成员对账快照（OnOpened 采集；保存时 diff 补齐双写）。</summary>
    private MembershipSnapshot _membershipSnapshot = MembershipSnapshot.Empty;

    /// <summary>新建章节时的预置所属卷（null = 未分卷）。</summary>
    private readonly string? _presetVolumeId;

    /// <summary>
    /// 编辑章节时载入的原始状态（保存时先按旧状态落盘，再走 ChapterLifecycleService 发布/撤回，
    /// 否则 FileNovelStore 的章节状态门禁会拒绝直接改 status）。
    /// </summary>
    private ChapterStatus? _originalChapterStatus;

    /// <summary>章节编辑作用域（Full / 细纲 / 内容；只有 Chapter 会按它裁剪页签）。</summary>
    private readonly ChapterEditScope _chapterScope;

    public EntityEditWindow(
        INovelStore store,
        EntityType type,
        string novelId,
        string? entityId,
        string? presetVolumeId = null,
        ChapterEditScope chapterScope = ChapterEditScope.Full
    )
    {
        _store = store;
        _type = type;
        _novelId = novelId;
        _entityId = entityId;
        _presetVolumeId = presetVolumeId;
        _chapterScope = chapterScope;
        _tabs = EntityFieldRegistry.GetTabs(type);

        // 作用域化章节表单：细纲 = 去掉「正文」页签；内容 = 只留「正文」。
        // 保存按页签字段回写（SaveAsync 只遍历 _tabs），缺省字段自动保持原值。
        if (type == EntityType.Chapter && _chapterScope != ChapterEditScope.Full)
            _tabs = EntityFieldRegistry.GetChapterTabs(_chapterScope);

        // 编辑模式：追加「关联」Tab（新建模式尚无任何引用，不渲染；
        // 章节「内容」作用域是整块铺满的正文编辑器、无 Tab 条，同样不渲染）。
        if (entityId is not null && _chapterScope != ChapterEditScope.Content)
            _relationsSection = new EntityRelationsSection(
                store,
                novelId,
                type,
                entityId,
                (t, id) => OpenCounterpartAsync(t, id)
            );

        InitializeComponent();

        Title = entityId is null
            ? $"新建{EntityFieldRegistry.Label(type)}"
            : $"编辑{EntityFieldRegistry.Label(type)}";
        // 作用域化章节：标题随入口变化（细纲 / 内容），不与「编辑章节」混淆
        if (entityId is not null && type == EntityType.Chapter)
            Title = _chapterScope switch
            {
                ChapterEditScope.Outline => "编辑章节细纲",
                ChapterEditScope.Content => "编辑章节内容",
                _ => Title,
            };
        FooterLabel.Text = $"编辑{EntityFieldRegistry.Label(_type)}";
        if (_relationsSection is not null)
            _relationsSection.PendingChanged += UpdateFooterLabel;

        BuildWindow();

        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += async (_, _) => await SaveAsync();
        // Esc 关闭（与其余对话框 / TUI EntityEditDialog 同口径）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>构建窗口内容（TabControl + 字段表单 + 底部按钮）。</summary>
    private void BuildWindow()
    {
        // 章节「内容」作用域：整块正文编辑器直接铺满内容区——没有「内容」标签行、
        // 不套 ScrollViewer、不设 64~160 的固定高度（对齐 TUI：内容直接全部铺满）。
        // 文本顶部起排 + 两端对齐（正文面板不是居中展示的短文案）。
        if (_type == EntityType.Chapter && _chapterScope == ChapterEditScope.Content)
        {
            var def = _tabs[0].Fields[0];
            var editor = new TextBox
            {
                Classes = { "Adaline" },
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                TextAlignment = TextAlignment.Justify,
                Margin = new Thickness(20, 16, 20, 12),
                FontSize = 13,
            };
            _fields[def.Prop] = editor; // 保存路径仍按字段名回写（SaveAsync 遍历同一注册表）
            ContentFillHost.Children.Add(editor);
            ContentFillHost.IsVisible = true;
            EntityTabControl.IsVisible = false;
            return;
        }

        foreach (var tab in _tabs)
        {
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 14 };
            foreach (var def in tab.Fields)
                panel.Children.Add(BuildField(def));
            EntityTabControl.Items.Add(
                new TabItem
                {
                    Header = tab.Name.Length > 0 ? tab.Name : "信息",
                    Content = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = panel,
                    },
                }
            );
        }

        if (_type == EntityType.Novel && _entityId is not null)
        {
            EntityTabControl.Items.Add(
                new TabItem { Header = "世界规则", Content = new WorldRulePanel(_store, _novelId) }
            );
        }

        if (_relationsSection is not null)
            EntityTabControl.Items.Add(
                new TabItem
                {
                    Header = "关联",
                    Content = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20, 16, 20, 16),
                            Children = { _relationsSection.Root },
                        },
                    },
                }
            );
    }

    /// <summary>构建单个字段控件。</summary>
    private StackPanel BuildField(FieldDef def)
    {
        // 字段标签 = 全局 Eyebrow 词汇（Fragment Mono 11px SageGray；此前手写 10px + 裸 hex 与样式漂移）
        var label = new TextBlock { Text = def.Label };
        label.Classes.Add("Eyebrow");

        Control editor = def.Type switch
        {
            FieldType.TextBox => new TextBox
            {
                Classes = { "Adaline" },
                PlaceholderText = def.Placeholder,
                FontSize = 13,
            },
            FieldType.TextArea => new TextBox
            {
                Classes = { "Adaline" },
                PlaceholderText = def.Placeholder,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = def.Prop is "Turnpoints" or "Tags" ? 84 : 64,
                MaxHeight = 160,
                FontSize = 13,
            },
            FieldType.FreeTag => new TextBox
            {
                Classes = { "Adaline" },
                PlaceholderText = def.Placeholder,
                AcceptsReturn = true,
                MinHeight = 64,
                MaxHeight = 120,
                FontSize = 13,
            },
            FieldType.Select => BuildSelect(def),
            FieldType.MultiSelect => BuildMultiSelect(def),
            FieldType.CheckBox => new CheckBox { Content = def.Label, FontSize = 13 },
            // 只读信息（章节号 / 字数 / 更新时间）：值由 PopulateReadOnlyFieldsAsync 预计算回填
            FieldType.ReadOnly => new TextBlock
            {
                FontSize = 13,
                Foreground = Application.Current!.TryFindResource("BrushSageGray", out var gray)
                    ? gray as IBrush
                    : Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            },
            _ => new TextBox { Classes = { "Adaline" } },
        };
        _fields[def.Prop] = editor;

        var wrapper = new StackPanel { Spacing = 6 };
        wrapper.Children.Add(label);
        wrapper.Children.Add(editor);
        return wrapper;
    }

    /// <summary>MultiSelect：复选框列表（Border 容器 + 可滚动候选）。候选由 LoadMultiSelectCandidatesAsync 填充。</summary>
    private Border BuildMultiSelect(FieldDef def)
    {
        var panel = new StackPanel { Spacing = 4 };
        _multiSelectPanels[def.Prop] = panel;

        return new Border
        {
            BorderBrush = Application.Current!.TryFindResource("BrushMist", out var mist)
                ? mist as IBrush
                : Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = new ScrollViewer
            {
                MaxHeight = 150,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel,
            },
        };
    }

    /// <summary>
    /// 加载 MultiSelect 候选（新建模式也需候选）；候选构建时按实体当前已选 ID 勾选。
    /// 无候选时显示占位文案。
    /// </summary>
    private async Task LoadMultiSelectCandidatesAsync()
    {
        foreach (
            var def in _tabs.SelectMany(t => t.Fields).Where(f => f.Type is FieldType.MultiSelect)
        )
        {
            if (!_multiSelectPanels.TryGetValue(def.Prop, out var panel))
                continue;

            var candidates = await LoadMultiSelectCandidatesCoreAsync(def.Prop);
            var selected = _selectedMultiSelectIds.GetValueOrDefault(def.Prop, []);
            var boxes = new List<(string Id, CheckBox Box)>(candidates.Count);
            foreach (var (name, id) in candidates)
            {
                var box = new CheckBox
                {
                    Content = name,
                    FontSize = 13,
                    Tag = id,
                    IsChecked = selected.Contains(id),
                };
                boxes.Add((id, box));
                panel.Children.Add(box);
            }
            _multiSelectBoxes[def.Prop] = boxes;
            if (boxes.Count == 0)
                panel.Children.Add(
                    new TextBlock
                    {
                        Text = "（无候选）",
                        FontSize = 12,
                        Foreground = Application.Current!.TryFindResource(
                            "BrushSageMist",
                            out var mist
                        )
                            ? mist as IBrush
                            : Brushes.Gray,
                    }
                );
        }
    }

    /// <summary>MultiSelect 候选实体（名称 + ID）。</summary>
    private async Task<List<(string Name, string Id)>> LoadMultiSelectCandidatesCoreAsync(
        string prop
    ) =>
        prop switch
        {
            "PlotlineIds" => (await _store.GetPlotlines(_novelId))
                .Select(p => (p.Name, p.Id))
                .ToList(),
            "CharacterIds" or "ParticipantIds" or "Members" => (
                await _store.GetCharacters(_novelId)
            )
                .Select(c => (c.Name, c.Id))
                .ToList(),
            "ItemIds" => (await _store.GetItems(_novelId)).Select(i => (i.Name, i.Id)).ToList(),
            _ => [],
        };

    /// <summary>记录实体当前多选集合（候选勾选依据；仅编辑模式）。</summary>
    private void CollectMultiSelectSelection()
    {
        foreach (
            var def in _tabs.SelectMany(t => t.Fields).Where(f => f.Type is FieldType.MultiSelect)
        )
        {
            var value = _entity?.GetType().GetProperty(def.Prop)?.GetValue(_entity);
            _selectedMultiSelectIds[def.Prop] = value as List<string> ?? [];
        }
    }

    /// <summary>下拉选择：枚举选项 或 ID 引用（异步加载候选）。</summary>
    private ComboBox BuildSelect(FieldDef def)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13,
        };
        // 显式应用 Adaline 模板（代码创建不经过 XAML 样式系统）
        if (
            Application.Current!.TryFindResource("AdalineComboBox", out var theme)
            && theme is Avalonia.Styling.ControlTheme controlTheme
        )
            combo.Theme = controlTheme;

        if (def.Options is not null && def.OptionValues is not null)
        {
            // 枚举选项：显示名 → 持久化值
            var map = new Dictionary<string, string>();
            for (var i = 0; i < def.Options.Length; i++)
                map[def.Options[i]] = def.OptionValues[i];
            _selectMaps[def.Prop] = map;
            combo.ItemsSource = def.Options;
        }
        else if (EntityFieldRegistry.SelectSource(def.Prop) is { } sourceType)
        {
            // ID 引用：候选异步加载，回填在 PopulateFields（必须等任务完成）
            var map = new Dictionary<string, string>();
            _selectMaps[def.Prop] = map;
            _selectLoadTasks.Add(LoadSelectCandidatesAsync(combo, sourceType, map));
        }
        else
        {
            var map = new Dictionary<string, string>();
            _selectMaps[def.Prop] = map;
            combo.ItemsSource = Array.Empty<string>();
        }
        return combo;
    }

    /// <summary>加载 ID 引用下拉的候选列表（是否可空由调用方按字段留空决定）。</summary>
    private async Task LoadSelectCandidatesAsync(
        ComboBox combo,
        EntityType sourceType,
        Dictionary<string, string> map
    )
    {
        try
        {
            var names = new List<string>();
            switch (sourceType)
            {
                case EntityType.Character:
                    foreach (var c in await _store.GetCharacters(_novelId))
                    {
                        map[c.Name] = c.Id;
                        names.Add(c.Name);
                    }
                    break;
                case EntityType.Faction:
                    foreach (var f in await _store.GetFactions(_novelId))
                    {
                        map[f.Name] = f.Id;
                        names.Add(f.Name);
                    }
                    break;
                case EntityType.Location:
                    foreach (var l in await _store.GetLocations(_novelId))
                    {
                        map[l.Name] = l.Id;
                        names.Add(l.Name);
                    }
                    break;
                case EntityType.Chapter:
                    foreach (var ch in (await _store.GetChapters(_novelId)).OrderBy(c => c.Order))
                    {
                        var name =
                            ch.Title.Length > 0 ? $"{ch.Order}. {ch.Title}" : $"第{ch.Order}章";
                        map[name] = ch.Id;
                        names.Add(name);
                    }
                    break;
                case EntityType.Volume:
                    foreach (var v in (await _store.GetVolumes(_novelId)).OrderBy(v => v.Order))
                    {
                        var name = v.Name.Length > 0 ? $"第{v.Order}卷 {v.Name}" : $"第{v.Order}卷";
                        map[name] = v.Id;
                        names.Add(name);
                    }
                    break;
            }

            // 允许清空（不选）
            names.Insert(0, "（不设置）");
            map["（不设置）"] = "";
            combo.ItemsSource = names;
        }
        catch
        {
            // 候选加载失败：留空（用户仍可保存其它字段）
        }
    }

    /// <summary>显示后异步加载实体数据与候选，再统一回填。</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            if (_entityId is not null)
            {
                _entity = await LoadEntityAsync();
                if (_entity is null)
                    return;
                if (_entity is Chapter loadedChapter)
                    _originalChapterStatus = loadedChapter.Status;
                CollectMultiSelectSelection();
                _membershipSnapshot = RelationWriteThrough.Capture(_entity);
            }

            await LoadMultiSelectCandidatesAsync();
            await Task.WhenAll(_selectLoadTasks);

            if (_entity is not null)
            {
                PopulateFields();
                await PopulateReadOnlyFieldsAsync();
                if (_relationsSection is not null)
                    await _relationsSection.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            await new MessageBox("加载失败", ex.Message).ShowDialogAsync(this);
        }
    }

    /// <summary>关联行导航：打开对端实体编辑窗（叠加于当前窗之上；关闭后重建关联数据，保留暂存）。</summary>
    private async Task OpenCounterpartAsync(EntityType type, string entityId)
    {
        var window = new EntityEditWindow(_store, type, _novelId, entityId);
        await window.ShowDialog(this);
        if (_relationsSection is not null)
            await _relationsSection.ReloadAsync();
    }

    /// <summary>底部暂存计数：关联改动 N 项（N&gt;0 时替代默认文案）。</summary>
    private void UpdateFooterLabel()
    {
        var count = _relationsSection?.PendingEdits.Count ?? 0;
        FooterLabel.Text =
            count > 0 ? $"关联改动 {count} 项" : $"编辑{EntityFieldRegistry.Label(_type)}";
    }

    /// <summary>按类型加载实体。</summary>
    private async Task<Entity?> LoadEntityAsync() =>
        _type switch
        {
            EntityType.Character => await _store.GetCharacter(_entityId!),
            EntityType.Plotline => await _store.GetPlotline(_entityId!),
            EntityType.Faction => await _store.GetFaction(_entityId!),
            EntityType.Location => await _store.GetLocation(_entityId!),
            EntityType.Scene => await _store.GetScene(_novelId, _entityId!),
            EntityType.Item => await _store.GetItem(_novelId, _entityId!),
            EntityType.Relationship => await _store.GetRelationship(_novelId, _entityId!),
            EntityType.Chapter => await _store.GetChapter(_novelId, _entityId!),
            EntityType.Volume => await _store.GetVolume(_novelId, _entityId!),
            EntityType.Novel => await _store.GetNovel(_entityId!),
            EntityType.Note => (await _store.GetNotes(_novelId)).FirstOrDefault(n =>
                n.Id == _entityId!
            ),
            _ => null,
        };

    /// <summary>将实体值填入表单控件。</summary>
    private void PopulateFields()
    {
        foreach (var def in _tabs.SelectMany(t => t.Fields))
        {
            if (!_fields.TryGetValue(def.Prop, out var control) || _entity is null)
                continue;
            var value = _entity.GetType().GetProperty(def.Prop)?.GetValue(_entity);

            switch (control)
            {
                case CheckBox box:
                    box.IsChecked = value is true;
                    break;
                case TextBox tb when def.Type is FieldType.FreeTag && value is List<string> tags:
                    tb.Text = string.Join("\n", tags);
                    break;
                case TextBox tb:
                    tb.Text = value?.ToString() ?? "";
                    break;
                case ComboBox combo:
                {
                    if (def.Options is not null && def.OptionValues is not null)
                    {
                        var idx = Array.IndexOf(def.OptionValues, value?.ToString());
                        // 旧数据兜底：早期版本存的是中文展示值（如 Gender 的「男」），
                        // 按显示名再匹配一次（TUI EntityEditDialog 同款回落）。
                        if (idx < 0 && value is string legacy)
                            idx = Array.IndexOf(def.Options, legacy);
                        combo.SelectedIndex = idx >= 0 ? idx : -1;
                    }
                    else if (_selectMaps.TryGetValue(def.Prop, out var map))
                    {
                        var id = value as string ?? "";
                        var name = map.FirstOrDefault(kv => kv.Value == id).Key ?? "（不设置）";
                        combo.SelectedItem = name;
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 只读信息回填（章节号 / 字数 / 更新时间）：不在模型属性上、不参与保存，
    /// 打开窗口时按当前数据预计算（对齐 TUI EntityEditDialog 的 ReadOnly 字段）。
    /// </summary>
    private async Task PopulateReadOnlyFieldsAsync()
    {
        if (_type == EntityType.Note && _entity is AuthorNote note)
        {
            SetReadOnlyText(
                "CapturedChapterOrder",
                note.CapturedChapterOrder is { } order ? $"第{order}章" : "—"
            );
            return;
        }

        if (_type != EntityType.Chapter || _entity is not Chapter chapter)
            return;
        SetReadOnlyText("ChapterNumber", await ChapterNumberOfAsync(chapter));
        SetReadOnlyText(
            "CharCount",
            Core.Novels.OutlineRowText.FormatWords(chapter.CharCount) + " 字"
        );
        SetReadOnlyText("UpdatedAt", chapter.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>向只读字段（TextBlock）回填文本；字段不在当前页签集合时静默跳过。</summary>
    private void SetReadOnlyText(string prop, string text)
    {
        if (_fields.TryGetValue(prop, out var control) && control is TextBlock block)
            block.Text = text;
    }

    /// <summary>
    /// 章节的卷内序号（1 起；同卷内按 <see cref="Chapter.Order" /> 升序数，换卷从 1 重新计数；
    /// 未分卷的章节算作同一组）。与全书全局 Order 区分——展示用卷内号，落库 / 排序仍用全局 Order。
    /// </summary>
    private async Task<string> ChapterNumberOfAsync(Chapter ch)
    {
        var siblings = (await _store.GetChapters(ch.NovelId))
            .Where(c =>
                string.Equals(c.VolumeId ?? "", ch.VolumeId ?? "", StringComparison.Ordinal)
            )
            .OrderBy(c => c.Order)
            .ToList();
        var index = siblings.FindIndex(c => string.Equals(c.Id, ch.Id, StringComparison.Ordinal));
        return index < 0 ? "—" : $"第 {index + 1} 章";
    }

    /// <summary>收集表单值 → 写回实体 → 保存。</summary>
    private async Task SaveAsync()
    {
        try
        {
            _entity ??= CreateEntity();
            if (_entity is null)
                return;

            foreach (var def in _tabs.SelectMany(t => t.Fields))
            {
                if (!_fields.TryGetValue(def.Prop, out var control))
                    continue;

                var prop = _entity.GetType().GetProperty(def.Prop);
                if (prop is null)
                    continue; // 不在模型上的字段（防御）

                if (def.Type is FieldType.CheckBox)
                {
                    prop.SetValue(_entity, control is CheckBox { IsChecked: true });
                    continue;
                }

                if (def.Type is FieldType.MultiSelect)
                {
                    var ids = _multiSelectBoxes
                        .GetValueOrDefault(def.Prop, [])
                        .Where(b => b.Box.IsChecked == true)
                        .Select(b => b.Id)
                        .ToList();
                    prop.SetValue(_entity, ids);
                    continue;
                }

                switch (control)
                {
                    case TextBox { AcceptsReturn: false } tb:
                        prop.SetValue(
                            _entity,
                            CoerceValue(prop.PropertyType, tb.Text?.Trim() ?? "")
                        );
                        break;
                    case TextBox tb when def.Type is FieldType.FreeTag:
                        prop.SetValue(
                            _entity,
                            tb.Text?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .ToList()
                                ?? []
                        );
                        break;
                    case TextBox tb:
                        prop.SetValue(
                            _entity,
                            CoerceValue(prop.PropertyType, tb.Text?.Trim() ?? "")
                        );
                        break;
                    case ComboBox combo:
                        var display = combo.SelectedItem as string ?? "";
                        var raw = _selectMaps.TryGetValue(def.Prop, out var map)
                            ? map.GetValueOrDefault(display, display)
                            : display;
                        prop.SetValue(_entity, CoerceValue(prop.PropertyType, raw));
                        break;
                }
            }

            if (!await SaveEntityAsync(_entity))
                return; // 生命周期对话框取消：窗口保留，用户可继续编辑或关闭
            Close();
        }
        catch (Exception ex)
        {
            await new MessageBox("保存失败", ex.Message).ShowDialogAsync(this);
        }
    }

    /// <summary>创建新实体实例。</summary>
    private Entity? CreateEntity() =>
        _type switch
        {
            EntityType.Character => new Character { Id = Ulid.NewUlid().ToString() },
            EntityType.Plotline => new Plotline { Id = Ulid.NewUlid().ToString() },
            EntityType.Faction => new Faction { Id = Ulid.NewUlid().ToString() },
            EntityType.Location => new Core.Data.Location { Id = Ulid.NewUlid().ToString() },
            EntityType.Scene => new SceneDefinition { Id = Ulid.NewUlid().ToString() },
            EntityType.Item => new Item { Id = Ulid.NewUlid().ToString() },
            EntityType.Relationship => new Relationship { Id = Ulid.NewUlid().ToString() },
            EntityType.Chapter => new Chapter
            {
                Id = Ulid.NewUlid().ToString(),
                NovelId = _novelId,
                VolumeId = _presetVolumeId,
            },
            EntityType.Volume => new Volume { Id = Ulid.NewUlid().ToString(), NovelId = _novelId },
            EntityType.Note => new AuthorNote
            {
                Id = Ulid.NewUlid().ToString(),
                NovelId = _novelId,
            },
            _ => null,
        };

    /// <summary>
    /// 保存实体并处理章节状态生命周期：状态有变化时先按旧状态落盘（保留字段编辑），
    /// 再经 <see cref="ChapterLifecycleService"/> 发布（预览 → 勾选项 → 应用）或撤回（预览 → 确认 → 回滚）。
    /// 用户取消 → 保持旧状态；发布/撤回失败 → 抛异常（窗口保留并提示，不关闭）。
    /// 同存储重叠时后者胜（规划 §4.7）。
    /// </summary>
    private async Task<bool> SaveEntityAsync(Entity entity)
    {
        var targetStatus =
            entity is Chapter chapter
            && _entityId is not null
            && _originalChapterStatus is { } original
            && original != chapter.Status
                ? chapter.Status
                : (ChapterStatus?)null;

        if (targetStatus is not null)
        {
            // 先按旧状态落盘：章节状态只允许生命周期服务修改（FileNovelStore 门禁），
            // 字段编辑（标题 / 细纲 / 换卷）仍需要保存。
            ((Chapter)entity).Status = _originalChapterStatus!.Value;
        }

        try
        {
            await PersistEntityAsync(entity);
        }
        finally
        {
            if (targetStatus is not null)
                ((Chapter)entity).Status = targetStatus.Value;
        }

        if (targetStatus is { } status)
            return await ApplyChapterLifecycleAsync((Chapter)entity, status);
        return true;
    }

    /// <summary>章节状态迁移：草稿 → 发布（预览 + 选项）/ 发布 → 草稿（预览 + 确认）。</summary>
    private async Task<bool> ApplyChapterLifecycleAsync(Chapter chapter, ChapterStatus target)
    {
        var service = new ChapterLifecycleService(_store);
        if (target == ChapterStatus.Published)
        {
            var preview = await service.PreviewPublishAsync(_novelId, chapter.Id);
            var window = new ChapterPublishWindow(preview);
            var options = await window.ShowAsync(this);
            if (preview.HasBlockingIssues)
            {
                throw new InvalidOperationException(
                    "发布预检未通过："
                        + string.Join(
                            "；",
                            preview
                                .ValidationIssues.Where(i => i.BlocksPublish)
                                .Take(5)
                                .Select(i => i.Message)
                        )
                );
            }
            if (options is null)
                return false; // 取消：保持草稿（字段已保存）

            var result = await service.PublishAsync(_novelId, chapter.Id, options);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "发布失败");
            return true;
        }

        var unpublishPreview = await service.PreviewUnpublishAsync(_novelId, chapter.Id);
        var unpublishDialog = new ChapterUnpublishWindow(chapter.Title, unpublishPreview);
        var confirmed = await unpublishDialog.ShowAsync(this);
        if (!unpublishDialog.CanUnpublish)
            throw new InvalidOperationException(
                "无法撤回：" + ChapterUnpublishWindow.DescribeBlockers(unpublishPreview)
            );
        if (!confirmed)
            return false; // 取消：保持发布

        var unpublish = await service.UnpublishAsync(_novelId, chapter.Id);
        if (!unpublish.Success)
            throw new InvalidOperationException(unpublish.Error ?? "撤回失败");
        return true;
    }

    /// <summary>
    /// 保存实体：字段 → 成员对账 → 关联编辑，统一落盘（手动写入：跳过 AI 冲突门禁，与章节阅读器一致）。
    /// 同存储重叠时后者胜（规划 §4.7）；异常向上抛（窗口保留，无回滚）。
    /// </summary>
    private async Task PersistEntityAsync(Entity entity)
    {
        if (string.IsNullOrEmpty(entity.NovelId))
            entity.NovelId = _novelId;

        // 新建场景无「序号」字段：按同小说场景数自动编号（对齐 TUI）。
        if (entity is SceneDefinition scene && _entityId is null)
            scene.Order = (await _store.GetScenesByNovel(_novelId)).Count + 1;

        // 新建且未填序号 → 插入到所选卷末尾（该卷尾号已被别卷占用时先全章重排）。
        if (_entityId is null && entity is Chapter newChapter && newChapter.Order <= 0)
        {
            var all = await _store.GetChapters(_novelId);
            var plan = ChapterOrdering.PlanInsert(all, null, newChapter.VolumeId);
            if (plan.RequiresReorder)
            {
                var assignments = ChapterOrdering.PlanFullReorder(
                    all,
                    insertAfterChapterId: null,
                    targetGroupKey: ChapterOrdering.GroupKey(newChapter.VolumeId)
                );
                newChapter.Order = assignments
                    .First(a => a.ChapterId == ChapterOrdering.InsertSlotId)
                    .Order;
                await _store.ReorderAllChapters(_novelId, assignments);
            }
            else
            {
                newChapter.Order = plan.Order;
            }
        }
        if (_entityId is null && entity is Volume newVolume && newVolume.Order <= 0)
            newVolume.Order = ChapterOrdering.NextVolumeOrder(await _store.GetVolumes(_novelId));

        // 成员对账 + 挂起关联编辑；与落盘同处手动写入作用域：跳过 AI 冲突门禁，异常也能恢复。
        IReadOnlyList<PendingRelationEdit> edits = [];
        if (_relationsSection is not null)
            edits = _relationsSection.PendingEdits;

        if (_store is FileNovelStore fns)
        {
            using (fns.BeginManualWrite())
                await RelationWriteThrough.ApplyAllAsync(
                    _store,
                    _novelId,
                    entity,
                    _membershipSnapshot,
                    edits
                );
        }
        else
        {
            await RelationWriteThrough.ApplyAllAsync(
                _store,
                _novelId,
                entity,
                _membershipSnapshot,
                edits
            );
        }
    }

    /// <summary>字符串 → 目标类型（字符串 / 枚举 / 整数 / 浮点 / 布尔）。</summary>
    private static object? CoerceValue(Type targetType, string value)
    {
        if (targetType == typeof(string))
            return value.Length == 0 ? null : value;
        if (targetType.IsEnum)
            return Enum.TryParse(targetType, value, ignoreCase: true, out var parsed)
                ? parsed
                : Activator.CreateInstance(targetType);
        if (targetType == typeof(int))
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var i
            )
                ? i
                : 0;
        if (targetType == typeof(double))
            return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var d
            )
                ? d
                : 0d;
        if (targetType == typeof(bool))
            return bool.TryParse(value, out var b) && b;
        return value;
    }
}
