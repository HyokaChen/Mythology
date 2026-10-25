using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Desktop.Styles;

/// <summary>
/// 动效原语（全仓唯一实现点）：
/// Enter         — 附加即可见 / IsVisible false→true 时播一次「淡入 + 上浮」
/// PopupEnter    — Popup 每次 Opened 时对 Child 播一次（可用 PopupEnterRise 定方向）
/// Stagger       — 列表容器附加：项按索引错峰淡入（只播一次，复用容器不重播）
/// PlayEnter / PlayPageEnter / FadeIn — 复用同一视觉树、只能显式调用的场合
/// PlayExit      — 淡出 + 下沉；仅用于显式 Close() 的覆盖层，调用方负责关闭
///
/// 时序按 DESIGN.md 的印刷基调：只做淡入与 ≤10px 位移，不用回弹/过冲；
/// 位移一律走 RenderTransform（不参与布局，不触发重排）。
/// 每个视觉同一时刻只允许一段动画：重复触发会取消上一段，避免两个时钟抢同一属性。
/// </summary>
public class Motion
{
    /// <summary>按下 / 悬停等即时反馈。</summary>
    public static readonly TimeSpan Micro = TimeSpan.FromMilliseconds(90);

    /// <summary>浮层、行内小元素入场。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    /// <summary>页面 / 消息行 / 对话框入场。</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(160);

    /// <summary>数值增长（进度条）。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(240);

    /// <summary>消息行默认上浮距离。</summary>
    public const double DefaultRise = 6;

    /// <summary>浮层默认上浮距离（提示框在输入框上方，从下往上落位）。</summary>
    public const double PopupRise = 4;

    /// <summary>页面切换的横向位移。</summary>
    public const double PageShift = 10;

    /// <summary>行内小元素（chip / 待办项 / 来源）的上浮距离。</summary>
    public const double SmallRise = 4;

    /// <summary>逐项入场的错峰步长（毫秒）。</summary>
    public const int StaggerStepMs = 18;

    /// <summary>只给前 N 项排错峰延迟；再往后一次性淡入，避免长列表首屏被拖成慢动作。</summary>
    public const int MaxStaggerItems = 12;

    /// <summary>已播放过入场动画的项容器（避免复用容器时重播）。</summary>
    private static readonly ConditionalWeakTable<Control, object?> Played = new();

    /// <summary>正在播放的动画（取消上一段，防止属性竞争）。</summary>
    private static readonly ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();

    /// <summary>临时探针输出（仅 MYTH_MOTION_PROBE=1 时接线）。</summary>
    public static readonly AttachedProperty<bool> EnterProperty = AvaloniaProperty.RegisterAttached<
        Motion,
        Visual,
        bool
    >("Enter");

    public static readonly AttachedProperty<double> EnterRiseProperty =
        AvaloniaProperty.RegisterAttached<Motion, Visual, double>("EnterRise", DefaultRise);

    public static readonly AttachedProperty<bool> PopupEnterProperty =
        AvaloniaProperty.RegisterAttached<Motion, Popup, bool>("PopupEnter");

    public static readonly AttachedProperty<double> PopupEnterRiseProperty =
        AvaloniaProperty.RegisterAttached<Motion, Popup, double>("PopupEnterRise", PopupRise);

    /// <summary>
    /// 列表逐项入场：容器（ItemsControl / ListBox）挂 true，每个生成的项容器按索引错峰播放。
    /// 只在项「新建」时播放（集合追加），复用同一项时不重播。
    /// </summary>
    public static readonly AttachedProperty<bool> StaggerProperty =
        AvaloniaProperty.RegisterAttached<Motion, ItemsControl, bool>("Stagger");

    public static bool GetStagger(ItemsControl c) => c.GetValue(StaggerProperty);

    public static void SetStagger(ItemsControl c, bool value) => c.SetValue(StaggerProperty, value);

    public static bool GetEnter(Visual v) => v.GetValue(EnterProperty);

    public static void SetEnter(Visual v, bool value) => v.SetValue(EnterProperty, value);

    public static double GetEnterRise(Visual v) => v.GetValue(EnterRiseProperty);

    public static void SetEnterRise(Visual v, double value) => v.SetValue(EnterRiseProperty, value);

    public static bool GetPopupEnter(Popup p) => p.GetValue(PopupEnterProperty);

    public static void SetPopupEnter(Popup p, bool value) => p.SetValue(PopupEnterProperty, value);

    public static double GetPopupEnterRise(Popup p) => p.GetValue(PopupEnterRiseProperty);

