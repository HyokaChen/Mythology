using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.ViewModels;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 「添加密钥」必须把新卡片插到列表顶部（按钮就在列表上方）。
/// 回归：追加到末尾时，已有若干连接器后新卡片会落在视口外，用户得先滚到底才能填写。
/// </summary>
public sealed class SettingsApiKeyListTests
{
    [Fact]
    public void AddApiKey_InsertsEditingCardAtTopOfList()
    {
        var vm = new SettingsViewModel();
        vm.ApiKeys.Add(new ApiKeyRow("custom", "agnes", "sk-old", "https://old/v1"));

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new SettingsWindow
            {
                DataContext = vm,
                Width = 1184,
                Height = 800,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var add = window
                .GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Content as string == "添加密钥");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, vm.ApiKeys.Count);
            Assert.True(vm.ApiKeys[0].IsEditing);
            Assert.Equal("openai", vm.ApiKeys[0].Provider);
            Assert.Equal("agnes", vm.ApiKeys[1].Name);

            var list = window
                .GetVisualDescendants()
                .OfType<ItemsControl>()
                .First(c => c.ItemsSource == vm.ApiKeys);
            Assert.Same(vm.ApiKeys[0], (list.ContainerFromIndex(0) as Control)?.DataContext);
            window.Close();
        });
    }
}
