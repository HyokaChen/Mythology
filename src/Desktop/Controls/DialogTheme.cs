using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Desktop.Controls;

/// <summary>
/// 对话框统一样式工具（对齐 DESIGN.md / Adaline）：
/// 供代码构建型窗口复用与 XAML 页面一致的控件外观。
///
/// ⚠️ 关键原则：code-built 控件通过 Classes 挂到全局样式（MythologyStyles.axaml），
/// 不设置本地 Background/Foreground 等视觉值——本地值优先级高于 Style.Setter，
/// 会覆盖 Adaline 样式（这是此前按钮/输入框外观不对齐的根因）。
/// </summary>
public static class DialogTheme
{
    // ── Adaline 色板（与 MythologyTheme.axaml 同步，仅保留实际使用的 token） ──
    public static readonly SolidColorBrush ForestInk = new(Color.Parse("#0a1d08"));
    public static readonly SolidColorBrush SageLeaf = new(Color.Parse("#4a6d47"));
    public static readonly SolidColorBrush CrimsonSpecimen = new(Color.Parse("#991e4b"));
    public static readonly SolidColorBrush AmberPin = new(Color.Parse("#80581c"));
    public static readonly SolidColorBrush SageGray = new(Color.Parse("#6b7860"));
    public static readonly SolidColorBrush SageMistBrush = new(Color.Parse("#a5ac9f"));
    public static readonly SolidColorBrush DeepTealBrush = new(Color.Parse("#2b6b5e"));

    /// <summary>对话框窗口底色（Design 页面底色 Linen）。</summary>
    public static readonly SolidColorBrush WindowBackground = new(Color.Parse("#f8f9f5"));

    // ── 字体 ──
    // 与 MythologyTheme.axaml 的 FontSans 同栈（Inter 无 CJK 字形——缺回退时中英混排行高/基线漂移）
    public static readonly FontFamily FontSans = new(
        "Inter, PingFang SC, Microsoft YaHei, Noto Sans CJK SC"
    );

    /// <summary>设置对话框通用窗口外观（Linen 底 + 居中 + Esc 关闭）。</summary>
    public static void ApplyWindowStyle(
        Window window,
        string title,
        int width = 520,
        int height = 400
    )
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.MinWidth = Math.Min(width, 460);
        window.MinHeight = Math.Min(height, 340);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Background = WindowBackground;
    }
}
