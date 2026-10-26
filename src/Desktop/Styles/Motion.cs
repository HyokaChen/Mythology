using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Desktop.Styles;

/// <summary>
/// 动效原语（全仓唯一实现点）：
/// Enter         — 附加即可见 / IsVisible false→true 时播一次「淡入 + 上浮」
/// PopupEnter    — Popup 每次 Opened 时对 Child 播一次（可用 PopupEnterRise 定方向）
/// Stagger       — 列表容器附加：项按索引错峰淡入（只播一次，复用容器不重播）
/// TabSwitch     — TabControl 附加：选中项按翻页方向滑入（替代 5 处手写接线）
/// PlayEnter / PlayPageEnter / FadeIn — 复用同一视觉树、只能显式调用的场合
/// PlayExit      — 淡出 + 下沉；仅用于显式 Close() 的覆盖层，调用方负责关闭
///
/// 时序按 DESIGN.md 的印刷基调：淡入 + ≤16px 位移 / ≤24px 翻页滑移，不用回弹/过冲；
/// 曲线统一 Fluent 的 Fast-Out（起手快、尾段长）。位移优先走渲染线程（Translation），
/// UI 线程只保留淡入——详见 TryRunTranslation 的通道说明。
/// 每个视觉同一时刻只允许一段动画：重复触发会取消上一段，避免两个时钟抢同一属性。
/// </summary>
public class Motion
{
    /// <summary>按下 / 悬停等即时反馈。</summary>
    public static readonly TimeSpan Micro = TimeSpan.FromMilliseconds(90);

    /// <summary>浮层、行内小元素、hover 薄雾入场。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);

    /// <summary>窗口 / 翻页 / 对话框入场（Fluent 标准档）。</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(250);

    /// <summary>数值增长（进度条）。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// 入场 / 退场 / 翻页统一曲线：Fluent 的 Fast-Out（0.1, 0.9, 0.2, 1）。
    /// 起手快、尾段长——读起来「干脆而不生硬」，是 Office 质感的主要来源；
    /// 原来的 CubicEaseOut 中段太匀，短位移下几乎读不出加速/收尾。
    /// </summary>
    public static readonly Easing MotionEasing = new SplineEasing(new KeySpline(0.1, 0.9, 0.2, 1));

    /// <summary>消息行 / 窗口内容默认上浮距离。</summary>
    public const double DefaultRise = 16;

    /// <summary>浮层默认上浮距离（提示框在输入框上方，从下往上落位）。</summary>
    public const double PopupRise = 8;

    /// <summary>页面切换的横向位移。</summary>
    public const double PageShift = 24;

    /// <summary>行内小元素（chip / 待办项 / 来源）的上浮距离。</summary>
    public const double SmallRise = 8;

    /// <summary>逐项入场的错峰步长（毫秒）。</summary>
    public const int StaggerStepMs = 12;

    /// <summary>只给前 N 项排错峰延迟；再往后一次性淡入，避免长列表首屏被拖成慢动作。</summary>
    public const int MaxStaggerItems = 12;

    /// <summary>已播放过入场动画的项容器（避免复用容器时重播）。</summary>
    private static readonly ConditionalWeakTable<Control, object?> Played = new();

    /// <summary>正在播放的动画（取消上一段，防止属性竞争）。</summary>
    private static readonly ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();

    /// <summary>附加即刻播一次入场（见 PlayEnter）。</summary>
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

    /// <summary>
    /// TabControl 翻页：选中项从翻页方向横向滑入 <see cref="PageShift"/> 并淡入。
    /// 内容两种形态都覆盖：SelectedContent 本身就是 Visual（TabItem 直接装内容）时直接播；
    /// 否则（ContentTemplate 数据模板）解析到承载它的 ContentPresenter.Child。
    /// </summary>
    public static readonly AttachedProperty<bool> TabSwitchProperty =
        AvaloniaProperty.RegisterAttached<Motion, TabControl, bool>("TabSwitch");

    public static bool GetTabSwitch(TabControl tabs) => tabs.GetValue(TabSwitchProperty);

    public static void SetTabSwitch(TabControl tabs, bool value) =>
        tabs.SetValue(TabSwitchProperty, value);

