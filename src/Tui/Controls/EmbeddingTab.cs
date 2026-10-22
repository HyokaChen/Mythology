using Core.Data;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// Embedding 设置标签页：独立于聊天连接器的 provider/模型/API Key 配置。
/// 无有效密钥时回退到聊天连接器（FallbackToChat）。
/// </summary>
internal sealed class EmbeddingTab(SettingsService cfg, Visual? optionalFooter = null)
{
    private readonly State<int> _providerSelectState = new(0);
    private readonly State<string?> _model = new(cfg.Embedding.Model);
    private string[] _providerDisplayItems = [];
    private Select<string>? _providerDropdown;
    private Select<string>? _modelSelect;
    private TextBox? _modelTextBox;
    private string[] _availableModels = [];
    private bool _modelSelectHandlerWired;

    private readonly VStack _apiKeyCards = new() { Spacing = 1 };
    private readonly List<EmbeddingKeyCardState> _apiKeyEntries = [];

    private static readonly string[] PresetDisplayNames = ProviderDefinition
        .All.Select(d => d.DisplayName)
        .ToArray();

    /// <summary>
    /// 构建完整的 EmbeddingConfig（供 SettingsDialog 保存用）。
    /// </summary>
    public EmbeddingConfig BuildEmbeddingConfig()
    {
        var (configKey, customName) = ResolveSelectedProvider();
        return new EmbeddingConfig
        {
            // 云端 opt-in（config.yaml 显式字段）：面板不编辑，保存时原样保留。
            Provider = cfg.Embedding.Provider,
            Default = configKey,
            DefaultModel = _model.Value ?? "text-embedding-3-small",
            Model = _model.Value ?? "",
            ApiKeys = _apiKeyEntries
                .Select(e => new ApiKeyEntry
                {
                    Provider = e.IsCustom.Value
                        ? "custom"
                        : ProviderDefinition.All[e.ProviderIdx.Value].ConfigKey,
                    Name = e.IsCustom.Value ? e.Name.Value : null,
                    ApiKey = e.ApiKey.Value,
                    BaseUrl = e.IsCustom.Value ? e.BaseUrl.Value : null,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// 构建 Embedding 设置标签页的 UI。
    /// </summary>
    public Visual Build()
    {
        // Provider 下拉
        _providerDisplayItems = BuildProviderDisplayItems(cfg.Embedding.ApiKeys);
        _providerSelectState.Value = FindProviderDisplayIndex(
            _providerDisplayItems,
            cfg.Embedding.Default
        );
        _providerDropdown = new Select<string>()
            .Items(_providerDisplayItems)
            .SelectedIndex(_providerSelectState)
            .Style(SelectStyle.NoBorder);
        _providerDropdown.HorizontalAlignment = Align.Stretch;
        _providerDropdown.SelectionChanged((_, _) => _ = LoadModelsAsync());

        // 模型：手动输入 + 服务端模型下拉（从提供商 /models 加载成功后替换显示）
        _modelTextBox = new TextBox()
            .Text(_model)
            .Placeholder("text-embedding-3-small")
            .HorizontalAlignment(Align.Stretch);
        _modelSelect = new Select<string>().Style(SelectStyle.NoBorder);
        _modelSelect.IsVisible = false;
        _modelSelect.HorizontalAlignment = Align.Stretch;
        var modelCell = new VStack(_modelSelect, _modelTextBox).Spacing(1);

        // 预设按钮
        var presetRow = new HStack(
            CreatePresetBtn("text-embedding-3-small", "OpenAI Small"),
            CreatePresetBtn("text-embedding-3-large", "OpenAI Large"),
            CreatePresetBtn("text-embedding-ada-002", "Ada 002")
        ).Spacing(1);

        // 顶部表单
        var topGrid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(12) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(new Markup("[dim]提供商[/]"), 0, 0)
            .Cell(_providerDropdown, 0, 1)
            .Cell(new Markup("[dim]模型[/]"), 1, 0)
            .Cell(modelCell, 1, 1)
            .Cell(new Markup("[dim]预设[/]"), 2, 0)
            .Cell(presetRow, 2, 1)
            .HorizontalAlignment(Align.Stretch);

        // 加载已有密钥卡片
        foreach (var entry in cfg.Embedding.ApiKeys)
        {
            if (ProviderDefinition.IsCustom(entry.Provider))
            {
                AddApiKeyCard(-1, entry.ApiKey, entry.BaseUrl, entry.Name);
            }
            else
            {
                var idx = Array.FindIndex(
                    PresetDisplayNames,
                    p => ProviderDefinition.FindByDisplayName(p)?.ConfigKey == entry.Provider
                );
                AddApiKeyCard(idx < 0 ? 0 : idx, entry.ApiKey, null, null);
            }
        }
        RefreshProviderDropdown();
        _ = LoadModelsAsync();

        // 说明
        var desc = new Markup(
            "[dim]独立配置 embedding 连接器，留空时降级为纯 BM25 检索（无向量语义）。\n"
                + "未配置独立 embedding 密钥时自动回退到聊天连接器。[/]"
        );

        var stack = new VStack(
            desc,
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
        ).Spacing(1);

        // 页脚：本地判定（System One）· 可选 区块（由 SettingsDialog 注入；内容自带标题）
        if (optionalFooter is not null)
        {
            stack.Children.Add(new Markup("[dim]──── 本地判定（System One）· 可选 ────[/]"));
            stack.Children.Add(optionalFooter);
        }

        return PanelThemes.CreateScrollableTabContent(stack);
    }

    private Button CreatePresetBtn(string model, string label)
    {
        return new Button(new Markup($"[dim]{label}[/]")).Click(() => _model.Value = model);
    }

    // ── 密钥卡片 ──

    private void AddApiKeyCard(int providerIdx, string? apiKey, string? baseUrl, string? name)
    {
        var isCustom = providerIdx < 0;
        var items = new List<string>(PresetDisplayNames) { "自定义" };
        var initialIdx = isCustom ? items.Count - 1 : Math.Clamp(providerIdx, 0, items.Count - 2);

        var state = new EmbeddingKeyCardState
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
            .Placeholder("Embedding 名称")
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
            .Placeholder("https://…/v1")
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

        cardSelect.SelectionChanged(
            (_, _) =>
            {
                var selectedCustom = state.ProviderIdx.Value == items.Count - 1;
                state.IsCustom.Value = selectedCustom;
                nameInput.IsVisible = selectedCustom;
                nameLabel.IsVisible = selectedCustom;
                urlInput.IsVisible = selectedCustom;
                urlLabel.IsVisible = selectedCustom;
                // 自定义密钥卡应自动成为当前激活的 embedding 提供商
                RefreshProviderDropdown(selectedCustom ? ResolveKeyForCard(state) : null);
                _ = LoadModelsAsync();
            }
        );
        nameInput.TextInput(
            (_, _) =>
                RefreshProviderDropdown(state.IsCustom.Value ? ResolveKeyForCard(state) : null)
        );

        AddCard(state, grid, deleteBtn);
    }

    private void AddCard(EmbeddingKeyCardState state, Grid grid, Button deleteBtn)
    {
        var border = new Border(grid) { Padding = new Thickness(1, 0, 1, 0) };
        _apiKeyEntries.Add(state);
        _apiKeyCards.Children.Add(border);

        deleteBtn.Click(() =>
        {
            _apiKeyCards.Children.Remove(border);
            _apiKeyEntries.Remove(state);
            RefreshProviderDropdown();
            _ = LoadModelsAsync();
        });
    }

    // ── Provider 下拉 helpers ──
    /// <summary>
    /// 从当前激活的提供商加载模型列表：成功则用下拉替换手动输入，失败/无密钥回退手动输入。
    /// 复用聊天设置（ProviderTab）同一套 ModelDiscoveryService（含自定义提供商 /models 拼接）。
    /// </summary>
    private async Task LoadModelsAsync()
    {
        if (_modelSelect is null || _modelTextBox is null)
            return;

        var (configKey, customName) = ResolveSelectedProvider();

        // 从当前密钥卡片中找激活的条目（与 RefreshProviderDropdown 同一映射逻辑）
        ApiKeyEntry? entry = null;
        if (!ProviderDefinition.IsCustom(configKey))
        {
            entry = _apiKeyEntries
                .Where(e => !e.IsCustom.Value)
                .Select(e => new ApiKeyEntry
                {
                    Provider = ProviderDefinition.All[e.ProviderIdx.Value].ConfigKey,
                    ApiKey = e.ApiKey.Value ?? "",
                })
                .FirstOrDefault(k => k.Provider == configKey);
        }
        else if (!string.IsNullOrEmpty(customName))
        {
            entry = _apiKeyEntries
                .Where(e => e.IsCustom.Value && e.Name.Value == customName)
                .Select(e => new ApiKeyEntry
                {
                    Provider = "custom",
                    Name = e.Name.Value,
                    ApiKey = e.ApiKey.Value ?? "",
                    BaseUrl = e.BaseUrl.Value,
                })
                .FirstOrDefault();
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
        if (models.Count == 0)
        {
            SetModelFallback();
            return;
        }

        _availableModels = [.. models];
        _modelSelect.Items.Clear();
        _modelSelect.Items.AddRange(models);

        // 尝试选中当前保存的模型
        var currentModel = _model.Value;
        var matchIdx = models.FindIndex(m =>
            string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase)
        );
        _modelSelect.SelectedIndex = matchIdx >= 0 ? matchIdx : 0;

        // 下拉选中 → 同步到 _model State（保存时取得到）
        if (!_modelSelectHandlerWired)
        {
            _modelSelect.SelectionChanged(
                (_, _) =>
                {
                    if (_modelSelect is not null && _availableModels.Length > 0)
                    {
                        var idx = Math.Clamp(
                            _modelSelect.SelectedIndex,
                            0,
                            _availableModels.Length - 1
                        );
                        _model.Value = _availableModels[idx];
                    }
                }
            );
            _modelSelectHandlerWired = true;
        }

        _modelSelect.IsVisible = true;
        _modelTextBox.IsVisible = false;
    }

    /// <summary>
    /// 模型列表加载失败/无密钥 → 回退手动输入。
    /// </summary>
    private void SetModelFallback()
    {
        if (_modelSelect is null || _modelTextBox is null)
            return;
        _modelSelect.IsVisible = false;
        _modelTextBox.IsVisible = true;
        _availableModels = [];
    }

    // ── Provider 下拉 helpers ──

    private void RefreshProviderDropdown(string? forceConfigKey = null)
    {
        if (_providerDropdown is null)
            return;

        var (currentKey, _) = string.IsNullOrEmpty(forceConfigKey)
            ? ResolveSelectedProvider()
            : (forceConfigKey, ProviderDefinition.GetCustomName(forceConfigKey));
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

    private static string[] BuildProviderDisplayItems(List<ApiKeyEntry> apiKeys)
    {
        var items = ProviderDefinition.All.Select(d => d.DisplayName).ToList();
        foreach (var entry in apiKeys)
        {
            if (ProviderDefinition.IsCustom(entry.Provider) && !string.IsNullOrEmpty(entry.Name))
                items.Add($"自定义: {entry.Name}");
        }

        if (items.Count == ProviderDefinition.All.Length)
            items.Add("自定义");

        return [.. items];
    }

    private static int FindProviderDisplayIndex(string[] items, string defaultKey)
    {
        if (!ProviderDefinition.IsCustom(defaultKey))
        {
            var def = ProviderDefinition.FindByConfigKey(defaultKey);
            for (var i = 0; i < items.Length; i++)
                if (items[i] == def.DisplayName)
                    return i;
        }

        var customName = ProviderDefinition.GetCustomName(defaultKey);
        if (customName is not null)
        {
            var target = $"自定义: {customName}";
            for (var i = 0; i < items.Length; i++)
                if (items[i] == target)
                    return i;
        }

        for (var i = 0; i < items.Length; i++)
            if (items[i] == "自定义")
                return i;

        return 0;
    }

    private static string ResolveKeyForCard(EmbeddingKeyCardState e)
    {
        if (e.IsCustom.Value)
            return string.IsNullOrWhiteSpace(e.Name.Value) ? "custom" : $"custom:{e.Name.Value}";
        return ProviderDefinition.All[e.ProviderIdx.Value].ConfigKey;
    }

    private (string ConfigKey, string? CustomName) ResolveSelectedProvider()
    {
        var selected = _providerDisplayItems[_providerSelectState.Value];

        var preset = ProviderDefinition.All.FirstOrDefault(d => d.DisplayName == selected);
        if (preset is not null)
            return (preset.ConfigKey, null);

        if (selected.StartsWith("自定义: "))
        {
            var name = selected["自定义: ".Length..];
            return ($"custom:{name}", name);
        }

        return ("custom", null);
    }
}

/// <summary>
/// 单张 embedding 密钥卡片的 State 容器。
/// </summary>
internal sealed class EmbeddingKeyCardState
{
    public State<int> ProviderIdx = new(0);
    public State<string?> ApiKey = new("", null);
    public State<string?> BaseUrl = new("", null);
    public State<string?> Name = new("", null);
    public State<bool> IsCustom = new(false);
}
