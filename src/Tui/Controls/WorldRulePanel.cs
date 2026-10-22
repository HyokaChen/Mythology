using System.Globalization;
using Core.Data;
using Microsoft.Extensions.Logging;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 小说详情里的「世界规则」面板：规则（四类机制）与设定（描述性世界观）的增删改查、按领域筛选。
/// 规则/设定即时写入 <c>world-rules.yaml</c>；输入校验与摘要统一走 <see cref="WorldRulePresentation"/>。
/// </summary>
public sealed class WorldRulePanel
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<EntityType, (string[] Names, string[] Ids)> _entityOptions;
    private readonly List<WorldRule> _rules;
    private readonly VStack _root;
    private readonly VStack _listHost = new();
    private readonly VStack _formHost = new();
    private readonly Button _rulesViewButton;
    private readonly Button _settingsViewButton;
    private readonly Button _newButton;
    private readonly MythSelect _filterSelect;
    private readonly MythSelect _ruleCategory;
    private readonly TextBox _ruleDescription;
    private readonly MythSelect _ruleKind;
    private readonly MythSelect _preset;
    private readonly TextBox _valueBox;
    private readonly TextBox _unitBox;
    private readonly TextBox _metricBox;
    private readonly MythSelect _operatorSelect;
    private readonly TextBox _thresholdBox;
    private readonly TextBox _actionBox;
    private readonly TextBox _requiredStateBox;
    private readonly TextBox _forbiddenActionBox;
    private readonly TextBox _triggerActionBox;
    private readonly MythSelect _subjectType;
    private readonly MythSelect _subjectId;
    private readonly MythSelect _effectKind;
    private readonly MythSelect _effectTargetType;
    private readonly MythSelect _effectTargetId;
    private readonly MythSelect _effectAspect;
    private readonly TextBox _effectFrom;
    private readonly TextBox _effectTo;
    private readonly TextBox _effectSummary;
    private readonly Markup _ruleError;
    private readonly VStack _ruleForm;
    private readonly Visual _cloudRow;
    private readonly Visual _numericRow;
    private readonly Visual _requiresRow;
    private readonly Visual _forbidsRow;
    private readonly Visual _triggersRow;
    private readonly Visual _subjectIdRow;
    private readonly Visual _effectSection;
    private readonly MythSelect _settingCategory;
    private readonly TextBox _settingTitle;
    private readonly TextArea _settingContent;
    private readonly Markup _settingError;
    private readonly VStack _settingForm;

    private WorldRuleEntryKind _entryKind = WorldRuleEntryKind.Rule;
    private WorldRule? _editing;
    private bool _formVisible;
    private WorldRuleCategory? _categoryFilter;
    private string?[] _subjectIdValues = [];
    private string?[] _effectTargetIdValues = [];

    /// <summary>根视觉元素。</summary>
    public Visual Root => _root;

    public WorldRulePanel(
        INovelStore store,
        string novelId,
        IReadOnlyList<WorldRule> rules,
        IReadOnlyDictionary<EntityType, (string[] Names, string[] Ids)> entityOptions,
        ILogger logger
    )
    {
        _store = store;
        _novelId = novelId;
        _logger = logger;
        _entityOptions = entityOptions;
        _rules = [.. rules];

        _rulesViewButton = new Button(new Markup("[bold]规则[/]"));
        _settingsViewButton = new Button(new Markup("[bold]设定[/]"));
        _rulesViewButton.Click(() => SwitchView(WorldRuleEntryKind.Rule));
        _settingsViewButton.Click(() => SwitchView(WorldRuleEntryKind.Setting));

        _filterSelect = NewSelect([
            WorldRulePresentation.AllCategoriesLabel,
            .. WorldRulePresentation.Categories.Select(c => c.Label),
        ]);
        _filterSelect.SelectionChanged(
            (_, _) =>
            {
                var index = _filterSelect.SelectedIndex;
                _categoryFilter =
                    index > 0 && index - 1 < WorldRulePresentation.Categories.Length
                        ? WorldRulePresentation.Categories[index - 1].Value
                        : null;
                RebuildList();
            }
        );

        _newButton = new Button(new Markup("[bold]＋ 新建[/]"));
        _newButton.Click(BeginNew);

        // ── 规则表单 ──
        _ruleCategory = NewSelect([.. WorldRulePresentation.Categories.Select(c => c.Label)]);
        _ruleDescription = NewTextBox("说明 / 地点距离时填「地点A>地点B」");
        _ruleKind = NewSelect([.. WorldRulePresentation.RuleKinds.Select(k => k.Label)]);
        _preset = NewSelect(["不使用（本地规则）", .. WorldRuleTypes.All.Select(t => t.Label)]);
        _valueBox = NewTextBox("数值，如 300");
        _unitBox = NewTextBox("单位，如 里/日");
        _metricBox = NewTextBox("指标，如 speed");
        _operatorSelect = NewSelect([.. WorldRulePresentation.Operators.Select(o => o.Label)]);
        _thresholdBox = NewTextBox("阈值，如 100");
        _actionBox = NewTextBox("行动，如 御剑");
        _requiredStateBox = NewTextBox("所需状态，如 金丹");
        _forbiddenActionBox = NewTextBox("被禁止的行动，如 御剑");
        _triggerActionBox = NewTextBox("触发条件，如 月圆");
        _subjectType = NewSelect([
            "不限",
            .. WorldRulePresentation.SubjectTypes.Select(t => t.Label),
        ]);
        _subjectId = NewSelect("全部");
        _effectKind = NewSelect([.. WorldRulePresentation.DeltaKinds.Select(k => k.Label)]);
        _effectTargetType = NewSelect([.. WorldRulePresentation.TargetTypes.Select(t => t.Label)]);
        _effectTargetId = NewSelect("留空=主体");
        _effectAspect = NewSelect([.. WorldRulePresentation.Aspects.Select(a => a.Label)]);
        _effectFrom = NewTextBox("变化前");
        _effectTo = NewTextBox("变化后");
        _effectSummary = NewTextBox("后果摘要，如：御剑失败");

        _ruleError = new Markup("") { IsVisible = false };
        _cloudRow = new VStack(Row("数值", _valueBox), Row("单位", _unitBox)).Spacing(0);
        _numericRow = new VStack(
            Row("指标", _metricBox),
            Row("比较符", _operatorSelect),
            Row("阈值", _thresholdBox)
        ).Spacing(0);
        _requiresRow = new VStack(
            Row("行动", _actionBox),
            Row("所需状态", _requiredStateBox)
        ).Spacing(0);
        _forbidsRow = Row("被禁止的行动", _forbiddenActionBox);
        _triggersRow = Row("触发条件", _triggerActionBox);
        _subjectIdRow = Row("主体", _subjectId);
        _effectSection = new VStack(
            Row("后果类型", _effectKind),
            Row("目标类型", _effectTargetType),
            Row("目标", _effectTargetId),
            Row("变化面", _effectAspect),
            Row("变化前", _effectFrom),
            Row("变化后", _effectTo),
            Row("摘要", _effectSummary)
        ).Spacing(0);

        _ruleForm = new VStack(
            new Markup("[bold]规则详情[/]"),
            _ruleError,
            Row("领域", _ruleCategory),
            Row("说明", _ruleDescription),
            Row("机制", _ruleKind),
            Row("云端检查", _preset),
            _cloudRow,
            new Markup("[dim]切换「云端检查 / 本地规则」会清除另一模式的字段。[/]"),
            _numericRow,
            _requiresRow,
            _forbidsRow,
            _triggersRow,
            Row("作用主体", _subjectType),
            _subjectIdRow,
            _effectSection,
            new Markup(
                "[dim]行动/状态 token 只匹配结构化字段（角色 Tags/Abilities/Status、组织 Type/Goal、地点 Type/Atmosphere、道具 Type/Description）；数值指标来自各实体的 Metrics。不从正文猜。[/]"
            ),
            new HStack(
                new Button(new Markup("[dim]取消[/]")).Click(CancelForm),
                new Button(new Markup("[bold]保存[/]"))
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => _ = SaveRuleAsync())
            ).Spacing(1)
        ).Spacing(0);
        _ruleForm.IsVisible = false;

        // ── 设定表单 ──
        _settingCategory = NewSelect([.. WorldRulePresentation.Categories.Select(c => c.Label)]);
        _settingTitle = NewTextBox("设定标题，如：宗门礼制");
        _settingContent = new TextArea()
            .Placeholder("设定正文：历史、文化、地理、力量体系、制度、语言、历法、禁忌等")
            .AutoSizeMode(TextEditorAutoSizeMode.Height)
            .MinHeight(6)
            .MaxHeight(12)
            .AcceptTab(false)
            .HorizontalAlignment(Align.Stretch);
        _settingError = new Markup("") { IsVisible = false };
        _settingForm = new VStack(
            new Markup("[bold]设定详情[/]"),
            _settingError,
            Row("领域", _settingCategory),
            Row("标题", _settingTitle),
            Row("正文", _settingContent),
            new HStack(
                new Button(new Markup("[dim]取消[/]")).Click(CancelForm),
                new Button(new Markup("[bold]保存[/]"))
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => _ = SaveSettingAsync())
            ).Spacing(1)
        ).Spacing(0);
        _settingForm.IsVisible = false;

        _formHost.Children.Add(_ruleForm);
        _formHost.Children.Add(_settingForm);
        _formHost.IsVisible = false;

        _root = new VStack(
            new Markup("[bold]世界规则[/] — 可执行硬约束（数值 / 前置 / 禁止 / 触发）"),
            new Markup("[dim]设定 = 描述性世界观，不参与机器判定。规则与设定改动即时保存。[/]"),
            new HStack(_rulesViewButton, _settingsViewButton, _newButton).Spacing(1),
            Row("领域筛选", _filterSelect),
            _listHost,
            _formHost
        ).Spacing(0);

        // 事件接线
        _ruleKind.SelectionChanged((_, _) => UpdateRuleFieldVisibility());
        _preset.SelectionChanged(
            (_, _) =>
            {
                var index = _preset.SelectedIndex;
                _unitBox.Text =
                    index > 0 && index - 1 < WorldRuleTypes.All.Length
                        ? WorldRuleTypes.All[index - 1].UnitHint
                        : "";
                UpdateRuleFieldVisibility();
            }
        );
        _subjectType.SelectionChanged(
            (_, _) =>
            {
                PopulateSubjectIds();
                if (_subjectType.SelectedIndex > 0 && _effectTargetType.SelectedIndex == 0)
                {
                    _effectTargetType.SelectedIndex = _subjectType.SelectedIndex - 1;
                    PopulateTargetIds();
                }
                UpdateRuleFieldVisibility();
            }
        );
        _effectTargetType.SelectionChanged((_, _) => PopulateTargetIds());

        UpdateViewStyles();
        RebuildList();
        UpdateRuleFieldVisibility();
    }

    private void SwitchView(WorldRuleEntryKind kind)
    {
        _entryKind = kind;
        _editing = null;
        _formVisible = false;
        UpdateViewStyles();
        UpdateFormVisibility();
        RebuildList();
    }

    private void UpdateViewStyles()
    {
        _rulesViewButton.Style(
            ThemeStyles.BuildSectionButton(
                PanelThemes.Current,
                _entryKind == WorldRuleEntryKind.Rule
            )
        );
        _settingsViewButton.Style(
            ThemeStyles.BuildSectionButton(
                PanelThemes.Current,
                _entryKind == WorldRuleEntryKind.Setting
            )
        );
    }

    private void BeginNew()
    {
        _editing = null;
        if (_entryKind == WorldRuleEntryKind.Rule)
        {
            ResetRuleForm();
        }
        else
        {
            ResetSettingForm();
        }
        _formVisible = true;
        UpdateFormVisibility();
    }

    private void BeginEdit(WorldRule rule)
    {
        _entryKind = rule.EntryKind;
        _editing = rule;
        UpdateViewStyles();
        if (rule.EntryKind == WorldRuleEntryKind.Rule)
        {
            PopulateRuleForm(rule);
        }
        else
        {
            PopulateSettingForm(rule);
        }
        _formVisible = true;
        UpdateFormVisibility();
        RebuildList();
    }

    private void CancelForm()
    {
        _editing = null;
        _formVisible = false;
        UpdateFormVisibility();
    }

    private void UpdateFormVisibility()
    {
        _formHost.IsVisible = _formVisible;
        _ruleForm.IsVisible = _formVisible && _entryKind == WorldRuleEntryKind.Rule;
        _settingForm.IsVisible = _formVisible && _entryKind == WorldRuleEntryKind.Setting;
        UpdateRuleFieldVisibility();
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

    private void ResetRuleForm()
    {
        ClearRuleError();
        _ruleCategory.SelectedIndex = 0;
        _ruleDescription.Text = "";
        _ruleKind.SelectedIndex = 0;
        _preset.SelectedIndex = 0;
        _valueBox.Text = "";
        _unitBox.Text = "";
        _unitBox.IsEnabled = true;
        _metricBox.Text = "";
        _operatorSelect.SelectedIndex = 0;
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
        ClearSettingError();
        _settingCategory.SelectedIndex = 0;
        _settingTitle.Text = "";
        _settingContent.Text = "";
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
        _operatorSelect.SelectedIndex = IndexOfOperator(rule.Operator);
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

    private void RebuildList()
    {
        _listHost.Children.Clear();
        var visible = _rules
            .Where(rule =>
                rule.EntryKind == _entryKind
                && (_categoryFilter is null || rule.Category == _categoryFilter)
            )
            .ToList();
        if (visible.Count == 0)
        {
            _listHost.Children.Add(
                new Markup(
                    _entryKind == WorldRuleEntryKind.Rule
                        ? "[dim]（暂无规则）声明后才会参与素材提示与云端检查。[/]"
                        : "[dim]（暂无设定）记录历史、文化、地理、力量体系等描述性内容。[/]"
                )
            );
            return;
        }

        foreach (
            var group in visible
                .GroupBy(rule => rule.Category)
                .OrderBy(group => CategoryOrder(group.Key))
        )
        {
            _listHost.Children.Add(
                new Markup($"[dim]{WorldRulePresentation.CategoryLabel(group.Key)}[/]")
            );
            foreach (var rule in group.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id))
            {
                _listHost.Children.Add(BuildRuleCard(rule));
            }
        }
    }

    private Visual BuildRuleCard(WorldRule rule)
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
                ? ToolCallRenderer.Truncate((rule.Content ?? "").ReplaceLineEndings(" "), 80)
                : WorldRulePresentation.SummaryFor(rule);

        var editButton = new Button(new Markup("[dim]编辑[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        editButton.Click(() => BeginEdit(rule));
        // 历史：规则的状态变化/本体动作（发布时联动改了 Severity 等都能在这里追溯）
        var historyButton = new Button(new Markup("[dim]历史[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        historyButton.Click(() =>
            EntityLogDialog.Show(_store, _novelId, EntityType.WorldRule, rule.Id)
        );
        var deleteButton = new Button(new Markup("[error]删除[/]")).Style(
            ThemeStyles.BuildButtonDanger(PanelThemes.Current)
        );
        deleteButton.Click(() => ConfirmDelete(rule));

        return new VStack(
            new Markup(
                $"[bold]{ToolCallRenderer.Escape(title)}[/] [dim]· {ToolCallRenderer.Escape(badge)}[/]"
            ),
            new Markup($"[dim]{ToolCallRenderer.Escape(ToolCallRenderer.Truncate(detail, 80))}[/]"),
            new HStack(editButton, historyButton, deleteButton).Spacing(1)
        ).Spacing(0);
    }

    private void ConfirmDelete(WorldRule rule)
    {
        var label = string.IsNullOrWhiteSpace(rule.Description)
            ? rule.EntryKind == WorldRuleEntryKind.Setting
                ? "未命名设定"
                : "未命名规则"
            : rule.Description;
        ConfirmDeleteDialog.Show(EntityType.WorldRule, label, () => _ = DeleteAsync(rule));
    }

    private async Task DeleteAsync(WorldRule rule)
    {
        try
        {
            await _store.DeleteWorldRule(_novelId, rule.Id);
            _rules.RemoveAll(r => r.Id == rule.Id);
            if (_editing?.Id == rule.Id)
            {
                _editing = null;
                _formVisible = false;
                UpdateFormVisibility();
            }
            RebuildList();
            ToastService.Info(
                rule.EntryKind == WorldRuleEntryKind.Setting ? "设定已删除" : "规则已删除"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除世界规则失败 novelId={NovelId}", _novelId);
            ToastService.Error($"删除失败: {ex.Message}");
        }
    }

    private async Task SaveRuleAsync()
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        var presetIndex = _preset.SelectedIndex;
        var category = SelectedCategory(_ruleCategory);
        var description = Trim(_ruleDescription.Text);
        var rule = new WorldRule
        {
            Id = _editing?.Id ?? NewId(),
            NovelId = _novelId,
            EntryKind = WorldRuleEntryKind.Rule,
            Category = category,
            Description = description,
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
                rule.Metric = Trim(_metricBox.Text);
                rule.Operator = SelectedOperator(_operatorSelect);
                rule.Threshold = threshold;
            }
            else if (rule.RuleKind == RuleKind.Requires)
            {
                rule.Action = Trim(_actionBox.Text);
                rule.RequiredState = Trim(_requiredStateBox.Text);
            }
            else if (rule.RuleKind == RuleKind.Forbids)
            {
                rule.ForbiddenAction = Trim(_forbiddenActionBox.Text);
            }
            else
            {
                rule.Action = Trim(_triggerActionBox.Text);
            }

            rule.SubjectType = SelectedSubjectType();
            rule.SubjectId = SelectedSubjectId();
            rule.Effect = BuildEffect();
        }

        if (_editing is not null)
        {
            WorldRulePresentation.CopyAdvanced(_editing, rule);
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
            _editing = null;
            _formVisible = false;
            _categoryFilter = null;
            _filterSelect.SelectedIndex = 0;
            UpdateFormVisibility();
            RebuildList();
            ToastService.Success("规则已保存");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存世界规则失败 novelId={NovelId}", _novelId);
            ShowRuleError($"保存失败：{ex.Message}");
        }
    }

    private async Task SaveSettingAsync()
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        var rule = new WorldRule
        {
            Id = _editing?.Id ?? NewId(),
            NovelId = _novelId,
            EntryKind = WorldRuleEntryKind.Setting,
            Category = SelectedCategory(_settingCategory),
            Description = Trim(_settingTitle.Text),
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
            _editing = null;
            _formVisible = false;
            _categoryFilter = null;
            _filterSelect.SelectedIndex = 0;
            UpdateFormVisibility();
            RebuildList();
            ToastService.Success("设定已保存");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存世界设定失败 novelId={NovelId}", _novelId);
            ShowSettingError($"保存失败：{ex.Message}");
        }
    }

    private void ReplaceRule(WorldRule rule)
    {
        var index = _rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0)
        {
            _rules[index] = rule;
        }
        else
        {
            _rules.Add(rule);
        }
    }

    private PlannedDelta? BuildEffect()
    {
        var targetId = SelectedEffectTargetId();
        var from = Trim(_effectFrom.Text);
        var to = Trim(_effectTo.Text);
        var summary = Trim(_effectSummary.Text);
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
        _subjectId.Items([.. names]);
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
        _effectTargetId.Items([.. names]);
        _effectTargetId.SelectedIndex = 0;
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
        _ruleError.Text = $"[error]{ToolCallRenderer.Escape(message)}[/]";
        _ruleError.IsVisible = true;
    }

    private void ClearRuleError()
    {
        _ruleError.Text = "";
        _ruleError.IsVisible = false;
    }

    private void ShowSettingError(string message)
    {
        _settingError.Text = $"[error]{ToolCallRenderer.Escape(message)}[/]";
        _settingError.IsVisible = true;
    }

    private void ClearSettingError()
    {
        _settingError.Text = "";
        _settingError.IsVisible = false;
    }

    private static MythSelect NewSelect(params string[] labels)
    {
        var select = new MythSelect();
        select.Items(labels);
        select.SelectedIndex = 0;
        select.HorizontalAlignment = Align.Stretch;
        select.Style(SelectStyle.NoBorder);
        return select;
    }

    private static TextBox NewTextBox(string placeholder) =>
        new TextBox().Placeholder(placeholder).HorizontalAlignment(Align.Stretch);

    private static Grid Row(string label, Visual control) =>
        new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .ColumnGap(1)
            .Cell(new Markup($"[dim]{label}[/]"), 0, 0)
            .Cell(control, 0, 1);

    private static WorldRuleCategory SelectedCategory(MythSelect select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.Categories.Length
            ? WorldRulePresentation.Categories[index].Value
            : WorldRuleCategory.Other;
    }

    private static RuleKind SelectedRuleKind(MythSelect select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.RuleKinds.Length
            ? WorldRulePresentation.RuleKinds[index].Value
            : RuleKind.Numeric;
    }

    private static string SelectedOperator(MythSelect select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.Operators.Length
            ? WorldRulePresentation.Operators[index].Value
            : ">";
    }

    private static PlannedDeltaKind SelectedDeltaKind(MythSelect select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.DeltaKinds.Length
            ? WorldRulePresentation.DeltaKinds[index].Value
            : PlannedDeltaKind.StateChange;
    }

    private static EntityType SelectedTargetType(MythSelect select)
    {
        var index = select.SelectedIndex;
        return index >= 0 && index < WorldRulePresentation.TargetTypes.Length
            ? WorldRulePresentation.TargetTypes[index].Value
            : EntityType.Character;
    }

    private static EvolutionAspect? SelectedAspect(MythSelect select)
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
                return i;
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

    private static string Trim(string? value) => value?.Trim() ?? "";

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
