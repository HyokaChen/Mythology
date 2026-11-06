using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// Adaline 输入框的竖直对齐契约（用户报障：搜索框文字不居中）。
/// 单行 → 居中；多行（AcceptsReturn）→ 顶对齐，否则拉伸后的正文框会把第一行竖直居中。
/// 用真实 App 样式 + 真实模板计算，不解析源码文本。
/// </summary>
public sealed class AdalineInputAlignmentTests
{
    [Fact]
    public void SingleLine_Inputs_Are_Vertically_Centered()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var plain = new TextBox { Classes = { "Adaline" } };
            var search = new TextBox { Classes = { "Adaline", "SearchField" } };
            var host = new StackPanel { Spacing = 8, Children = { plain, search } };
            var window = new Window { Content = host, Width = 320, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, plain.VerticalContentAlignment);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, search.VerticalContentAlignment);
        });
    }

    [Fact]
    public void Multiline_Inputs_Stay_Top_Aligned()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var box = new TextBox
            {
                Classes = { "Adaline" },
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
            };
            var window = new Window { Content = box, Width = 320, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, box.VerticalContentAlignment);
        });
    }
}
