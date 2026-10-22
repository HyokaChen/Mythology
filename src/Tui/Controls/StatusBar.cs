using System.Globalization;
using Core.Cloud;
using Core.Data;
using Tui.Data;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 底部状态栏：左侧显示旋转动画 + 诗词，右侧显示小说名称 + 模型信息。
/// </summary>
public sealed class MythStatusBar
{
    /// <summary>旋转动画控件。</summary>
    private readonly Spinner _spinner;

    /// <summary>状态文本（显示诗词或 idle 状态）。</summary>
    private readonly State<string?> _statusText = new("💤 idle");

    /// <summary>小说名称。</summary>
    private readonly State<string?> _novelName = new(null);

    /// <summary>Token 计数文本。</summary>
    private readonly State<string?> _tokenText = new(null);

    /// <summary>增强模式文本。</summary>
    private readonly State<string?> _enhanceModeText = new(null);

    /// <summary>增强模式完整标签（tooltip 展示用）。</summary>
    private readonly State<string?> _enhanceModeTooltip = new(null);

    /// <summary>完整状态文本（未截断诗歌，tooltip 展示用）。</summary>
    private readonly State<string?> _statusFull = new(null);

    /// <summary>完整小说名（未截断，tooltip 展示用）。</summary>
    private string? _novelNameFull;

    /// <summary>状态文本 tooltip 内容（Run/Compact 时挂载，Idle 时移除）。</summary>
    private readonly Markup _statusTooltipContent;

    /// <summary>状态文本 tooltip 宿主（Idle 时不挂内容，避免空白 tooltip）。</summary>
    private readonly TooltipHost _statusTooltipHost;

    /// <summary>小说名 tooltip 宿主。</summary>
    private readonly TooltipHost _novelTooltipHost;

    /// <summary>增强模式 tooltip 宿主。</summary>
    private readonly TooltipHost _enhanceTooltipHost;

    /// <summary>云端连接状态文本（右侧标识区）。</summary>
    private readonly State<string?> _cloudText = new(null);

    /// <summary>云端同步状态文本（右侧标识区，🌐 连接 + ⇅ 同步两个标识）。</summary>
    private readonly State<string?> _syncText = new(null);

    /// <summary>同步状态 tooltip（完整说明）。</summary>
    private readonly State<string?> _syncTooltip = new(null);

    /// <summary>
    /// 设置云端连接状态（状态栏右侧 🌐 标识）：
    /// Connected → 🌐 绿 / Authenticating·Disabled → 🌐 暗淡 / RateLimited·Unavailable → 🌐+❗/✕。
    /// </summary>
    public void SetCloudState(CloudConnectionState state)
    {
        var text = state switch
        {
            CloudConnectionState.Connected => "[green]🌐[/]",
            CloudConnectionState.Authenticating => "[dim]🌐[/]",
            CloudConnectionState.Disabled => "[dim]🌐[/]",
            CloudConnectionState.RateLimited => "[yellow]🌐❗[/]",
            CloudConnectionState.QuotaExceeded => "[yellow]🌐$[/]",
            CloudConnectionState.Unavailable => "[red]🌐✕[/]",
            _ => "[dim]🌐[/]",
        };
        _cloudText.Value = text;
        UpdateSyncSegment();
    }

    /// <summary>设置云端同步状态（状态栏右侧 ⇅ 标识；null = 未接线不显示）。</summary>
    public void SetCloudSyncState(ClientPlatform.CloudSyncState state)
    {
        _syncText.Value = state switch
        {
            ClientPlatform.CloudSyncState.Syncing => "[aqua]⇅…[/]",
            ClientPlatform.CloudSyncState.Synced => "[green]⇅✓[/]",
            ClientPlatform.CloudSyncState.Conflicts => "[amber]⇅![/]",
            ClientPlatform.CloudSyncState.Offline => "[dim]⇅ø[/]",
            _ => null,
        };
        _syncTooltip.Value = state switch
        {
            ClientPlatform.CloudSyncState.Syncing => "云端同步中…",
            ClientPlatform.CloudSyncState.Synced => "已与云端同步",
            ClientPlatform.CloudSyncState.Conflicts => "有未合并冲突（cloud-conflicts/ 下有副本）",
            ClientPlatform.CloudSyncState.Offline => "云端不可达（网络/服务异常）",
            _ => "云端同步",
        };
        UpdateSyncSegment();
    }

    private void UpdateSyncSegment()
    {
        // 组合展示：🌐 连接 + ⇅ 同步（同步未接线时只显示 🌐）
        var sync = _syncText.Value;
        var cloud = _cloudText.Value;
        _syncSegment.Value = sync is null ? cloud : $"{cloud} {sync}";
    }

    private readonly State<string?> _syncSegment = new(null);

    /// <summary>当前增强模式。</summary>
    private string _enhanceMode = "semi";

    /// <summary>诗词随机数生成器。</summary>
    private readonly Random _poetryRng = new();

    /// <summary>Agent 展示状态（AgentUiState）。</summary>
    private TuiState _agentState = TuiState.Idle;

