using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Desktop.ViewModels;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Desktop.Controls;

/// <summary>
/// 自动更新提示弹窗 code-behind —— 只负责 Esc 键路由与弹窗显示时的焦点接管；
/// 全部状态与命令都在共享单例 UpdateState 上（与设置页同源），本类不持有任何状态。
/// </summary>
public partial class UpdatePromptDialog : UserControl
{
    private IDisposable? _visibilitySubscription;

    public UpdatePromptDialog()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => _visibilitySubscription?.Dispose();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        // Esc = 稍后提醒（隧道预览：焦点在根面板时按键先到这里；不取消下载、不清状态）
        (DataContext as UpdateState)
            ?.RemindLaterCommand.Execute()
            .Subscribe();
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _visibilitySubscription?.Dispose();
        if (DataContext is not UpdateState state)
            return;

        // 弹窗可见即聚焦根面板：Esc 的 KeyDown 才会从隧道命中本控件
        // （不强行聚焦输入框；点击弹窗外的主界面后焦点离开，Esc 不再命中——与参考实现同款行为）
        _visibilitySubscription = state
            .WhenAnyValue(x => x.IsPromptVisible)
            .Subscribe(visible =>
            {
                if (visible)
                    RootPanel.Focus();
            });
    }
}
