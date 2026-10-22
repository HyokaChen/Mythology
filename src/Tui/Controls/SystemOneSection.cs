using System.Globalization;
using Core.Data;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 「本地判定（System One / d1-omni）· 可选」配置区块：由「嵌入」标签页作为页脚承载。
/// 默认关闭——enabled=false 时不发任何请求；保存后由 SettingsDialog 经 ReloadSystemOne 热生效。
/// 可指向本地 llama.cpp，也可指向托管 Jev（TypeSafe/OpenRouter：改服务地址+路径+访问密钥）。
/// Build() 返回不含滚动容器的 VStack（容器由嵌入页提供）。
/// </summary>
internal sealed class SystemOneSection(SettingsService cfg)
{
    private readonly State<bool> _enabled = new(cfg.SystemOne.Enabled);
    private readonly State<string?> _baseUrl = new(cfg.SystemOne.BaseUrl);
    private readonly State<string?> _path = new(cfg.SystemOne.Path);
    private readonly State<string?> _apiKey = new(cfg.SystemOne.ApiKey);
    private readonly State<string?> _model = new(cfg.SystemOne.Model);
    private readonly State<string?> _timeoutMs = new(
        cfg.SystemOne.TimeoutMs.ToString(CultureInfo.InvariantCulture)
    );
    private readonly State<string?> _maxStateChars = new(
        cfg.SystemOne.MaxStateChars.ToString(CultureInfo.InvariantCulture)
    );
    private readonly State<bool> _rerankEnabled = new(cfg.SystemOne.Rerank.Enabled);
    private readonly State<bool> _conflictsEnabled = new(cfg.SystemOne.Conflicts.Enabled);
    private readonly State<bool> _relationsEnabled = new(cfg.SystemOne.Relations.Enabled);
    private readonly State<string?> _minProbability = new(
        cfg.SystemOne.Relations.MinProbability.ToString(CultureInfo.InvariantCulture)
    );

    // ── 循环判定（有效值 = 主开关 ∧ 子开关；保存时 master 由三开关 OR 推导）──
    private readonly State<bool> _stopCheck = new(
        cfg.SystemOne.Loop.Enabled && cfg.SystemOne.Loop.StopCheck.Enabled
    );
    private readonly State<bool> _driftCheck = new(
        cfg.SystemOne.Loop.Enabled && cfg.SystemOne.Loop.DriftCheck.Enabled
    );
    private readonly State<bool> _stabilityCheck = new(
        cfg.SystemOne.Loop.Enabled && cfg.SystemOne.Loop.StabilityCheck.Enabled
    );

    /// <summary>
    /// 构建完整的 SystemOneConfig（供 SettingsDialog 保存用）。
    /// 未编辑的数值字段保留 config.yaml 现值；非法输入保留原值。
    /// </summary>
    public SystemOneConfig BuildSystemOneConfig()
    {
        var loopOn = _stopCheck.Value || _driftCheck.Value || _stabilityCheck.Value;
        return cfg.SystemOne with
        {
            Enabled = _enabled.Value,
            BaseUrl = string.IsNullOrWhiteSpace(_baseUrl.Value)
                ? cfg.SystemOne.BaseUrl
                : _baseUrl.Value.Trim(),
            Path = NormalizePath(_path.Value, cfg.SystemOne.Path),
            ApiKey = _apiKey.Value?.Trim() ?? "",
            Model = string.IsNullOrWhiteSpace(_model.Value)
                ? cfg.SystemOne.Model
                : _model.Value.Trim(),
            TimeoutMs = ParseInt(_timeoutMs.Value, cfg.SystemOne.TimeoutMs),
            MaxStateChars = ParseInt(_maxStateChars.Value, cfg.SystemOne.MaxStateChars),
            Rerank = cfg.SystemOne.Rerank with { Enabled = _rerankEnabled.Value },
            Conflicts = cfg.SystemOne.Conflicts with { Enabled = _conflictsEnabled.Value },
            Relations = cfg.SystemOne.Relations with
            {
                Enabled = _relationsEnabled.Value,
                MinProbability = ParseProbability(
                    _minProbability.Value,
                    cfg.SystemOne.Relations.MinProbability
                ),
            },
            // 循环判定：三开关全关 → 只关主开关（子开关原值保留，避免无意义写入）；
            // 任一开关打开 → 主开关 = true，三个子开关按 UI 值写入。
            Loop = loopOn
                ? (
                    cfg.SystemOne.Loop with
                    {
                        Enabled = true,
                        StopCheck = cfg.SystemOne.Loop.StopCheck with
                        {
                            Enabled = _stopCheck.Value,
                        },
                        DriftCheck = cfg.SystemOne.Loop.DriftCheck with
                        {
                            Enabled = _driftCheck.Value,
                        },
                        StabilityCheck = cfg.SystemOne.Loop.StabilityCheck with
                        {
                            Enabled = _stabilityCheck.Value,
                        },
                    }
                )
                : cfg.SystemOne.Loop with
                {
                    Enabled = false,
                },
        };
    }