    /// <summary>模型信息。</summary>
    private readonly State<string?> _modelInfo = new(null);

    /// <summary>面板颜色配置。</summary>
    private PanelColors _colors;

    /// <summary>状态栏左侧文本（诗歌）最大显示宽度（仅超长诗句才截断）。</summary>
    private const int StatusTextMaxWidth = 64;

    /// <summary>小说名最大显示宽度（📖 图标另计，仅超长书名才截断）。</summary>
    private const int NovelNameMaxWidth = 24;

    // ── 测试缝（internal，仅 Tui.Tests 可见） ──

    internal string StatusText => _statusText.Value ?? "";
    internal string TokenText => _tokenText.Value ?? "";
    internal string EnhanceModeText => _enhanceModeText.Value ?? "";
    internal string NovelNameText => _novelName.Value ?? "";
    internal string StatusFullText => _statusFull.Value ?? "";
    internal string NovelNameFull => _novelNameFull ?? "";
    internal string EnhanceModeTooltipText => _enhanceModeTooltip.Value ?? "";

    /// <summary>Footer 控件实例。</summary>
    public Footer Footer { get; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="modelInfo">模型信息字符串。</param>
    /// <param name="colors">面板颜色配置。</param>
    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="modelInfo">模型信息字符串。</param>
    /// <param name="colors">面板颜色配置。</param>
    public MythStatusBar(string modelInfo, PanelColors colors)
    {
        _modelInfo.Value = modelInfo;
        _colors = colors;

        // Spinner 默认隐藏（IsActive=false）。
        // 当 LLM 开始运行时，SetAgentState(AgentUiState.Run) 设置 IsActive=true → 动画启动。
        // 空闲时，spinner 隐藏并通过 _statusText 显示 💤 emoji。
        _spinner = new Spinner().Style(SpinnerStyles.Dots with { Foreground = colors.AccentBlue });
        _spinner.IsActive = false;
        _spinner.IsVisible = false;

        // Tooltip 宿主：idle 时状态文本不挂 tooltip 内容（避免空白 tooltip 弹窗）
        _statusTooltipContent = new Markup(() => _statusFull.Value ?? "");
        _statusTooltipHost = new TooltipHost(new Markup(_statusText));
        _novelTooltipHost = new TooltipHost(new Markup(_novelName))
        {
            TooltipContent = new Markup(() => _novelNameFull ?? ""),
        };
        _enhanceTooltipHost = new TooltipHost(new Markup(_enhanceModeText))
        {
            TooltipContent = new Markup(() => _enhanceModeTooltip.Value ?? ""),
        };

        Footer = new Footer()
            .Style(new FooterStyle { Background = _colors.StatusBg, Foreground = _colors.StatusFg })
            .Left(
                new HStack(
                    new Padder { Content = _spinner, Padding = new Thickness(2, 0, 0, 0) },
                    _statusTooltipHost
                ).Spacing(1)
            )
            .Center(new Markup(""))
            .Right(
                // §11 点 4（S4）：右侧区块间插动态 │ 分隔符（对应 State 为空时不显示）
                new HStack(
                    // 云端 + 同步标识（🌐 连接态 / ⇅ 同步态；未登录时整体隐藏）
                    new TooltipHost(new Markup(() => _syncSegment.Value ?? ""))
                    {
                        TooltipContent = new Markup(() => _syncTooltip.Value ?? "云端连接"),
                    },
                    new Markup(() => string.IsNullOrEmpty(_syncSegment.Value) ? "" : "[dim]│[/]"),
                    _novelTooltipHost,
                    new Markup(() => string.IsNullOrEmpty(_novelName.Value) ? "" : "[dim]│[/]"),
                    _enhanceTooltipHost,
                    new Markup(() =>
                        string.IsNullOrEmpty(_enhanceModeText.Value) ? "" : "[dim]│[/]"
                    ),
                    new Markup(_tokenText),
                    new Markup(() => string.IsNullOrEmpty(_tokenText.Value) ? "" : "[dim]│[/]"),
                    new Markup(() => _modelInfo.Value ?? "")
                ).Spacing(2)
            );
    }

    /// <summary>
    /// 设置 Agent 状态（§11 点 4 S1：三态分色——
    /// idle 弱化、compact amber 警示、run 诗词 italic 沙色 + spinner 铜色动画）。
    /// </summary>
    /// <param name="state">Agent 展示状态（Idle/Run/Compact）。</param>
    public void SetAgentState(TuiState state)
    {
        _agentState = state;
        switch (_agentState)
        {
            case TuiState.Run:
                _spinner.IsVisible = true;
                _spinner.IsActive = true;
                var poem = PoetryStore.Lines[_poetryRng.Next(PoetryStore.Lines.Length)];
                _statusFull.Value = poem;
                _statusTooltipHost.TooltipContent = _statusTooltipContent;
                _statusText.Value = $"[italic]{ClipDisplay(poem, StatusTextMaxWidth)}[/]";
                break;
            case TuiState.Compact:
                _spinner.IsVisible = true;
                _spinner.IsActive = true;
                _statusFull.Value = "🔄 正在压缩上下文...";
                _statusTooltipHost.TooltipContent = _statusTooltipContent;
                _statusText.Value = "[dim]🔄 正在压缩上下文...[/]";
                break;
            default:
                _spinner.IsActive = false;
                _spinner.IsVisible = false;
                _statusFull.Value = null;
                _statusTooltipHost.TooltipContent = null;
                _statusText.Value = "💤 idle";
                break;
        }
    }

    /// <summary>
    /// 设置小说名称（状态栏空间有限，不再展示题材）。
    /// </summary>
    /// <param name="name">小说名称。</param>
    public void SetNovelName(string? name)
    {
        _novelNameFull = string.IsNullOrEmpty(name) ? null : name;
        var display = string.IsNullOrEmpty(name) ? "" : ClipDisplay(name, NovelNameMaxWidth);
        _novelName.Value = string.IsNullOrEmpty(display) ? "" : $"[bold]📖 {display}[/]";
    }

    /// <summary>
    /// 设置模型信息。
    /// </summary>
    /// <param name="modelInfo">模型信息字符串。</param>
    public void SetModelInfo(string modelInfo) => _modelInfo.Value = modelInfo;

    /// <summary>
    /// 设置增强模式。
    /// </summary>
    /// <param name="mode">模式：none/semi/full</param>
    public void SetEnhanceMode(string mode)
    {
        _enhanceMode = mode;
        _enhanceModeText.Value = mode switch
        {
            "full" => "[bold red]⚡[/]",
            "semi" => "[bold yellow]⚡[/]",
            "none" => "[dim]✋[/]",
            _ => "",
        };
        _enhanceModeTooltip.Value = mode switch
        {
            "full" => "增强模式：全自动",
            "semi" => "增强模式：半自动",
            "none" => "增强模式：手动",
            _ => "",
        };
    }

    /// <summary>
    /// 切换增强模式（循环：none → semi → full → none）。
    /// </summary>
    /// <returns>切换后的模式</returns>
    public string CycleEnhanceMode()
    {
        _enhanceMode = _enhanceMode switch
        {
            "none" => "semi",
            "semi" => "full",
            "full" => "none",
            _ => "semi",
        };
        SetEnhanceMode(_enhanceMode);
        return _enhanceMode;
    }

    /// <summary>
    /// 按终端显示宽度截断文本（CJK/emoji 双宽），超出部分以 … 结尾。
    /// </summary>
    private static string ClipDisplay(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 1)
            return text;
        var clipped = MarkdownConverter.TruncateToDisplayWidthPublic(text, maxWidth - 1);
        return clipped.Length < text.Length ? clipped + "…" : text;
    }