    /// <summary>窗口动效已接线的标记（防重复订阅 Closing）。</summary>
    private static readonly AttachedProperty<bool> WindowMotionAttachedProperty =
        AvaloniaProperty.RegisterAttached<Motion, Window, bool>("WindowMotionAttached");

    /// <summary>
    /// 窗口级动效接线（App 的 WindowOpened 类处理器统一调用；测试可直接调用，幂等）：
    /// 入场（内容未自挂 Enter 时，延后到首次 Opened 播放）+ 退场
    /// （首次 Closing 取消 → 淡出下沉 → 真正关闭）。
    /// </summary>
    public static void AttachWindowMotion(Window window)
    {
        if (window.GetValue(WindowMotionAttachedProperty))
            return;
        window.SetValue(WindowMotionAttachedProperty, true);

        if (window.Content is Visual content && !GetEnter(content))
        {
            // 入场必须等 Content 真正入树：WindowOpened 事件早于 IsVisible=true 与挂载，
            // 那一刻合成器拿不到 CompositionVisual（位移只能退化成 UI 线程），
            // 动画也跑在未挂载的视觉上。Opened 在其后，且仍在首帧渲染前，不会闪。
            if (window.IsVisible)
                _ = PlayEnter(content);
            else
                window.Opened += (_, _) =>
                {
                    // 显示期间已自行接过动画（或已播过）就不再重播
                    if (!GetEnter(content) && !Running.TryGetValue(content, out _))
                        _ = PlayEnter(content);
                };
        }

        var exiting = false;
        window.Closing += async (_, e) =>
        {
            // 关机 / 退应用 / 随宿主关闭不播退场：取消会让父窗关不掉或拖慢退出
            if (
                exiting
                || e.CloseReason
                    is WindowCloseReason.ApplicationShutdown
                        or WindowCloseReason.OSShutdown
                        or WindowCloseReason.OwnerWindowClosing
            )
                return;
            e.Cancel = true;
            exiting = true;
            try
            {
                if (window.Content is Visual c)
                    await PlayExit(c);
            }
            finally
            {
                // 动画失败也必须关得掉（PlayExit 内部已吞异常，finally 兜底）
                window.Close();
            }
        };
    }

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

