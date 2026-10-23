using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace Desktop.Styles;

/// <summary>
/// 动效原语（全仓唯一实现点）：
/// Enter      — 附加即可见 / IsVisible false→true 时播一次「淡入 + 上浮」（新建视觉树或反复显隐的面）
/// PopupEnter — Popup 每次 Opened 时对 Child 播一次（浮层）
/// PlayEnter / FadeIn — 复用同一视觉树、只能显式调用的场合
/// </summary>
public class Motion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120); // 交互反馈
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(200); // 入场
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(260); // 数值增长
    public const double DefaultRise = 8;

    public static readonly AttachedProperty<bool> EnterProperty = AvaloniaProperty.RegisterAttached<
        Motion,
        Visual,
        bool
    >("Enter");

    public static readonly AttachedProperty<double> EnterRiseProperty =
        AvaloniaProperty.RegisterAttached<Motion, Visual, double>("EnterRise", DefaultRise);

    public static readonly AttachedProperty<bool> PopupEnterProperty =
        AvaloniaProperty.RegisterAttached<Motion, Popup, bool>("PopupEnter");

    public static bool GetEnter(Visual v) => v.GetValue(EnterProperty);

    public static void SetEnter(Visual v, bool value) => v.SetValue(EnterProperty, value);

    public static double GetEnterRise(Visual v) => v.GetValue(EnterRiseProperty);

    public static void SetEnterRise(Visual v, double value) => v.SetValue(EnterRiseProperty, value);

    public static bool GetPopupEnter(Popup p) => p.GetValue(PopupEnterProperty);

    public static void SetPopupEnter(Popup p, bool value) => p.SetValue(PopupEnterProperty, value);

    static Motion()
    {
        EnterProperty.Changed.AddClassHandler<Visual>(
            static (visual, e) =>
            {
                if (e.NewValue is not true)
                    return;
                visual.AttachedToVisualTree += static (sender, args) =>
                {
                    _ = args;
                    if (sender is Visual v && v.IsVisible)
                        _ = PlayEnter(v, GetEnterRise(v));
                };
                visual.PropertyChanged += static (sender, args) =>
                {
                    if (
                        args.Property == Visual.IsVisibleProperty
                        && sender is Visual { IsVisible: true } v
                    )
                        _ = PlayEnter(v, GetEnterRise(v));
                };
            }
        );

        PopupEnterProperty.Changed.AddClassHandler<Popup>(
            static (popup, e) =>
            {
                if (e.NewValue is not true)
                    return;
                popup.Opened += (_, _) =>
                {
                    if (popup.Child is Visual child)
                        _ = PlayEnter(child, 6);
                };
            }
        );
    }

    /// <summary>淡入 + 上浮（位移只走 RenderTransform，不触发布局）。</summary>
    public static async Task PlayEnter(Visual visual, double rise = DefaultRise)
    {
        var translate = visual.RenderTransform as TranslateTransform;
        if (translate is null)
        {
            if (visual.RenderTransform is not null) // 已有 scale/rotate 等：不覆盖，退化为淡入
            {
                await FadeIn(visual);
                return;
            }
            translate = new TranslateTransform();
            visual.RenderTransform = translate;
        }

        visual.Opacity = 0;
        translate.Y = rise;
        await Task.WhenAll(RunOpacity(visual), RunRise(translate, rise));
        visual.Opacity = 1;
        translate.Y = 0;
    }

    /// <summary>仅淡入（对话框/遮罩）。</summary>
    public static async Task FadeIn(Visual visual)
    {
        visual.Opacity = 0;
        await RunOpacity(visual);
        visual.Opacity = 1;
    }

    private static Task RunOpacity(Visual visual) =>
        BuildAnimation(() => Visual.OpacityProperty, 0, 1).RunAsync(visual, CancellationToken.None);

    private static Task RunRise(TranslateTransform translate, double rise) =>
        BuildAnimation(() => TranslateTransform.YProperty, rise, 0)
            .RunAsync(translate, CancellationToken.None);

    private static Animation BuildAnimation(
        Func<AvaloniaProperty> property,
        double from,
        double to
    ) =>
        new()
        {
            Duration = Normal,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property(), from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property(), to) } },
            },
        };
}
