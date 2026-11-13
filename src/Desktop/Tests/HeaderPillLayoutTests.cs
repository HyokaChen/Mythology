using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 顶部切换胶囊的长标题回归锁（用户报障：标题在胶囊右边缘被硬切，没有省略号）。
///
/// 根因：SukiUI 的 Button 模板把内容包在水平 StackPanel 里——它按无限宽测量子项，
/// TextBlock 的 `TextTrimming` 永远拿不到可用宽度，内容溢出胶囊后由渲染边界裁掉半个字符，
/// 连右侧 chevron 也一起挤出可见区。
/// 修复：标题 TextBlock 自带 MaxWidth（胶囊宽度 − 内边距 − 图标与间距）。
/// 这里断言的是**渲染结果**（TextLayout 行已折叠 = 画出了省略号）与几何关系
/// （短标题不被撑开、标题不压住 chevron、chevron 不越出胶囊内容区），而不是 XAML 文本。
/// </summary>
public sealed class HeaderPillLayoutTests
{
    private const string LongSessionTitle =
        "没有改掉啊！！我看到 章节[ID:a2b8abb1b511]:新学期开始了，后面还有很长很长的内容";
    private const string LongNovelTitle =
        "恋爱吧！折花君：一个非常非常长的书名，用来验证省略号截断是否生效";

    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    [Theory]
    [InlineData("SessionTitleText", LongSessionTitle, true)]
    [InlineData("NovelTitleText", LongNovelTitle, true)]
    [InlineData("SessionTitleText", "新对话", false)]
    public void Title_TrimsWithEllipsisOnlyWhenOverBudget(string name, string text, bool shouldTrim)
    {
        Ui.Run(() =>
        {
            var window = new MainWindow
            {
                WindowState = WindowState.Normal,
                Width = 1280,
                Height = 820,
            };
            try
            {
                window.Show();

                var title = window.FindControl<TextBlock>(name);
                Assert.NotNull(title);
                title!.Text = text;
                window.UpdateLayout();

                Assert.NotNull(title.TextLayout);
                var line = Assert.Single(title.TextLayout!.TextLines);
                var button = title.GetVisualAncestors().OfType<Button>().First();
                var diagnostic =
                    $"{name} text={text} title.width={title.Bounds.Width:F1} "
                    + $"max={title.MaxWidth:F1} layout.width={title.TextLayout.Width:F1} "
                    + $"line.width={line.Width:F1} collapsed={line.HasCollapsed} "
                    + $"button.width={button.Bounds.Width:F1}";

                Assert.True(title.Bounds.Width <= title.MaxWidth + 0.5, diagnostic);
                if (shouldTrim)
                {
                    Assert.True(line.HasCollapsed, diagnostic);
                }
                else
                {
                    Assert.False(line.HasCollapsed, diagnostic);
                    Assert.True(title.Bounds.Width < title.MaxWidth - 0.5, diagnostic);
                }

                var chevron = title.GetVisualParent()!.GetVisualChildren().OfType<Control>().Last();
                Assert.True(
                    title.Bounds.Right <= chevron.Bounds.Left + 0.5,
                    $"{name} 压住了 chevron：title.right={title.Bounds.Right:F1} "
                        + $"chevron.left={chevron.Bounds.Left:F1}"
                );

                var chevronRight = chevron.TranslatePoint(
                    new Point(chevron.Bounds.Width, 0),
                    button
                );
                Assert.NotNull(chevronRight);
                var contentRight = button.Bounds.Width - button.Padding.Right;
                Assert.True(
                    chevronRight!.Value.X <= contentRight + 0.5,
                    $"{name} chevron 侵入右侧内边距：chevron.right={chevronRight.Value.X:F1} "
                        + $"content.right={contentRight:F1}"
                );
            }
            finally
            {
                CloseAndPump(window);
            }
        });
    }

    /// <summary>
    /// 关闭窗口必须等退场动画（150ms）落地：全局 Closing 处理器会先 Cancel 再异步 Close，
    /// 不等就会把主窗口留在共享的 headless UI 线程里，污染后续测试。
    /// </summary>
    private static void CloseAndPump(Window window)
    {
        if (!window.IsVisible)
            return;
        window.Close();
        for (var i = 0; i < 200 && window.IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Assert.False(
            window.IsVisible,
            "MainWindow 未在退场动画后关闭，会污染后续 headless UI 测试"
        );
    }
}
