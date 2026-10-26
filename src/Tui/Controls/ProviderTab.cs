using Core.Data;
using Core.Shared;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 连接器设置标签页：管理 provider 选择、模型、思考级别、密钥卡片。
/// </summary>
internal sealed class ProviderTab
{
    private readonly SettingsService _cfg;
    private readonly ModelDiscoveryService _discovery = new();

    // ── 顶部状态 ──
    private readonly State<int> _providerSelectState = new(0); // 连接器选择状态
    private readonly State<string?> _defaultModelState; // 默认模型状态
    private readonly State<int> _thinkingSelectState = new(2); // 思考级别选择状态
    private readonly State<int> _contextWindowSelectState = new(4); // 上下文窗口选择状态（默认 index 4 = 128K）
    private string[] _providerDisplayItems = []; // 连接器显示项
    private Select<string>? _providerDropdown; // 连接器下拉选择框

    // ── 模型下拉 ──
    private Select<string>? _modelSelect; // 模型下拉选择框
    private TextBox? _modelTextBox; // 模型手动输入框
    private string[] _availableModels = []; // 可用模型列表
    private bool _modelSelectVisible = true; // 模型下拉是否可见
    private int _modelLoadVersion; // 模型发现请求版本（丢弃过期结果）

    // ── 密钥卡片 ──
    private readonly VStack _apiKeyCards = new() { Spacing = 1 }; // 密钥卡片容器
    private readonly List<ApiKeyCardState> _apiKeyEntries = []; // 密钥卡片状态列表

    /// <summary>
    /// 初始化 ProviderTab 实例。
    /// </summary>
    /// <param name="cfg">设置服务。</param>
    public ProviderTab(SettingsService cfg)
    {
        _cfg = cfg;
        _defaultModelState = new State<string?>(cfg.Provider.DefaultModel);

        // 思考级别初始值
        var key = cfg.Provider.ThinkingLevel;
        for (var i = 0; i < ThinkingLevels.Length; i++)
        {
            if (ThinkingLevels[i].Key == key)
            {
                _thinkingSelectState.Value = i;
                break;
            }
        }

        // 上下文窗口初始值
        var ctxWindow = cfg.Provider.ContextWindowSize;
        for (var i = 0; i < ContextWindowSizes.Length; i++)
        {
            if (ContextWindowSizes[i].Tokens == ctxWindow)
            {
                _contextWindowSelectState.Value = i;
                break;
            }
        }
    }

    // ── 思考级别定义（统一来源：Core.ThinkingLevelBudgets） ──
    private static readonly (string Key, string Label)[] ThinkingLevels =
        ThinkingLevelBudgets.Options;

    // ── 预设 provider DisplayName 列表（卡片内下拉用） ──

    // ── 上下文窗口大小选项 ──
    private static readonly (string Label, int Tokens)[] ContextWindowSizes =
    [
        ("8K", 8_192),
        ("16K", 16_384),
        ("32K", 32_768),
        ("64K", 65_536),
        ("128K", 131_072),
        ("200K", 200_000),
        ("256K", 262_144),
        ("512K", 524_288),
        ("1M", 1_048_576),
    ];
    private static readonly string[] PresetDisplayNames = ProviderDefinition
        .All.Select(d => d.DisplayName)
        .ToArray();

    // ── 公开属性（Save 用） ──

    /// <summary>选中的配置键。</summary>
    public string SelectedConfigKey => ResolveSelectedProvider().ConfigKey;

    /// <summary>选中的上下文窗口大小（tokens）。</summary>
    public int SelectedContextWindowSize =>
        ContextWindowSizes[_contextWindowSelectState.Value].Tokens;

    /// <summary>选中的自定义名称（来自复合键）。</summary>
    public string? SelectedCustomName => ResolveSelectedProvider().CustomName;

    /// <summary>选中的模型。</summary>
    public string SelectedModel =>
        _modelSelectVisible && _modelSelect is not null && _availableModels.Length > 0
            ? _availableModels[
                Math.Clamp(_modelSelect.SelectedIndex, 0, _availableModels.Length - 1)
            ]
            : _defaultModelState.Value ?? "gpt-4o-mini";

    /// <summary>选中的思考级别。</summary>
    public string SelectedThinkingLevel => ThinkingLevels[_thinkingSelectState.Value].Key;