        TabSwitchProperty.Changed.AddClassHandler<TabControl>(
            static (tabs, e) =>
            {
                if (e.NewValue is not true)
                    return;
                var state = new TabSwitchState(tabs.SelectedIndex)
                {
                    // 属性可能在已加载后才挂上（运行时赋值）：不能只等 Loaded 事件
                    Ready = tabs.IsLoaded,
                };
                tabs.Loaded += (_, _) => state.Ready = true;
                tabs.SelectionChanged += (_, _) =>
                {
                    var index = tabs.SelectedIndex;
                    var offset = PageEnterOffset(state.LastIndex, index);
                    state.LastIndex = index;
                    if (!state.Ready)
                        return;

                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () =>
                        {
                            if (tabs.SelectedIndex != index)
                                return; // 快速连点时只让最后一页播
                            if (ResolveTabContent(tabs) is { } content)
                                _ = PlayPageEnter(content, offset);
                        },
                        Avalonia.Threading.DispatcherPriority.Loaded
                    );
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

    /// <summary>翻页方向：前进从右侧进入（起点 +<see cref="PageShift"/>），后退从左侧进入（−PageShift）。</summary>
    public static double PageEnterOffset(int fromIndex, int toIndex) =>
        toIndex >= fromIndex ? PageShift : -PageShift;

    /// <summary>
    /// 合成器位移开关。无头测试置 false 可确定性地走 UI 线程 RenderTransform 分支
    /// （Desktop.Tests 经 InternalsVisibleTo 访问）。
    /// </summary>
    internal static bool CompositionMotionEnabled { get; set; } = true;

    /// <summary>
    /// 渲染线程位移：把 [from, to] 交给合成器播在 CompositionVisual.Translation 上。
    ///
    /// 为什么必须是 Translation：Avalonia 布局只写 Offset / Size / Opacity / TransformMatrix
    /// （Visual.SynchronizeCompositionProperties），从不写 Translation——它是唯一不会被
    /// UI 线程每帧覆写的运动通道（SmoothScroll.Avalonia 的惯性滚动同此原理）。
    /// 淡入不能走合成器：同一方法每次 visual 变脏都会执行 `Opacity = (float)Opacity`，
    /// 会把合成器动画打回 UI 线程值。
    ///
    /// 返回 false = 合成器不可用（无头 UseHeadlessDrawing / 未入树）或开关关闭
    /// → 调用方回退 UI 线程 RenderTransform。任何异常都吞掉并回退，最坏即原行为。
    /// </summary>
    internal static bool TryRunTranslation(
        Visual visual,
        Vector3D from,
        Vector3D to,
        TimeSpan duration
    )
    {
        if (!CompositionMotionEnabled)
            return false;
        try
        {
            var cv = ElementComposition.GetElementVisual(visual);
            if (cv is null)
                return false;

            var animation = cv.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Duration = duration;
            animation.InsertKeyFrame(0f, from, MotionEasing);
            animation.InsertKeyFrame(1f, to, MotionEasing);
            cv.StartAnimation("Translation", animation);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] 合成器位移不可用，回退 UI 线程: {ex}");
            return false;
        }
    }

    /// <summary>淡入 + 上浮（位移优先走渲染线程，回退 RenderTransform）。</summary>
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
            visual.Opacity = 0;

            // 位移优先走渲染线程；只剩淡入在 UI 线程（合成器 Opacity 会被布局覆写）
            if (TryRunTranslation(visual, new Vector3D(0, rise, 0), default, actual))
            {
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            var translate = visual.RenderTransform as TranslateTransform;
            if (translate is null && visual.RenderTransform is not null)
            {
                // 已有 scale/rotate 等：不覆盖，退化为淡入
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            translate ??= new TranslateTransform();
            visual.RenderTransform = translate;
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
            visual.Opacity = 0;

            if (TryRunTranslation(visual, new Vector3D(fromX, 0, 0), default, actual))
            {
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            var translate = visual.RenderTransform as TranslateTransform;
            if (translate is null && visual.RenderTransform is not null)
            {
                await RunOpacity(visual, actual, cts.Token);
                return;
            }

            translate ??= new TranslateTransform();
            visual.RenderTransform = translate;
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
    /// 退场：淡出 + 下沉（位移优先走渲染线程）。
    /// 只用于显式 Close() 的覆盖层——调用方负责在动画结束后真正关闭/隐藏，
    /// 且必须保证失败路径也能关闭（动画异常不得卡住窗口）。
    /// </summary>
    public static async Task PlayExit(Visual visual, double sink = DefaultRise)
    {
        var cts = Begin(visual);
        var actual = Fast;
        try
        {
            TryRunTranslation(visual, default, new Vector3D(0, sink, 0), actual);
            await RunOpacity(visual, 1, 0, actual, cts.Token);
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

        // Translation 不被布局覆写，不显式停掉会残留上一段的半程值
        // （例如同一视觉先合成器入场、又因开关翻转改走 UI 线程回退）。
        try
        {
            ElementComposition.GetElementVisual(visual)?.StopAnimation("Translation");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Motion] 停止上一段合成器位移失败: {ex}");
        }

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
            Easing = MotionEasing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property(), from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property(), to) } },
            },
        };

    /// <summary>单个 TabControl 的翻页状态（上次索引 + 是否已 Loaded）。</summary>
    private sealed class TabSwitchState(int lastIndex)
    {
        public int LastIndex = lastIndex;

        public bool Ready;
    }

    /// <summary>
    /// TabControl 当前选中内容的视觉：直接内容取 SelectedContent；
    /// 数据模板（ContentTemplate）时 SelectedContent 是 VM 对象，须解析到承载它的 ContentPresenter.Child。
    /// 必须排除 TabItem 内部的 Header 呈现器——它的 Content 也是同一个数据对象
    /// （SelectedContent == SelectedItem），命中它会把翻页动画播到标签文字上。
    /// </summary>
    internal static Visual? ResolveTabContent(TabControl tabs)
    {
        if (tabs.SelectedContent is Visual direct)
            return direct;
        return tabs.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(p =>
                ReferenceEquals(p.Content, tabs.SelectedContent)
                && p.FindAncestorOfType<TabItem>() is null
            )
            ?.Child;
    }
}