    /// <summary>
    /// 设置 Token 统计：当前会话上下文 / 模型上下文窗口。
    /// 展示形式：📊 12.3k / 128k（&lt;1000 原样，&lt;1,000,000 用 k，否则用 m）。
    /// 超过 warningRatio（默认 90%）显示黄色提醒，超过窗口 100% 显示红色。
    /// </summary>
    /// <param name="usedTokens">当前会话已占用的上下文 token 数。</param>
    /// <param name="windowTokens">当前选中模型的上下文窗口大小（tokens）。</param>
    /// <param name="warningRatio">上下文占用达到窗口该比例时显示警告色。</param>
    public void SetTokenStats(long usedTokens, long windowTokens, double warningRatio = 0.9)
    {
        if (windowTokens <= 0)
        {
            _tokenText.Value = "";
            return;
        }
        if (usedTokens <= 0)
        {
            _tokenText.Value = $"[dim]📊 0 / {FormatTokens(windowTokens)}[/]";
            return;
        }
        var ratio = (double)usedTokens / windowTokens;
        var style =
            ratio >= 1.0 ? "[bold red]"
            : ratio >= warningRatio ? "[yellow]"
            : "[dim]";
        _tokenText.Value =
            $"{style}📊 {FormatTokens(usedTokens)} / {FormatTokens(windowTokens)}[/]";
    }

    /// <summary>
    /// 压缩 token 数值为紧凑单位：128000 → "128k"，12345 → "12.3k"，1500000 → "1.5m"。
    /// </summary>
    private static string FormatTokens(long value)
    {
        if (value < 1000)
            return value.ToString(CultureInfo.InvariantCulture);
        if (value < 1_000_000)
        {
            var k = value / 1000.0;
            return k >= 1000 ? $"{Trim(k / 1000.0)}m" : $"{Trim(k)}k";
        }
        return $"{Trim(value / 1_000_000.0)}m";
    }

    /// <summary>数值格式化为最多 1 位小数（去掉尾随 .0）。</summary>
    private static string Trim(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// 应用主题颜色。
    /// </summary>
    /// <param name="colors">面板颜色配置。</param>
    public void ApplyTheme(PanelColors colors)
    {
        _colors = colors;
        Footer.Style(
            new FooterStyle { Background = colors.StatusBg, Foreground = colors.StatusFg }
        );
        _spinner.Style(SpinnerStyles.Dots with { Foreground = colors.AccentBlue });
    }
}
