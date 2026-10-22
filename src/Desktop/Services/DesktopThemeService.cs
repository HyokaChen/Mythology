using System.Text.Json;
using Avalonia.Media;
using Avalonia.Styling;

namespace Desktop.Services;

/// <summary>
/// 桌面端主题服务 — 管理浅色/深色切换（对齐 TUI ThemeService 语义，但配色体系独立）。
/// 浅色 = Adaline 纸感（默认，DESIGN.md 主基调）；深色 = Slate Hollow 夜空墨绿（DESIGN.md dark band 延伸）。
/// 实现：运行时替换 Application.Resources 中的 Color/Brush token；
/// 所有 XAML 使用 DynamicResource 引用，故替换后全局即时生效，无需重启。
/// 持久化独立于 TUI 的 config.yaml theme 字段（终端 16 色主题语义不同），写入 ~/.mythology/desktop-ui.json。
/// </summary>
public sealed class DesktopThemeService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mythology",
        "desktop-ui.json"
    );

    /// <summary>浅色调色板快照（首次进入深色前捕获，用于切回浅色时精确还原）。</summary>
    private readonly Dictionary<string, Color> _lightSnapshot = [];

    /// <summary>浅色表层键快照（SukiUI/Fluent 硬编码表层，Color/Brush 混合类型，原值整存整还原）。</summary>
    private readonly Dictionary<string, object> _lightSurfaceSnapshot = [];

    /// <summary>当前是否深色。</summary>
    public bool IsDark { get; private set; }

    /// <summary>主题变更事件（供 ViewModel 刷新按钮文案等）。</summary>
    public event Action? ThemeChanged;

    /// <summary>深色调色板：夜空墨绿（Adaline 反相，保持低饱和与纸感层次）。</summary>
    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        // ── 表面（Linen 纸 → Slate Hollow 夜空，三级层次） ──
        ["ColorLinen"] = Color.Parse("#1c221c"), // 页面底
        ["ColorBone"] = Color.Parse("#252c24"), // 卡片/侧栏
        ["ColorSurfaceGlow"] = Color.Parse("#2b332a"), // 高亮卡片
        ["ColorSlateHollow"] = Color.Parse("#12170f"), // 深色带（更深）
        ["ColorMist"] = Color.Parse("#39432f"), // hairline 边框
        ["ColorMistOnSlate"] = Color.Parse("#4a5641"),
        // ── 文字（Forest Ink 墨 → 亮纸） ──
        ["ColorForestInk"] = Color.Parse("#e9efe1"), // 正文/主 CTA 填充
        ["ColorOlivePress"] = Color.Parse("#c6d3aa"), // 标题/hover
        ["ColorSageGray"] = Color.Parse("#9caa90"), // 辅助文字
        ["ColorSageMist"] = Color.Parse("#71806a"), // 元数据
        // ── 强调（亮化以在暗底保持对比） ──
        ["ColorSageLeaf"] = Color.Parse("#7d9b70"),
        ["ColorDeepTeal"] = Color.Parse("#5f8f84"),
        ["ColorEucalyptus"] = Color.Parse("#333d31"), // tag 底
        ["ColorLichen"] = Color.Parse("#5b6751"), // ghost 边框
        ["ColorCrimsonSpecimen"] = Color.Parse("#cf6689"),
        ["ColorAmberPin"] = Color.Parse("#cfa961"),
    };

    /// <summary>
    /// 深色表层键：MythologyTheme 里硬编码的 SukiUI 表层色（Color 类型）不随 DarkPalette 翻转，
    /// 深色下 tooltip / 弹层会残留浅色 —— 按 DarkPalette 表面三级映射单独覆盖。
    /// </summary>
    private static readonly Dictionary<string, Color> DarkSurfaceColors = new()
    {
        ["SukiCardBackground"] = Color.Parse("#2b332a"), // = 深色 SurfaceGlow
        ["SukiBackground"] = Color.Parse("#2b332a"),
        ["SukiText"] = Color.Parse("#e9efe1"), // = 深色 ForestInk
        ["SukiMuteText"] = Color.Parse("#9caa90"), // = 深色 SageGray
        ["SukiMenuBorderBrush"] = Color.Parse("#39432f"), // = 深色 Mist
        ["SukiLightBorderBrush"] = Color.Parse("#39432f"),
    };

    /// <summary>深色表层键（SolidColorBrush 类型：Fluent 菜单浮层）。</summary>
    private static readonly Dictionary<string, Color> DarkSurfaceBrushes = new()
    {
        ["MenuFlyoutItemBackgroundPointerOver"] = Color.Parse("#333d31"), // = 深色 Eucalyptus
        ["MenuFlyoutItemForegroundPointerOver"] = Color.Parse("#e9efe1"),
        ["MenuFlyoutItemBackgroundPressed"] = Color.Parse("#333d31"),
        ["MenuFlyoutItemForegroundPressed"] = Color.Parse("#e9efe1"),
        ["MenuFlyoutItemBackgroundDisabled"] = Color.Parse("#26000000"),
        ["MenuFlyoutItemForegroundDisabled"] = Color.Parse("#71806a"),
        ["MenuFlyoutPresenterBackground"] = Color.Parse("#2b332a"),
        ["MenuFlyoutPresenterBorderBrush"] = Color.Parse("#39432f"),
        ["MenuFlyoutSubItemChevron"] = Color.Parse("#9caa90"),
        ["MenuFlyoutSubItemChevronPointerOver"] = Color.Parse("#e9efe1"),
    };

    /// <summary>启动时恢复持久化主题（无状态文件则浅色）。</summary>
    public void Restore()
    {
        try
        {
            if (!File.Exists(StatePath))
                return;
            var json = File.ReadAllText(StatePath);
            var state = JsonSerializer.Deserialize<UiState>(json);
            if (state?.Theme == "dark")
                SetDark(true);
        }
        catch
        {
            // 状态文件损坏：静默回退浅色
        }
    }

    /// <summary>在浅色/深色之间切换（Ctrl+T、/theme）。</summary>
    public void Toggle() => SetDark(!IsDark);

    /// <summary>设置主题（即时生效并持久化）。</summary>
    public void SetDark(bool dark)
    {
        if (IsDark == dark)
            return;

        if (Avalonia.Application.Current is not { } app)
            return;

        if (dark)
        {
            CaptureLightSnapshot(app);
            ApplyPalette(app, DarkPalette);
            ApplySurfacePalette(app);
        }
        else
        {
            ApplyPalette(app, _lightSnapshot);
            // 表层键类型不一（Color/Brush），按快照原值写回
            foreach (var (key, value) in _lightSurfaceSnapshot)
                app.Resources[key] = value;
        }

        IsDark = dark;

        // SukiUI 基础主题跟随（让 Suki 自带控件的滚动条/弹层也变暗）
        try
        {
            SukiUI
                .SukiTheme.GetInstance()
                .ChangeBaseTheme(dark ? ThemeVariant.Dark : ThemeVariant.Light);
        }
        catch
        {
            // Suki 主题不可用时忽略：自定义 token 已切换
        }

        Persist();
        ThemeChanged?.Invoke();
    }

    /// <summary>首次切深色前，记录浅色原值（避免硬编码两套表导致漂移）。</summary>
    private void CaptureLightSnapshot(Avalonia.Application app)
    {
        if (_lightSnapshot.Count > 0)
            return;
        foreach (var key in DarkPalette.Keys)
        {
            if (app.Resources.TryGetResource(key, null, out var val) && val is Color c)
                _lightSnapshot[key] = c;
        }
        foreach (var key in DarkSurfaceColors.Keys.Concat(DarkSurfaceBrushes.Keys))
        {
            if (app.Resources.TryGetResource(key, null, out var val) && val is not null)
                _lightSurfaceSnapshot[key] = val;
        }
    }

    /// <summary>写入 Color token 及其同名 Brush token（DynamicResource 消费者自动刷新）。</summary>
    private static void ApplyPalette(Avalonia.Application app, Dictionary<string, Color> palette)
    {
        foreach (var (colorKey, color) in palette)
        {
            app.Resources[colorKey] = color;

            // ColorForestInk → BrushForestInk（Brush 定义在已合并字典中，ContainsKey 查不到，
            // 故直接写顶层 Resources；此层优先于 MergedDictionaries，多余 key 无害）
            app.Resources["Brush" + colorKey["Color".Length..]] = new SolidColorBrush(color);
        }
    }

    /// <summary>深色表层键写入（Color 直写；Brush 键包 SolidColorBrush）。</summary>
    private static void ApplySurfacePalette(Avalonia.Application app)
    {
        foreach (var (key, color) in DarkSurfaceColors)
            app.Resources[key] = color;
        foreach (var (key, color) in DarkSurfaceBrushes)
            app.Resources[key] = new SolidColorBrush(color);
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new UiState { Theme = IsDark ? "dark" : "light" });
            File.WriteAllText(StatePath, json);
        }
        catch
        {
            // 持久化失败不影响运行时主题
        }
    }

    private sealed class UiState
    {
        public string Theme { get; set; } = "light";
    }
}
