using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 模型快速切换窗：搜索过滤后列表变短，ListBox 的旧选中索引不能继续引用新集合。
/// 旧实现复用同一个 List 且不通知集合变化，输入搜索会触发 Avalonia 选中项越界崩溃。
/// </summary>
public sealed class ModelQuickSwitchWindowTests
{
    [Fact]
    public void FilterShrinkingList_ResetsSelectionWithoutCrash()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var applied = new List<string>();
            var models = Enumerable.Range(1, 10).Select(i => $"model-{i}").ToArray();
            var window = new ModelQuickSwitchWindow("model-6", models, applied.Add);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var list = window.FindControl<ListBox>("ModelList")!;
            Assert.Equal("model-6", Assert.IsType<QuickModelItem>(list.SelectedItem).Name);

            var search = window.FindControl<TextBox>("SearchInput")!;
            search.Text = "model-9";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, list.ItemCount);
            var selected = Assert.IsType<QuickModelItem>(list.SelectedItem);
            Assert.Equal("model-9", selected.Name);
            Assert.Empty(applied);
            search.Text = "no-match";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, list.ItemCount);
            Assert.Null(list.SelectedItem);

            search.Text = "";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(10, list.ItemCount);
            Assert.Equal("model-6", Assert.IsType<QuickModelItem>(list.SelectedItem).Name);
            Assert.Empty(applied);
            window.Close();
        });
    }

    [Fact]
    public void EnterAfterFilter_AppliesSelectedModel()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var applied = new List<string>();
            var models = Enumerable.Range(1, 10).Select(i => $"model-{i}").ToArray();
            var window = new ModelQuickSwitchWindow("model-6", models, applied.Add);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var search = window.FindControl<TextBox>("SearchInput")!;
            search.Text = "model-9";
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["model-9"], applied);
        });
    }
}
