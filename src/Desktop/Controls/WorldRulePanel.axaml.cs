using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 小说详情里的「世界规则」面板（移植 TUI WorldRulePanel）：
/// 规则（四类机制）与设定（描述性世界观）的增删改查、按领域筛选。
/// 规则/设定即时写入 <c>world-rules.yaml</c>；校验与摘要统一走 <see cref="WorldRulePresentation"/>。
/// </summary>
public partial class WorldRulePanel : UserControl
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly List<WorldRule> _rules = [];
    private readonly Dictionary<EntityType, (string[] Names, string[] Ids)> _entityOptions = [];

    private readonly ComboBox _ruleFilter;
    private readonly ComboBox _settingFilter;
    private readonly ComboBox _ruleCategory;
    private readonly ComboBox _ruleKind;
    private readonly ComboBox _preset;
    private readonly ComboBox _operator;
    private readonly ComboBox _subjectType;
    private readonly ComboBox _subjectId;
    private readonly ComboBox _effectKind;
    private readonly ComboBox _effectTargetType;

    private readonly ComboBox _effectTargetId;
    private readonly ComboBox _effectAspect;
    private readonly TextBox _ruleDescription;
    private readonly TextBox _valueBox;
    private readonly TextBox _unitBox;
    private readonly TextBox _metricBox;
    private readonly TextBox _thresholdBox;
    private readonly TextBox _actionBox;
    private readonly TextBox _requiredStateBox;
    private readonly TextBox _forbiddenActionBox;
    private readonly TextBox _triggerActionBox;
    private readonly TextBox _effectFrom;
    private readonly TextBox _effectTo;
    private readonly TextBox _effectSummary;
    private readonly TextBlock _ruleError;
    private readonly StackPanel _cloudRow;
    private readonly StackPanel _numericRow;
    private readonly StackPanel _requiresRow;
    private readonly StackPanel _forbidsRow;
    private readonly StackPanel _triggersRow;
    private readonly Control _subjectIdRow;
    private readonly StackPanel _effectSection;
    private readonly ComboBox _settingCategory;
    private readonly TextBox _settingTitle;
    private readonly TextBox _settingContent;
    private readonly TextBlock _settingError;

    private WorldRule? _editingRule;
    private WorldRule? _editingSetting;
    private WorldRuleCategory? _ruleFilterValue;
    private WorldRuleCategory? _settingFilterValue;
    private string?[] _subjectIdValues = [];
    private string?[] _effectTargetIdValues = [];
    private bool _loaded;

    public WorldRulePanel(INovelStore store, string novelId)
    {
        _store = store;
        _novelId = novelId;
        InitializeComponent();

        _ruleFilter = RuleCategoryFilter;
        _settingFilter = SettingCategoryFilter;
        _ruleFilter.ItemsSource = CategoryOptions();
        _ruleFilter.SelectedIndex = 0;
        _ruleFilter.SelectionChanged += (_, _) =>
        {
            _ruleFilterValue =
                _ruleFilter.SelectedIndex > 0
                    ? WorldRulePresentation.Categories[_ruleFilter.SelectedIndex - 1].Value
                    : null;
            RebuildRuleList();
        };
        _settingFilter.ItemsSource = CategoryOptions();
        _settingFilter.SelectedIndex = 0;
        _settingFilter.SelectionChanged += (_, _) =>
        {
            _settingFilterValue =
                _settingFilter.SelectedIndex > 0
                    ? WorldRulePresentation.Categories[_settingFilter.SelectedIndex - 1].Value
                    : null;
            RebuildSettingList();
        };

        _ruleCategory = NewCombo(CategoryLabels());
        _ruleDescription = NewText("说明 / 地点距离时填 地点A>地点B");
        _ruleKind = NewCombo(WorldRulePresentation.RuleKinds.Select(k => k.Label).ToArray());
        _preset = NewCombo(
            new[] { "不使用（本地规则）" }.Concat(WorldRuleTypes.All.Select(t => t.Label)).ToArray()
        );
        _valueBox = NewText("数值，如 300");
        _unitBox = NewText("单位，如 里/日");
        _metricBox = NewText("指标，如 speed");
        _operator = NewCombo(WorldRulePresentation.Operators.Select(o => o.Label).ToArray());
        _thresholdBox = NewText("阈值，如 100");
        _actionBox = NewText("行动，如 御剑");
        _requiredStateBox = NewText("所需状态，如 金丹");
        _forbiddenActionBox = NewText("被禁止的行动，如 御剑");
        _triggerActionBox = NewText("触发条件，如 月圆");
        _subjectType = NewCombo(
            new[] { "不限" }
                .Concat(WorldRulePresentation.SubjectTypes.Select(t => t.Label))
                .ToArray()
        );
        _subjectId = NewCombo(["全部"]);
        _effectKind = NewCombo(WorldRulePresentation.DeltaKinds.Select(k => k.Label).ToArray());
        _effectTargetType = NewCombo(
            WorldRulePresentation.TargetTypes.Select(t => t.Label).ToArray()
        );
        _effectTargetId = NewCombo(["留空=主体"]);
        _effectAspect = NewCombo(WorldRulePresentation.Aspects.Select(a => a.Label).ToArray());
        _effectFrom = NewText("变化前");
        _effectTo = NewText("变化后");
        _effectSummary = NewText("后果摘要，如：御剑失败");
        _ruleError = NewErrorText();

        _cloudRow = new StackPanel { Spacing = 4 };
        AddRow(_cloudRow, "数值", _valueBox);
        AddRow(_cloudRow, "单位", _unitBox);
        _numericRow = new StackPanel { Spacing = 4 };
        AddRow(_numericRow, "指标", _metricBox);
        AddRow(_numericRow, "比较符", _operator);
        AddRow(_numericRow, "阈值", _thresholdBox);
        _requiresRow = new StackPanel { Spacing = 4 };
        AddRow(_requiresRow, "行动", _actionBox);
        AddRow(_requiresRow, "所需状态", _requiredStateBox);
        _forbidsRow = new StackPanel { Spacing = 4 };
        AddRow(_forbidsRow, "被禁止的行动", _forbiddenActionBox);
        _triggersRow = new StackPanel { Spacing = 4 };
        AddRow(_triggersRow, "触发条件", _triggerActionBox);
        _subjectIdRow = NewRow("主体", _subjectId);
        _effectSection = new StackPanel { Spacing = 4 };
        AddRow(_effectSection, "后果类型", _effectKind);
        AddRow(_effectSection, "目标类型", _effectTargetType);
        AddRow(_effectSection, "目标", _effectTargetId);
        AddRow(_effectSection, "变化面", _effectAspect);
        AddRow(_effectSection, "变化前", _effectFrom);
        AddRow(_effectSection, "变化后", _effectTo);
        AddRow(_effectSection, "摘要", _effectSummary);

        _settingCategory = NewCombo(CategoryLabels());
        _settingTitle = NewText("设定标题，如：宗门礼制");
        _settingContent = new TextBox
        {
            Classes = { "Adaline" },
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 120,
            FontSize = 12.5,
        };
        _settingError = NewErrorText();

        BuildRuleForm();
        BuildSettingForm();
        RuleFormScroll.IsVisible = false;
        SettingFormScroll.IsVisible = false;
        UpdateRuleFieldVisibility();

        Loaded += async (_, _) => await LoadAsync();
    }

    private ILogger<WorldRulePanel>? Log =>
        (ILogger<WorldRulePanel>?)Program.Services?.GetService(typeof(ILogger<WorldRulePanel>));

    private void BuildRuleForm()
    {
        var host = RuleFormHost;
        host.Children.Add(FormTitle("规则详情"));
        host.Children.Add(_ruleError);
        AddRow(host, "领域", _ruleCategory);
        AddRow(host, "说明", _ruleDescription);
        AddRow(host, "机制", _ruleKind);
        AddRow(host, "云端检查", _preset);
        host.Children.Add(_cloudRow);
        host.Children.Add(Hint("切换「云端检查 / 本地规则」会清除另一模式的字段。"));
        host.Children.Add(_numericRow);
        host.Children.Add(_requiresRow);
        host.Children.Add(_forbidsRow);
        host.Children.Add(_triggersRow);
        AddRow(host, "作用主体", _subjectType);
        host.Children.Add(_subjectIdRow);
        host.Children.Add(_effectSection);
        host.Children.Add(
            Hint(
                "行动/状态 token 只匹配结构化字段（角色 Tags/Abilities/Status、组织 Type/Goal、地点 Type/Atmosphere、道具 Type/Description）；数值指标来自各实体的 Metrics。不从正文猜。"
            )
        );
        host.Children.Add(
            Buttons(("取消", CancelRule_OnClick, false), ("保存", SaveRule_OnClick, true))
        );

        _ruleKind.SelectionChanged += (_, _) => UpdateRuleFieldVisibility();
        _preset.SelectionChanged += (_, _) =>
        {
            var index = _preset.SelectedIndex;
            _unitBox.Text =
                index > 0 && index - 1 < WorldRuleTypes.All.Length
                    ? WorldRuleTypes.All[index - 1].UnitHint
                    : "";
            UpdateRuleFieldVisibility();
        };
        _subjectType.SelectionChanged += (_, _) =>
        {
            PopulateSubjectIds();
            if (_subjectType.SelectedIndex > 0 && _effectTargetType.SelectedIndex == 0)
            {
                _effectTargetType.SelectedIndex = _subjectType.SelectedIndex - 1;
                PopulateTargetIds();
            }
            UpdateRuleFieldVisibility();
        };
        _effectTargetType.SelectionChanged += (_, _) => PopulateTargetIds();
    }

    private void BuildSettingForm()
    {
        var host = SettingFormHost;
        host.Children.Add(FormTitle("设定详情"));
        host.Children.Add(_settingError);
        AddRow(host, "领域", _settingCategory);
        AddRow(host, "标题", _settingTitle);
        AddRow(host, "正文", _settingContent);
        host.Children.Add(
            Buttons(("取消", CancelSetting_OnClick, false), ("保存", SaveSetting_OnClick, true))
        );
    }

    private async Task LoadAsync()
    {
        if (_loaded)
            return;
        _loaded = true;

        string? loadError = null;
        try
        {
            _rules.Clear();
            _rules.AddRange(await _store.GetWorldRules(_novelId));
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "加载世界规则失败 novelId={Id}", _novelId);
            loadError = $"加载世界规则失败：{ex.Message}";
        }

        try
        {
            var characters = await _store.GetCharacters(_novelId);
            _entityOptions[EntityType.Character] = (
                characters.Select(c => c.Name).ToArray(),
                characters.Select(c => c.Id).ToArray()
            );
            var factions = await _store.GetFactions(_novelId);
            _entityOptions[EntityType.Faction] = (
                factions.Select(f => f.Name).ToArray(),
                factions.Select(f => f.Id).ToArray()
            );
            var locations = await _store.GetLocations(_novelId);
            _entityOptions[EntityType.Location] = (
                locations.Select(l => l.Name).ToArray(),
                locations.Select(l => l.Id).ToArray()
            );
            var items = await _store.GetItems(_novelId);
            _entityOptions[EntityType.Item] = (
                items.Select(i => i.Name).ToArray(),
                items.Select(i => i.Id).ToArray()
            );
            var plotlines = await _store.GetPlotlines(_novelId);
            _entityOptions[EntityType.Plotline] = (
                plotlines.Select(p => p.Name).ToArray(),
                plotlines.Select(p => p.Id).ToArray()
            );
            var chapters = (await _store.GetStructure(_novelId))?.Chapters ?? [];
            _entityOptions[EntityType.Chapter] = (
                chapters.Select(c => $"{c.Order}. {c.Title}").ToArray(),
                chapters.Select(c => c.Id).ToArray()
            );
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "加载世界规则可选实体失败 novelId={Id}", _novelId);
            loadError ??= $"加载可选实体失败：{ex.Message}";
        }

        StatusText.Text = loadError ?? "";
        StatusText.IsVisible = loadError is not null;
        RebuildRuleList();
        RebuildSettingList();
    }

    private void RebuildRuleList()
    {
        var host = RulesList;
        host.Children.Clear();
        var visible = _rules
            .Where(rule =>
                rule.EntryKind == WorldRuleEntryKind.Rule
                && (_ruleFilterValue is null || rule.Category == _ruleFilterValue)
            )
            .ToList();
        if (visible.Count == 0)
        {
            host.Children.Add(Empty("（暂无规则）声明后才会参与素材提示与云端检查。"));
            return;
        }
        foreach (
            var group in visible
                .GroupBy(rule => rule.Category)
                .OrderBy(group => CategoryOrder(group.Key))
        )
        {
            host.Children.Add(GroupHeader(WorldRulePresentation.CategoryLabel(group.Key)));
            foreach (var rule in group.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id))
                host.Children.Add(BuildCard(rule));
        }
    }

    private void RebuildSettingList()
    {
        var host = SettingsList;
        host.Children.Clear();
        var visible = _rules
            .Where(rule =>
                rule.EntryKind == WorldRuleEntryKind.Setting
                && (_settingFilterValue is null || rule.Category == _settingFilterValue)
            )
            .ToList();
        if (visible.Count == 0)
        {
            host.Children.Add(Empty("（暂无设定）记录历史、文化、地理、力量体系等描述性内容。"));
            return;
        }
        foreach (
            var group in visible
                .GroupBy(rule => rule.Category)
                .OrderBy(group => CategoryOrder(group.Key))
        )
        {
            host.Children.Add(GroupHeader(WorldRulePresentation.CategoryLabel(group.Key)));
            foreach (var rule in group.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id))
                host.Children.Add(BuildCard(rule));
        }
    }

    private Control BuildCard(WorldRule rule)
    {
        var title = string.IsNullOrWhiteSpace(rule.Description)
            ? rule.EntryKind == WorldRuleEntryKind.Setting
                ? "未命名设定"
                : "未命名规则"
            : rule.Description;
        var badge =
            rule.EntryKind == WorldRuleEntryKind.Setting
                ? "设定"
                : WorldRulePresentation.RuleKindLabel(rule.RuleKind);
        var detail =
            rule.EntryKind == WorldRuleEntryKind.Setting
                ? (rule.Content ?? "").ReplaceLineEndings(" ")
                : WorldRulePresentation.SummaryFor(rule);

        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(
            new TextBlock
            {
                Text = $"{title} · {badge}",
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = TryBrush("BrushForestInk"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        textStack.Children.Add(
            new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Foreground = TryBrush("BrushSageGray"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var edit = SmallButton("编辑");
        edit.Click += (_, _) => BeginEdit(rule);
        var history = SmallButton("历史");
        history.Click += (_, _) => ShowHistoryAsync(rule);
        var delete = SmallButton("删除", danger: true);
        delete.Click += (_, _) => ConfirmDelete(rule);
        buttons.Children.Add(edit);
        buttons.Children.Add(history);
        buttons.Children.Add(delete);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(textStack);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Classes = { "Card", "Glow" },
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid,
        };
        return card;
    }

    private void BeginEdit(WorldRule rule)
    {
        if (rule.EntryKind == WorldRuleEntryKind.Rule)
        {
            _editingRule = rule;
            PopulateRuleForm(rule);
            RuleFormScroll.IsVisible = true;
            ViewTabs.SelectedIndex = 0;
        }
        else
        {
            _editingSetting = rule;
            PopulateSettingForm(rule);
            SettingFormScroll.IsVisible = true;
            ViewTabs.SelectedIndex = 1;
        }
    }

    private void NewRule_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetRuleForm();
        RuleFormScroll.IsVisible = true;
        ViewTabs.SelectedIndex = 0;
    }

    private void NewSetting_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ResetSettingForm();
        SettingFormScroll.IsVisible = true;
        ViewTabs.SelectedIndex = 1;
    }

    private void CancelRule_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _editingRule = null;
        RuleFormScroll.IsVisible = false;
    }

    private void CancelSetting_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _editingSetting = null;
        SettingFormScroll.IsVisible = false;
    }

    private async void SaveRule_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        var presetIndex = _preset.SelectedIndex;
        var rule = new WorldRule
        {
            Id = _editingRule?.Id ?? NewId(),
            NovelId = _novelId,
            EntryKind = WorldRuleEntryKind.Rule,
            Category = SelectedCategory(_ruleCategory),
            Description = TextOf(_ruleDescription),
        };

        if (presetIndex > 0)
        {
            if (
                presetIndex - 1 >= WorldRuleTypes.All.Length
                || !TryParseDouble(_valueBox.Text, out var value)
            )
            {
                ShowRuleError("数值格式不正确");
                return;
            }
            var (key, _, unitHint) = WorldRuleTypes.All[presetIndex - 1];
            rule.RuleKind = RuleKind.Numeric;
            rule.Type = key;
            rule.Value = value;
            rule.Unit = unitHint;
        }
        else
        {
            rule.RuleKind = SelectedRuleKind(_ruleKind);
            if (rule.RuleKind == RuleKind.Numeric)
            {
                if (!TryParseNullableDouble(_thresholdBox.Text, out var threshold))
                {
                    ShowRuleError("数值格式不正确");
                    return;
                }
                rule.Metric = TextOf(_metricBox);
                rule.Operator = SelectedOperator(_operator);
                rule.Threshold = threshold;
            }
            else if (rule.RuleKind == RuleKind.Requires)
            {
                rule.Action = TextOf(_actionBox);
                rule.RequiredState = TextOf(_requiredStateBox);
            }
            else if (rule.RuleKind == RuleKind.Forbids)
            {
                rule.ForbiddenAction = TextOf(_forbiddenActionBox);
            }
            else
            {
                rule.Action = TextOf(_triggerActionBox);
            }

            rule.SubjectType = SelectedSubjectType();
            rule.SubjectId = SelectedSubjectId();
            rule.Effect = BuildEffect();
        }

        if (_editingRule is not null)
        {
            WorldRulePresentation.CopyAdvanced(_editingRule, rule);
        }

        var error = WorldRulePresentation.Validate(rule);
        if (error is not null)
        {
            ShowRuleError(error);
            return;
        }

        try
        {
            await _store.SaveWorldRule(_novelId, rule);
            ReplaceRule(rule);
            _editingRule = null;
            RuleFormScroll.IsVisible = false;
            _ruleFilterValue = null;
            _ruleFilter.SelectedIndex = 0;
            RebuildRuleList();
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "保存世界规则失败 novelId={Id}", _novelId);
            ShowRuleError($"保存失败：{ex.Message}");
        }
    }

    private async void SaveSetting_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        var rule = new WorldRule
        {
            Id = _editingSetting?.Id ?? NewId(),
            NovelId = _novelId,
            EntryKind = WorldRuleEntryKind.Setting,
            Category = SelectedCategory(_settingCategory),
            Description = TextOf(_settingTitle),
            Content = _settingContent.Text ?? "",
        };

        var error = WorldRulePresentation.Validate(rule);
        if (error is not null)
        {
            ShowSettingError(error);
            return;
        }

        try
        {
            await _store.SaveWorldRule(_novelId, rule);
            ReplaceRule(rule);
            _editingSetting = null;
            SettingFormScroll.IsVisible = false;
            _settingFilterValue = null;
            _settingFilter.SelectedIndex = 0;
            RebuildSettingList();
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "保存世界设定失败 novelId={Id}", _novelId);
            ShowSettingError($"保存失败：{ex.Message}");
        }
    }

    private void ConfirmDelete(WorldRule rule)
    {
        var label = string.IsNullOrWhiteSpace(rule.Description)
            ? rule.EntryKind == WorldRuleEntryKind.Setting
                ? "未命名设定"
                : "未命名规则"
            : rule.Description;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            _ = DeleteAsync(rule);
            return;
        }
        _ = new ConfirmDeleteWindow("世界规则", label, () => DeleteAsync(rule)).ShowDialog(owner);
    }

    /// <summary>「历史」→ 实体时间线（世界规则的本体动作 + 字段审计，只读 Markdown）。</summary>
    private async void ShowHistoryAsync(WorldRule rule)
    {
        var name =
            !string.IsNullOrWhiteSpace(rule.Description) ? rule.Description
            : !string.IsNullOrWhiteSpace(rule.Type) ? rule.Type
            : rule.Id;
        try
        {
            var entries = await Core.Evolution.EntityLogView.BuildForEntityAsync(
                _store,
                _novelId,
                EntityType.WorldRule,
                rule.Id
            );
            var markdown = Core.Evolution.EntityLogFormatter.ToMarkdown(name, entries);
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new MarkdownDialog("时间线 · " + name, markdown).ShowDialog(owner);
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "加载世界规则时间线失败 id={Id}", rule.Id);
            StatusText.Text = $"加载时间线失败：{ex.Message}";
            StatusText.IsVisible = true;
        }
    }

    private async Task DeleteAsync(WorldRule rule)
    {
        try
        {
            await _store.DeleteWorldRule(_novelId, rule.Id);
            _rules.RemoveAll(r => r.Id == rule.Id);
            if (_editingRule?.Id == rule.Id)
            {
                _editingRule = null;
                RuleFormScroll.IsVisible = false;
            }
            if (_editingSetting?.Id == rule.Id)
            {
                _editingSetting = null;
                SettingFormScroll.IsVisible = false;
            }
            RebuildRuleList();
            RebuildSettingList();
        }
        catch (Exception ex)
        {
            Log?.LogWarning(ex, "删除世界规则失败 novelId={Id}", _novelId);
            StatusText.Text = $"删除失败：{ex.Message}";
            StatusText.IsVisible = true;
        }
    }

    private void ReplaceRule(WorldRule rule)
    {
        var index = _rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0)
            _rules[index] = rule;
        else
            _rules.Add(rule);
    }

    private void PopulateRuleForm(WorldRule rule)
    {
        ClearRuleError();
        _ruleCategory.SelectedIndex = IndexOfCategory(rule.Category);
        _ruleDescription.Text = rule.Description;
        _ruleKind.SelectedIndex = IndexOfRuleKind(rule.RuleKind);

        var presetIndex = 0;
        if (WorldRulePresentation.IsCloudPreset(rule))
        {
            var normalized = WorldRuleTypes.Normalize(rule.Type);
            for (var i = 0; i < WorldRuleTypes.All.Length; i++)
            {
                if (string.Equals(WorldRuleTypes.All[i].Key, normalized, StringComparison.Ordinal))
                {
                    presetIndex = i + 1;
                    break;
                }
            }
        }
        _preset.SelectedIndex = presetIndex;
        _valueBox.Text = rule.Value > 0 ? FormatNumber(rule.Value) : "";
        _unitBox.Text = rule.Unit;
        if (presetIndex > 0 && string.IsNullOrWhiteSpace(_unitBox.Text))
        {
            _unitBox.Text = WorldRuleTypes.All[presetIndex - 1].UnitHint;
        }
        _metricBox.Text = rule.Metric ?? "";
        _operator.SelectedIndex = IndexOfOperator(rule.Operator);
        _thresholdBox.Text = rule.Threshold is { } threshold ? FormatNumber(threshold) : "";
        _actionBox.Text = rule.Action ?? "";
        _requiredStateBox.Text = rule.RequiredState ?? "";
        _forbiddenActionBox.Text = rule.ForbiddenAction ?? rule.Action ?? "";
        _triggerActionBox.Text = rule.Action ?? "";

        _subjectType.SelectedIndex = rule.SubjectType is { } subjectType
            ? IndexOfSubjectType(subjectType) + 1
            : 0;
        PopulateSubjectIds();
        _subjectId.SelectedIndex = rule.SubjectId is { Length: > 0 } subjectId
            ? Math.Max(0, Array.IndexOf(_subjectIdValues, subjectId))
            : 0;

        var effect = rule.Effect;
        _effectKind.SelectedIndex = IndexOfDeltaKind(effect?.Kind ?? PlannedDeltaKind.StateChange);
        _effectTargetType.SelectedIndex = IndexOfTargetType(
            effect?.TargetEntityType ?? EntityType.Character
        );
        PopulateTargetIds();
        _effectTargetId.SelectedIndex = effect?.TargetId is { Length: > 0 } targetId
            ? Math.Max(0, Array.IndexOf(_effectTargetIdValues, targetId))
            : 0;
        _effectAspect.SelectedIndex = IndexOfAspect(effect?.Aspect);
        _effectFrom.Text = effect?.FromSummary ?? "";
        _effectTo.Text = effect?.ToSummary ?? "";
        _effectSummary.Text = effect?.Summary ?? "";
        UpdateRuleFieldVisibility();
    }

    private void PopulateSettingForm(WorldRule rule)
    {
        ClearSettingError();
        _settingCategory.SelectedIndex = IndexOfCategory(rule.Category);
        _settingTitle.Text = rule.Description;
        _settingContent.Text = rule.Content;
    }

    private void ResetRuleForm()
    {
        _editingRule = null;
        ClearRuleError();
        _ruleCategory.SelectedIndex = 0;
        _ruleDescription.Text = "";
        _ruleKind.SelectedIndex = 0;
        _preset.SelectedIndex = 0;
        _valueBox.Text = "";
        _unitBox.Text = "";
        _unitBox.IsEnabled = true;
        _metricBox.Text = "";
        _operator.SelectedIndex = 0;
        _thresholdBox.Text = "";
        _actionBox.Text = "";
        _requiredStateBox.Text = "";
        _forbiddenActionBox.Text = "";
        _triggerActionBox.Text = "";
        _subjectType.SelectedIndex = 0;
        PopulateSubjectIds();
        _effectKind.SelectedIndex = 0;
        _effectTargetType.SelectedIndex = 0;
        PopulateTargetIds();
        _effectAspect.SelectedIndex = 0;
        _effectFrom.Text = "";
        _effectTo.Text = "";
        _effectSummary.Text = "";
        UpdateRuleFieldVisibility();
    }

    private void ResetSettingForm()
    {
        _editingSetting = null;
        ClearSettingError();
        _settingCategory.SelectedIndex = 0;
        _settingTitle.Text = "";
        _settingContent.Text = "";
    }

    private void UpdateRuleFieldVisibility()
    {
        var preset = _preset.SelectedIndex > 0;
        _cloudRow.IsVisible = preset;
        _unitBox.IsEnabled = !preset;
        _ruleKind.IsEnabled = !preset;
        _numericRow.IsVisible = !preset && _ruleKind.SelectedIndex == 0;
        _requiresRow.IsVisible = !preset && _ruleKind.SelectedIndex == 1;
        _forbidsRow.IsVisible = !preset && _ruleKind.SelectedIndex == 2;
        _triggersRow.IsVisible = !preset && _ruleKind.SelectedIndex == 3;
        _subjectIdRow.IsVisible = _subjectType.SelectedIndex > 0;
        _effectSection.IsVisible = !preset;
    }

    private void PopulateSubjectIds()
    {
        var names = new List<string> { "全部" };
        var ids = new List<string?> { null };
        if (_subjectType.SelectedIndex > 0)
        {
            var type = WorldRulePresentation.SubjectTypes[_subjectType.SelectedIndex - 1].Value;
            if (_entityOptions.TryGetValue(type, out var options))
            {
                for (var i = 0; i < options.Names.Length && i < options.Ids.Length; i++)
                {
                    names.Add(options.Names[i]);
                    ids.Add(options.Ids[i]);
                }
            }
        }
        _subjectIdValues = [.. ids];
        _subjectId.ItemsSource = names;
        _subjectId.SelectedIndex = 0;
    }

    private void PopulateTargetIds()
    {
        var names = new List<string> { "留空=主体" };
        var ids = new List<string?> { null };
        var type = SelectedTargetType(_effectTargetType);
        if (_entityOptions.TryGetValue(type, out var options))
        {
            for (var i = 0; i < options.Names.Length && i < options.Ids.Length; i++)
            {
                names.Add(options.Names[i]);
                ids.Add(options.Ids[i]);
            }
        }
        _effectTargetIdValues = [.. ids];
        _effectTargetId.ItemsSource = names;
        _effectTargetId.SelectedIndex = 0;
    }

    private PlannedDelta? BuildEffect()
    {
        var targetId = SelectedEffectTargetId();
        var from = TextOf(_effectFrom);
        var to = TextOf(_effectTo);
        var summary = TextOf(_effectSummary);
        var aspect = SelectedAspect(_effectAspect);
        if (
            string.IsNullOrWhiteSpace(summary)
            && string.IsNullOrWhiteSpace(from)
            && string.IsNullOrWhiteSpace(to)
            && string.IsNullOrWhiteSpace(targetId)
            && aspect is null
        )
        {
            return null;
        }

        return new PlannedDelta
        {
            Kind = SelectedDeltaKind(_effectKind),
            TargetEntityType = SelectedTargetType(_effectTargetType),
            TargetId = targetId ?? "",
            Aspect = aspect,
            FromSummary = from,
            ToSummary = to,
            Summary = summary,
        };
    }

    private EntityType? SelectedSubjectType()
    {
        var index = _subjectType.SelectedIndex;
        return index > 0 && index - 1 < WorldRulePresentation.SubjectTypes.Length
            ? WorldRulePresentation.SubjectTypes[index - 1].Value
            : null;
    }

    private string? SelectedSubjectId()
    {
        var index = _subjectId.SelectedIndex;
        return index > 0 && index < _subjectIdValues.Length ? _subjectIdValues[index] : null;
    }

    private string? SelectedEffectTargetId()
    {
        var index = _effectTargetId.SelectedIndex;
        return index > 0 && index < _effectTargetIdValues.Length
            ? _effectTargetIdValues[index]
            : null;
    }

    private void ShowRuleError(string message)
    {
        _ruleError.Text = message;
        _ruleError.IsVisible = true;
    }

    private void ClearRuleError()
    {
        _ruleError.Text = "";
        _ruleError.IsVisible = false;
    }

    private void ShowSettingError(string message)
    {
        _settingError.Text = message;
        _settingError.IsVisible = true;
    }

    private void ClearSettingError()
    {
        _settingError.Text = "";
        _settingError.IsVisible = false;
    }

    private static string[] CategoryLabels() =>
        WorldRulePresentation.Categories.Select(c => c.Label).ToArray();

    private static string[] CategoryOptions() =>
        [WorldRulePresentation.AllCategoriesLabel, .. CategoryLabels()];

    private static WorldRuleCategory SelectedCategory(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.Categories.Length
            ? WorldRulePresentation.Categories[index].Value
            : WorldRuleCategory.Other;
    }

    private static RuleKind SelectedRuleKind(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.RuleKinds.Length
            ? WorldRulePresentation.RuleKinds[index].Value
            : RuleKind.Numeric;
    }

    private static string SelectedOperator(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.Operators.Length
            ? WorldRulePresentation.Operators[index].Value
            : ">";
    }

    private static PlannedDeltaKind SelectedDeltaKind(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.DeltaKinds.Length
            ? WorldRulePresentation.DeltaKinds[index].Value
            : PlannedDeltaKind.StateChange;
    }

    private static EntityType SelectedTargetType(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.TargetTypes.Length
            ? WorldRulePresentation.TargetTypes[index].Value
            : EntityType.Character;
    }

    private static EvolutionAspect? SelectedAspect(ComboBox select)
    {
        var index = select.SelectedIndex;
        return index > 0 && index < WorldRulePresentation.Aspects.Length
            ? WorldRulePresentation.Aspects[index].Value
            : null;
    }

    private static int IndexOfCategory(WorldRuleCategory value)
    {
        for (var i = 0; i < WorldRulePresentation.Categories.Length; i++)
        {
            if (WorldRulePresentation.Categories[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int IndexOfRuleKind(RuleKind value)
    {
        for (var i = 0; i < WorldRulePresentation.RuleKinds.Length; i++)
        {
            if (WorldRulePresentation.RuleKinds[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int IndexOfOperator(string? value)
    {
        for (var i = 0; i < WorldRulePresentation.Operators.Length; i++)
        {
            if (
                string.Equals(
                    WorldRulePresentation.Operators[i].Value,
                    value,
                    StringComparison.Ordinal
                )
            )
            {
                return i;
            }
        }
        return 0;
    }

    private static int IndexOfDeltaKind(PlannedDeltaKind value)
    {
        for (var i = 0; i < WorldRulePresentation.DeltaKinds.Length; i++)
        {
            if (WorldRulePresentation.DeltaKinds[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int IndexOfTargetType(EntityType value)
    {
        for (var i = 0; i < WorldRulePresentation.TargetTypes.Length; i++)
        {
            if (WorldRulePresentation.TargetTypes[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int IndexOfSubjectType(EntityType value)
    {
        for (var i = 0; i < WorldRulePresentation.SubjectTypes.Length; i++)
        {
            if (WorldRulePresentation.SubjectTypes[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int IndexOfAspect(EvolutionAspect? value)
    {
        for (var i = 0; i < WorldRulePresentation.Aspects.Length; i++)
        {
            if (WorldRulePresentation.Aspects[i].Value == value)
                return i;
        }
        return 0;
    }

    private static int CategoryOrder(WorldRuleCategory value)
    {
        for (var i = 0; i < WorldRulePresentation.Categories.Length; i++)
        {
            if (WorldRulePresentation.Categories[i].Value == value)
                return i;
        }
        return WorldRulePresentation.Categories.Length;
    }

    private ComboBox NewCombo(string[] items)
    {
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (
            Application.Current?.TryFindResource("AdalineComboBox", out var theme) == true
            && theme is ControlTheme controlTheme
        )
            combo.Theme = controlTheme;
        return combo;
    }

    private static TextBox NewText(string placeholder) =>
        new TextBox
        {
            Classes = { "Adaline" },
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12.5,
        };

    private TextBlock NewErrorText() =>
        new TextBlock
        {
            FontSize = 11,
            Foreground = TryBrush("BrushCrimsonSpecimen"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

    private TextBlock FormTitle(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = TryBrush("BrushForestInk"),
        };

    private TextBlock Hint(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = TryBrush("BrushSageGray"),
            TextWrapping = TextWrapping.Wrap,
        };

    private Control NewRow(string label, Control control)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(12)),
                new ColumnDefinition(GridLength.Star),
            },
            Margin = new Thickness(0, 0, 0, 4),
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = TryBrush("BrushSageGray"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 2);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private void AddRow(Panel host, string label, Control control) =>
        host.Children.Add(NewRow(label, control));

    private Control Empty(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = TryBrush("BrushSageMist"),
            TextWrapping = TextWrapping.Wrap,
        };

    private Control GroupHeader(string text) =>
        new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = TryBrush("BrushSageMist"),
            Margin = new Thickness(0, 4, 0, 2),
        };

    private Button SmallButton(string text, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Classes = { "NavPill" },
            Padding = new Thickness(8, 3),
            FontSize = 11,
        };
        if (danger)
            button.Foreground = TryBrush("BrushCrimsonSpecimen");
        return button;
    }

    private static Control Buttons(
        params (
            string Text,
            EventHandler<Avalonia.Interactivity.RoutedEventArgs> Click,
            bool Primary
        )[] actions
    )
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var (text, click, primary) in actions)
        {
            var button = new Button
            {
                Content = text,
                Classes = { primary ? "Primary" : "Ghost" },
                FontSize = 11,
            };
            button.Click += click;
            panel.Children.Add(button);
        }
        return panel;
    }

    private IBrush TryBrush(string key)
    {
        if (
            Application.Current?.TryFindResource(key, out var resource) == true
            && resource is IBrush brush
        )
            return brush;
        return Brushes.Gray;
    }

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseNullableDouble(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!TryParseDouble(text, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static string TextOf(TextBox box) => (box.Text ?? "").Trim();

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
