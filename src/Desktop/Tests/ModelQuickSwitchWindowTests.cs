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

    [Fact]
    public void ModelsArrivingAfterFilter_RefreshesListAndSelection()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ModelQuickSwitchWindow("model-6", ["model-6"], _ => { });
            window.SetLoading();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var list = window.FindControl<ListBox>("ModelList")!;
            var note = window.FindControl<TextBlock>("StatusNote")!;
            var search = window.FindControl<TextBox>("SearchInput")!;
            Assert.True(note.IsVisible);
            Assert.Equal(1, list.ItemCount);

            // 用户可能在“正在获取…”期间就输入搜索词；发现结果回来必须沿用该过滤条件。
            search.Text = "model-9";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, list.ItemCount);

            window.SetModels(Enumerable.Range(1, 10).Select(i => $"model-{i}").ToArray());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, list.ItemCount);
            Assert.Equal("model-9", Assert.IsType<QuickModelItem>(list.SelectedItem).Name);
            Assert.False(note.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void ErrorAfterLoading_ShowsNoteAndKeepsCurrentList()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ModelQuickSwitchWindow("model-6", ["model-6"], _ => { });
            window.SetLoading();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.SetError("未获取到模型列表");

            var note = window.FindControl<TextBlock>("StatusNote")!;
            var list = window.FindControl<ListBox>("ModelList")!;
            Assert.True(note.IsVisible);
            Assert.Equal("未获取到模型列表", note.Text);
            Assert.Equal("model-6", Assert.IsType<QuickModelItem>(list.SelectedItem).Name);
            window.Close();
        });
    }
}