    /// <summary>
    /// 构建 API 密钥条目列表。
    /// </summary>
    /// <returns>API 密钥条目列表。</returns>
    public List<ApiKeyEntry> BuildApiKeyEntries() =>
        _apiKeyEntries
            .Select(e => new ApiKeyEntry
            {
                Provider = e.IsCustom.Value
                    ? "custom"
                    : ProviderDefinition.All[e.ProviderIdx.Value].ConfigKey,
                Name = e.IsCustom.Value ? e.Name.Value : null,
                ApiKey = e.ApiKey.Value,
                BaseUrl = e.IsCustom.Value ? e.BaseUrl.Value : null,
            })
            .ToList();

    // ── 构建 UI ──

    /// <summary>
    /// 构建连接器设置标签页的 UI。
    /// </summary>
    /// <returns>视觉元素。</returns>
    public Visual Build()
    {
        // 连接器下拉
        _providerDisplayItems = BuildProviderDisplayItems(_cfg.Provider.ApiKeys);
        _providerSelectState.Value = FindProviderDisplayIndex(
            _providerDisplayItems,
            _cfg.Provider.Default
        );

        _providerDropdown = new Select<string>()
            .Items(_providerDisplayItems)
            .SelectedIndex(_providerSelectState)
            .Style(SelectStyle.NoBorder);
        _providerDropdown.HorizontalAlignment = Align.Stretch;

        // 切换 provider 时触发模型发现
        _providerDropdown.SelectionChanged(OnProviderChanged);

        // 模型下拉
        _modelSelect = new Select<string>()
            .SelectedIndex(new State<int>(0))
            .Style(SelectStyle.NoBorder);
        _modelSelect.HorizontalAlignment = Align.Stretch;
        _modelSelect.SelectionChanged((_, _) => SyncModelFromSelection());

        // 模型手动输入（回退）
        _modelTextBox = new TextBox()
            .Text(_defaultModelState)
            .Placeholder("gpt-4o-mini")
            .HorizontalAlignment(Align.Stretch);
        _modelTextBox.IsVisible = false;

        // 思考级别下拉
        var thinkingSelect = new Select<string>()
            .Items(ThinkingLevels.Select(t => t.Label).ToArray())
            .SelectedIndex(_thinkingSelectState)
            .Style(SelectStyle.NoBorder);
        thinkingSelect.HorizontalAlignment = Align.Stretch;

        // 模型区域：下拉优先，无模型时回退到手动输入
        var modelArea = new VStack(_modelSelect, _modelTextBox).Spacing(0);

        // 上下文窗口下拉
        var contextWindowSelect = new Select<string>()
            .Items(ContextWindowSizes.Select(w => w.Label).ToArray())
            .SelectedIndex(_contextWindowSelectState)
            .Style(SelectStyle.NoBorder);
        contextWindowSelect.HorizontalAlignment = Align.Stretch;

        // 顶部表单
        var topGrid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(12) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(new Markup("[dim]连接器[/]"), 0, 0)
            .Cell(_providerDropdown, 0, 1)
            .Cell(new Markup("[dim]模型[/]"), 1, 0)
            .Cell(modelArea, 1, 1)
            .Cell(new Markup("[dim]思考[/]"), 2, 0)
            .Cell(thinkingSelect, 2, 1)
            .Cell(new Markup("[dim]上下文[/]"), 3, 0)
            .Cell(contextWindowSelect, 3, 1)
            .HorizontalAlignment(Align.Stretch);

        // 加载已有密钥卡片
        foreach (var entry in _cfg.Provider.ApiKeys)
        {
            if (ProviderDefinition.IsCustom(entry.Provider))
            {
                AddApiKeyCard(-1, entry.ApiKey, entry.BaseUrl, entry.Name);
            }
            else
            {
                var idx = Array.FindIndex(
                    PresetDisplayNames,
                    p => ProviderDefinition.FindByDisplayName(p).ConfigKey == entry.Provider
                );
                AddApiKeyCard(idx < 0 ? 0 : idx, entry.ApiKey, null, null);
            }
        }
        // 所有卡片加载完毕后刷新连接器下拉（确保自定义条目完整）
        RefreshProviderDropdown();

        // 初始加载模型列表
        _ = LoadModelsAsync();

        return PanelThemes.CreateScrollableTabContent(
            new VStack(
                topGrid,
                new Markup("[bold]密钥管理[/]"),
                _apiKeyCards,
                new Button("➕ 添加密钥")
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() =>
                    {
                        AddApiKeyCard(0, "", null, null);
                        RefreshProviderDropdown();
                    })
            ).Spacing(1)
        );
    }

    // ── 密钥卡片 ──

    /// <summary>
    /// 添加密钥卡片（预设 + 自定义统一入口）。
    /// 下拉菜单末尾包含"自定义"选项，选中后展开名称/地址字段。
    /// </summary>
    /// <param name="providerIdx">预设提供商索引（-1 表示自定义）。</param>
    /// <param name="apiKey">API 密钥。</param>
    /// <param name="baseUrl">自定义基础 URL。</param>
    /// <param name="name">自定义名称。</param>
    private void AddApiKeyCard(int providerIdx, string? apiKey, string? baseUrl, string? name)
    {
        var isCustom = providerIdx < 0;
        var items = new List<string>(PresetDisplayNames) { "自定义" };
        var initialIdx = isCustom ? items.Count - 1 : Math.Clamp(providerIdx, 0, items.Count - 2);

        var state = new ApiKeyCardState
        {
            ProviderIdx = new State<int>(initialIdx),
            ApiKey = new State<string?>(apiKey ?? ""),
            BaseUrl = new State<string?>(baseUrl ?? ""),
            Name = new State<string?>(name ?? ""),
            IsCustom = new State<bool>(isCustom),
        };

        var cardSelect = new Select<string>()
            .Items(items)
            .SelectedIndex(state.ProviderIdx)
            .Style(SelectStyle.NoBorder);
        cardSelect.HorizontalAlignment = Align.Stretch;

        var nameInput = new TextBox()
            .Text(state.Name)
            .Placeholder("我的智谱")
            .HorizontalAlignment(Align.Stretch);
        nameInput.IsVisible = isCustom;

        var keyInput = new TextBox()
            .Text(state.ApiKey)
            .Placeholder("sk-…")
            .IsPassword(true)
            .PasswordRevealMode(PasswordRevealMode.WhileFocused)
            .HorizontalAlignment(Align.Stretch);

        var urlInput = new TextBox()
            .Text(state.BaseUrl)
            .Placeholder("https://open.bigmodel.cn/api/paas/v4/")
            .HorizontalAlignment(Align.Stretch);
        urlInput.IsVisible = isCustom;

        var nameLabel = new Markup("[dim]名称[/]") { IsVisible = isCustom };

        var urlLabel = new Markup("[dim]地址[/]") { IsVisible = isCustom };

        var deleteBtn = new Button("❌").Style(ThemeStyles.BuildCloseButton(PanelThemes.Current));

        var grid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(8) },
                new ColumnDefinition { Width = GridLength.Star() },
                new ColumnDefinition { Width = GridLength.Auto }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(cardSelect, 0, 0, 1, 2)
            .Cell(deleteBtn, 0, 2)
            .Cell(nameLabel, 1, 0)
            .Cell(nameInput, 1, 1)
            .Cell(new Markup("[dim]密钥[/]"), 2, 0)
            .Cell(keyInput, 2, 1)
            .Cell(urlLabel, 3, 0)
            .Cell(urlInput, 3, 1)
            .HorizontalAlignment(Align.Stretch);

        // 切换预设/自定义时更新可见性、状态和顶部连接器下拉
        cardSelect.SelectionChanged(
            (_, _) =>
            {
                var selectedCustom = state.ProviderIdx.Value == items.Count - 1;
                state.IsCustom.Value = selectedCustom;
                nameInput.IsVisible = selectedCustom;
                nameLabel.IsVisible = selectedCustom;
                urlInput.IsVisible = selectedCustom;
                urlLabel.IsVisible = selectedCustom;
                RefreshProviderDropdown();
            }
        );
        // 自定义名称输入时刷新顶部连接器下拉
        nameInput.TextInput((_, _) => RefreshProviderDropdown());

        AddCard(state, grid, deleteBtn);
    }

    /// <summary>
    /// 添加密钥卡片到 UI。
    /// </summary>
    /// <param name="state">密钥卡片状态。</param>
    /// <param name="grid">网格布局。</param>
    /// <param name="deleteBtn">删除按钮。</param>
    private void AddCard(ApiKeyCardState state, Grid grid, Button deleteBtn)
    {
        var border = new Border(grid) { Padding = new Thickness(1, 0, 1, 0) };
        state.CardBorder = border;
        _apiKeyEntries.Add(state);
        _apiKeyCards.Children.Add(border);

        deleteBtn.Click(() =>
        {
            _apiKeyCards.Children.Remove(border);
            _apiKeyEntries.Remove(state);
            RefreshProviderDropdown();
        });
    }

    // ── 模型发现 ──

    /// <summary>
    /// 连接器变更事件处理。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnProviderChanged(object? sender, SelectSelectionChangedEventArgs e)
    {
        _ = LoadModelsAsync();
    }

    /// <summary>
    /// 异步加载可用模型列表。
    /// </summary>
    private async Task LoadModelsAsync()
    {
        var loadVersion = ++_modelLoadVersion;
        var (configKey, customName) = ResolveSelectedProvider();

        // 找到对应的 ApiKeyEntry
        ApiKeyEntry? entry = null;
        if (!ProviderDefinition.IsCustom(configKey))
        {
            entry = _cfg.Provider.ApiKeys.FirstOrDefault(k => k.Provider == configKey);
        }
        else if (!string.IsNullOrEmpty(customName))
        {
            entry = _cfg.Provider.ApiKeys.FirstOrDefault(k =>
                ProviderDefinition.IsCustom(k.Provider) && k.Name == customName
            );
        }

        var def = ProviderDefinition.FindByConfigKey(configKey);

        if (entry is null)
        {
            SetModelFallback();
            return;
        }

        var models = await ModelDiscoveryService.GetModelsAsync(
            entry,
            def.LlmProvider is not null ? def : null
        );

        // Provider 可能已再次切换；丢弃过期结果，避免旧请求覆盖新选择。
        if (loadVersion != _modelLoadVersion)
            return;

        if (models.Count == 0)
        {
            SetModelFallback();
            return;
        }

        ApplyDiscoveredModels(models, def.DefaultModelHint);
    }

    /// <summary>
    /// 应用一次模型发现结果：优先保留当前模型，否则选中列表首项，
    /// 并立即同步 <see cref="_defaultModelState"/>，确保回退手动输入时显示值正确。
    /// </summary>
    internal void ApplyDiscoveredModels(IReadOnlyList<string> models, string? fallbackModel = null)
    {
        if (_modelSelect is null || _modelTextBox is null || models.Count == 0)
        {
            SetModelFallback();
            return;
        }

        _availableModels = [.. models];
        _modelSelect.Items.Clear();
        _modelSelect.Items.AddRange(models);

        // 尝试选中当前保存的模型；空值用提供商默认提示，仍无匹配则选首项。
        var currentModel = _defaultModelState.Value;
        if (string.IsNullOrWhiteSpace(currentModel))
            currentModel = fallbackModel ?? models[0];

        var matchIndex = -1;
        for (var i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i], currentModel, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        _modelSelect.SelectedIndex = matchIndex >= 0 ? matchIndex : 0;
        SyncModelFromSelection();

        _modelSelectVisible = true;
        _modelSelect.IsVisible = true;
        _modelTextBox.IsVisible = false;
    }

    /// <summary>
    /// 下拉选中项 → <see cref="_defaultModelState"/>。选中事件与程序化赋值都走这里。
    /// </summary>
    private void SyncModelFromSelection()
    {
        if (_modelSelect is null || _availableModels.Length == 0)
            return;

        var index = Math.Clamp(_modelSelect.SelectedIndex, 0, _availableModels.Length - 1);
        _defaultModelState.Value = _availableModels[index];
    }

    /// <summary>
    /// 设置模型回退（手动输入模式）。
    /// </summary>
    private void SetModelFallback()
    {
        if (_modelSelect is null || _modelTextBox is null)
            return;

        _modelSelect.IsVisible = false;
        _modelTextBox.IsVisible = true;
        _modelSelectVisible = false;
        _availableModels = [];
    }

    // ── 连接器下拉 helpers ──

    /// <summary>
    /// 刷新顶部连接器下拉菜单（密钥卡片增删或类型切换时调用）。
    /// </summary>
    private void RefreshProviderDropdown()
    {
        if (_providerDropdown is null)
            return;

        // 保存当前选择
        var (currentKey, currentName) = ResolveSelectedProvider();

        // 从当前卡片状态构建新的显示项
        var liveEntries = _apiKeyEntries
            .Select(e => new ApiKeyEntry
            {
                Provider = e.IsCustom.Value
                    ? "custom"
                    : ProviderDefinition.All[e.ProviderIdx.Value].ConfigKey,
                Name = e.IsCustom.Value ? e.Name.Value : null,
                ApiKey = e.ApiKey.Value ?? "",
                BaseUrl = e.IsCustom.Value ? e.BaseUrl.Value : null,
            })
            .ToList();

        _providerDisplayItems = BuildProviderDisplayItems(liveEntries);
        _providerSelectState.Value = FindProviderDisplayIndex(_providerDisplayItems, currentKey);
        _providerDropdown.Items(_providerDisplayItems);
        _providerDropdown.SelectedIndex(_providerSelectState);
    }

    /// <summary>
    /// 构建连接器显示项列表（预设 + 自定义条目）。
    /// </summary>
    /// <param name="apiKeys">当前密钥条目列表。</param>
    /// <returns>显示项数组。</returns>
    private static string[] BuildProviderDisplayItems(List<ApiKeyEntry> apiKeys)
    {
        var items = ProviderDefinition.All.Select(d => d.DisplayName).ToList();
        items.AddRange(
            from entry in apiKeys
            where ProviderDefinition.IsCustom(entry.Provider) && !string.IsNullOrEmpty(entry.Name)
            select $"自定义: {entry.Name}"
        );

        if (items.Count == ProviderDefinition.All.Length)
            items.Add("自定义");

        return [.. items];
    }

    /// <summary>
    /// 查找连接器显示索引。
    /// </summary>
    /// <param name="items">显示项数组。</param>
    /// <param name="defaultKey">默认配置键，支持 "openai" 或 "custom:商汤"。</param>
    /// <returns>显示索引。</returns>
    private static int FindProviderDisplayIndex(string[] items, string defaultKey)
    {
        if (!ProviderDefinition.IsCustom(defaultKey))
        {
            var def = ProviderDefinition.FindByConfigKey(defaultKey);
            for (var i = 0; i < items.Length; i++)
                if (items[i] == def.DisplayName)
                    return i;
        }

        // 自定义 provider：从复合键中提取名称
        var customName = ProviderDefinition.GetCustomName(defaultKey);
        if (customName is not null)
        {
            var target = $"自定义: {customName}";
            for (var i = 0; i < items.Length; i++)
                if (items[i] == target)
                    return i;
        }

        // 旧格式 fallback：纯 "custom" 无名称
        for (var i = 0; i < items.Length; i++)
            if (items[i] == "自定义")
                return i;

        return 0;
    }

    /// <summary>
    /// 解析选中的连接器。
    /// </summary>
    /// <returns>配置键（预设如 "openai"，自定义如 "custom:商汤"）和自定义名称的元组。</returns>
    private (string ConfigKey, string? CustomName) ResolveSelectedProvider()
    {
        var selected = _providerDisplayItems[_providerSelectState.Value];

        var preset = ProviderDefinition.All.FirstOrDefault(d => d.DisplayName == selected);
        if (preset is not null)
            return (preset.ConfigKey, null);

        // 自定义：返回复合键 "custom:商汤"
        if (selected.StartsWith("自定义: "))
        {
            var name = selected["自定义: ".Length..];
            return ($"custom:{name}", name);
        }

        return ("custom", null);
    }
}

/// <summary>
/// 单张密钥卡片的 State 容器。
/// </summary>
internal sealed class ApiKeyCardState
{
    public State<int> ProviderIdx = new(0); // 预设提供商下拉索引
    public State<string?> ApiKey = new("", null); // API 密钥
    public State<string?> BaseUrl = new("", null); // 自定义基础 URL
    public State<string?> Name = new("", null); // 自定义名称
    public State<bool> IsCustom = new(false); // 是否为自定义提供商
    public Border? CardBorder; // 卡片边框引用（用于删除时定位）
}