    public static void SetPopupEnterRise(Popup p, double value) =>
        p.SetValue(PopupEnterRiseProperty, value);

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
                        _ = PlayEnter(child, GetPopupEnterRise(popup), Fast);
                };
            }
        );

        StaggerProperty.Changed.AddClassHandler<ItemsControl>(
            static (items, e) =>
            {
                if (e.NewValue is not true)
                    return;
                items.Loaded += static (sender, _) =>
                {
                    if (sender is not ItemsControl { IsLoaded: true } list)
                        return;
                    StaggerChildren(list);
                };
                // 追加项才触发（流式消息 / 新待办）：已播过的容器按 Played 表跳过。
                // 不用 LayoutUpdated：它在每次布局都触发，是热路径。
                items.Items.CollectionChanged += (_, _) =>
                {
                    if (items.IsLoaded)
                        StaggerChildren(items);
                };
            }
        );
    }

    /// <summary>
    /// 对列表中尚未播过入场的项容器按索引错峰播放。
    /// 项容器由虚拟化面板生成，位置不固定，只能扫视觉树（已播过的按 <see cref="Played"/> 跳过，
    /// 所以重复触发的成本是「遍历 + 查表」，不再起新动画）。
    /// 只给前 <see cref="MaxStaggerItems"/> 项排延迟：再往后一次性淡入，
    /// 避免长列表首屏被拖成慢动作（用户观感 = 卡）。
    /// </summary>
    private static void StaggerChildren(ItemsControl items)
    {
        if (items is not { IsLoaded: true })
            return;

        var animated = 0;
        foreach (var child in items.GetVisualDescendants().OfType<Control>())
        {
            if (child is not ContentPresenter and not ListBoxItem)
                continue;
            if (Played.TryGetValue(child, out _))
                continue;

            // 前 N 项错峰；之后立即播（不再累计延迟）
            var delay =
                animated < MaxStaggerItems
                    ? TimeSpan.FromMilliseconds(animated * StaggerStepMs)
                    : TimeSpan.Zero;
            _ = PlayEnterDelayed(child, SmallRise, delay);
            Played.AddOrUpdate(child, null);
            animated++;
        }
    }

    /// <summary>带延迟的入场（错峰用；延迟期间不可取消，但动画本身仍走单时钟表）。</summary>
    private static async Task PlayEnterDelayed(Visual visual, double rise, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay).ConfigureAwait(true);
        await PlayEnter(visual, rise, Fast);
    }

    /// <summary>页面前进/后退的入场方向（ten px；向后翻页从右侧进入）。</summary>
    public static double PageEnterOffset(int fromIndex, int toIndex) =>
        toIndex >= fromIndex ? PageShift : -PageShift;

    /// <summary>
    /// TabControl 翻页动效（供 SetUpPanelView / SettingsView / AboutDialog / WorldRulePanel 等复用）：
    /// 新内容从翻页方向横向滑入 10px 并淡入；不移动旧内容，避免两个页面同时占位。
    /// 返回新的「上一次索引」，调用方负责保存以便下次计算方向。
    /// </summary>
    public static int AnimateTabSwitch(
        TabControl tabs,
        int lastIndex,
        bool ready,
        Action<Visual, double>? play = null
    )
    {
        var index = tabs.SelectedIndex;
        var offset = PageEnterOffset(lastIndex, index);
        if (!ready)
            return index;

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (tabs.SelectedIndex != index)
                    return; // 快速连点时只让最后一页播
                var content = tabs.SelectedContent as Visual;
                if (content is null)
                    return;
                if (play is not null)
                    play(content, offset);
                else
                    _ = PlayPageEnter(content, offset);
            },
            Avalonia.Threading.DispatcherPriority.Loaded
        );
        return index;
    }

    /// <summary>淡入 + 上浮（位移只走 RenderTransform，不触发布局）。</summary>
    public static async Task PlayEnter(
        Visual visual,
        double rise = DefaultRise,
        TimeSpan? duration = null
    )
    {
        var cts = Begin(visual);
        var actual = duration ?? Normal;
        try
        {
            var translate = visual.RenderTransform as TranslateTransform;
            if (translate is null && visual.RenderTransform is not null)
            {
                // 已有 scale/rotate 等：不覆盖，退化为淡入
                visual.Opacity = 0;
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            if (translate is null)
            {
                translate = new TranslateTransform();
                visual.RenderTransform = translate;
            }

            visual.Opacity = 0;
            translate.Y = rise;
            await Task.WhenAll(
                RunOpacity(visual, actual, cts.Token),
                RunRise(translate, rise, actual, cts.Token)
            );
        }
        catch (OperationCanceledException)
        {
            // 被同一视觉上的新动画接管：不回落、不覆盖新动画
            return;
        }
        catch (Exception ex)
        {
            // 动效失败绝不能让内容停在透明态
            Debug.WriteLine($"[Motion] PlayEnter 失败: {ex}");
        }
        finally
        {
            if (IsCurrent(visual, cts))
            {
                visual.Opacity = 1;
                if (visual.RenderTransform is TranslateTransform translate)
                    translate.Y = 0;
                Running.Remove(visual);
            }
        }
    }

    /// <summary>页面切换：横向滑入 + 淡入（fromX 为起始偏移，负值从左侧进入）。</summary>
    public static async Task PlayPageEnter(Visual visual, double fromX, TimeSpan? duration = null)
    {
        var cts = Begin(visual);
        var actual = duration ?? Normal;
        try
        {
            var translate = visual.RenderTransform as TranslateTransform;
            if (translate is null && visual.RenderTransform is not null)
            {
                visual.Opacity = 0;
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            if (translate is null)
            {
                translate = new TranslateTransform();
                visual.RenderTransform = translate;
            }

            visual.Opacity = 0;
            translate.X = fromX;
            translate.Y = 0;
            await Task.WhenAll(
                RunOpacity(visual, actual, cts.Token),
                RunSlide(translate, fromX, actual, cts.Token)
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] PlayPageEnter 失败: {ex}");
        }
        finally
        {
            if (IsCurrent(visual, cts))
            {
                visual.Opacity = 1;
                if (visual.RenderTransform is TranslateTransform translate)
                    translate.X = 0;
                Running.Remove(visual);
            }
        }
    }

    /// <summary>仅淡入（对话框/遮罩）。</summary>
    public static async Task FadeIn(Visual visual, TimeSpan? duration = null)
    {
        var cts = Begin(visual);
        var actual = duration ?? Normal;
        try
        {
            visual.Opacity = 0;
            await RunOpacity(visual, actual, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] FadeIn 失败: {ex}");
        }
        finally
        {
            if (IsCurrent(visual, cts))
            {
                visual.Opacity = 1;
                Running.Remove(visual);
            }
        }
    }

    /// <summary>
    /// 退场：淡出 + 可选下沉（位移走 RenderTransform）。
    /// 只用于显式 Close() 的覆盖层——调用方负责在动画结束后真正关闭/隐藏，
    /// 且必须保证失败路径也能关闭（动画异常不得卡住窗口）。
    /// </summary>
    public static async Task PlayExit(Visual visual, double sink = DefaultRise)
    {
        var cts = Begin(visual);
        var actual = Fast;
        try
        {
            var translate = visual.RenderTransform as TranslateTransform;
            if (translate is null && visual.RenderTransform is null)
            {
                translate = new TranslateTransform();
                visual.RenderTransform = translate;
            }

            await RunOpacity(visual, 1, 0, actual, cts.Token);
            if (translate is not null)
                await RunSink(translate, sink, actual, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] PlayExit 失败: {ex}");
        }
        finally
        {
            if (IsCurrent(visual, cts))
                Running.Remove(visual);
        }
    }

    private static CancellationTokenSource Begin(Visual visual)
    {
        if (Running.TryGetValue(visual, out var previous))
            previous.Cancel();

        var cts = new CancellationTokenSource();
        Running.AddOrUpdate(visual, cts);
        return cts;
    }

    private static bool IsCurrent(Visual visual, CancellationTokenSource cts) =>
        Running.TryGetValue(visual, out var current) && ReferenceEquals(current, cts);

    private static Task RunOpacity(Visual visual, TimeSpan duration, CancellationToken token) =>
        RunOpacity(visual, 0, 1, duration, token);

    private static Task RunOpacity(
        Visual visual,
        double from,
        double to,
        TimeSpan duration,
        CancellationToken token
    ) => BuildAnimation(() => Visual.OpacityProperty, from, to, duration).RunAsync(visual, token);

    private static Task RunRise(
        TranslateTransform translate,
        double rise,
        TimeSpan duration,
        CancellationToken token
    ) =>
        BuildAnimation(() => TranslateTransform.YProperty, rise, 0, duration)
            .RunAsync(translate, token);

    /// <summary>下沉（退场用：0 → sink）。</summary>
    private static Task RunSink(
        TranslateTransform translate,
        double sink,
        TimeSpan duration,
        CancellationToken token
    ) =>
        BuildAnimation(() => TranslateTransform.YProperty, 0, sink, duration)
            .RunAsync(translate, token);

    private static Task RunSlide(
        TranslateTransform translate,
        double fromX,
        TimeSpan duration,
        CancellationToken token
    ) =>
        BuildAnimation(() => TranslateTransform.XProperty, fromX, 0, duration)
            .RunAsync(translate, token);

    private static Animation BuildAnimation(
        Func<AvaloniaProperty> property,
        double from,
        double to,
        TimeSpan duration
    ) =>
        new()
        {
            Duration = duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property(), from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property(), to) } },
            },
        };
}