    /// <summary>构建区块 UI（标题 + 字段表 + 子功能开关 + 说明；不含滚动容器）。</summary>
    public Visual Build()
    {
        var grid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(12) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(
                new Switch("启用")
                    .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                    .IsOn(_enabled),
                0,
                0
            )
            .Cell(new Markup("[dim]服务地址[/]"), 1, 0)
            .Cell(
                new TextBox()
                    .Text(_baseUrl)
                    .Placeholder("http://127.0.0.1:8080")
                    .HorizontalAlignment(Align.Stretch),
                1,
                1
            )
            .Cell(new Markup("[dim]接口路径[/]"), 2, 0)
            .Cell(
                new TextBox()
                    .Text(_path)
                    .Placeholder("/v1/systemone")
                    .HorizontalAlignment(Align.Stretch),
                2,
                1
            )
            .Cell(new Markup("[dim]访问密钥[/]"), 3, 0)
            .Cell(
                new TextBox()
                    .Text(_apiKey)
                    .Placeholder("托管 Jev 必填（本地留空）")
                    .HorizontalAlignment(Align.Stretch),
                3,
                1
            )
            .Cell(new Markup("[dim]模型[/]"), 4, 0)
            .Cell(
                new TextBox()
                    .Text(_model)
                    .Placeholder("d1-omni")
                    .HorizontalAlignment(Align.Stretch),
                4,
                1
            )
            .Cell(new Markup("[dim]超时(ms)[/]"), 5, 0)
            .Cell(
                new TextBox()
                    .Text(_timeoutMs)
                    .Placeholder("20000")
                    .HorizontalAlignment(Align.Stretch),
                5,
                1
            )
            .Cell(new Markup("[dim]状态预算[/]"), 6, 0)
            .Cell(
                new TextBox()
                    .Text(_maxStateChars)
                    .Placeholder("3000")
                    .HorizontalAlignment(Align.Stretch),
                6,
                1
            )
            .Cell(
                new HStack(
                    new Switch("重排")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_rerankEnabled),
                    new Switch("冲突判定")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_conflictsEnabled),
                    new Switch("关系提案")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_relationsEnabled)
                ).Spacing(2),
                7,
                0
            )
            .Cell(new Markup("[dim]关系最低概率[/]"), 8, 0)
            .Cell(
                new TextBox()
                    .Text(_minProbability)
                    .Placeholder("0.7")
                    .HorizontalAlignment(Align.Stretch),
                8,
                1
            )
            .Cell(
                new HStack(
                    new Switch("停止复核")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_stopCheck),
                    new Switch("漂移复核")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_driftCheck),
                    new Switch("稳定性复核")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(_stabilityCheck)
                ).Spacing(2),
                9,
                0
            )
            .HorizontalAlignment(Align.Stretch);

        var note = new Markup(
            "[dim]需本机另跑 llama-server（建议 -b 8192 -ub 8192 以容纳更长状态）；"
                + "也可指向托管 Jev（TypeSafe：服务地址 https://api.typesafe.ai + 访问密钥）。"
                + "模型不可达时自动跳过判定，检索不受影响。"
                + "循环判定：停止复核会在一轮无工具调用的收尾后复核是否真的完成（未完成则注入提醒继续）；"
                + "漂移/稳定性复核可压制误报。[/]"
        );

        return new VStack(new Markup("[bold]本地判定（System One）· 可选[/]"), grid, note).Spacing(
            1
        );
    }

    private static string NormalizePath(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var path = text.Trim();
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : fallback;

    private static double ParseProbability(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 1)
            : fallback;
}
