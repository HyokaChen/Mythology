using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Data;
using Desktop.ViewModels;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 顶部提供商下拉用 SelectedValue 匹配选项对象；打开设置页时必须选中配置里的当前提供商
/// （具名自定义 → 「自定义: 名称」）。回归：换成 SelectedValue 后初始选中的回填不能丢。
/// </summary>
public sealed class SettingsProviderComboTests
{
    [Fact]
    public async Task TopCombo_SelectsConfiguredCustomProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myth-settings-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        await store.SaveAsync(
            new ConfigData
            {
                Providers = new ProviderConfig
                {
                    Default = "custom:krb",
                    DefaultModel = "m",
                    ApiKeys =
                    [
                        new ApiKeyEntry
                        {
                            Provider = "custom",
                            Name = "krb",
                            ApiKey = "k",
                            BaseUrl = "https://b/v1",
                        },
                    ],
                },
            }
        );
        var vm = new SettingsViewModel(store, null, null).Refresh();

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

            var top = window
                .GetVisualDescendants()
                .OfType<ComboBox>()
                .First(c => c.ItemsSource == vm.ProviderOptions);
            var selected = top.SelectedItem as ProviderOption;
            Assert.Equal("自定义: krb", selected?.Display ?? "<null>");
            Assert.Equal("自定义: krb", top.SelectedValue as string);
            window.Close();
        });

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// 回归：在顶部新增并命名自定义卡片时，不能把既有选中卡片（B）的选项对象
    /// 改标成新卡片（A）的名字——那会让下拉显示 A、保存却仍是 B。
    /// </summary>
    [Fact]
    public void AddAndNameNewCustomCard_DoesNotReLabelSelectedOption()
    {
        var vm = new SettingsViewModel();
        vm.ApiKeys.Add(new ApiKeyRow("custom", "B", "sk-b", "https://b/v1"));
        vm.ProviderDisplay = "自定义: B";

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

            var top = window
                .GetVisualDescendants()
                .OfType<ComboBox>()
                .First(c => c.ItemsSource == vm.ProviderOptions);
            var selected = Assert.IsType<ProviderOption>(top.SelectedItem);
            Assert.Equal("自定义: B", selected.Display);

            var add = window
                .GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Content as string == "添加密钥");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var newRow = vm.ApiKeys[0];
            newRow.ProviderDisplay = "自定义";
            newRow.Name = "A";
            Dispatcher.UIThread.RunJobs();

            Assert.Same(selected, top.SelectedItem);
            Assert.Equal("自定义: B", selected.Display);
            Assert.Equal("自定义: B", vm.ProviderDisplay);
            Assert.Contains(
                "自定义: B",
                top.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray()
            );
            window.Close();
        });
    }

    /// <summary>
    /// 删除当前选中的自定义卡片后，顶部提供商不能变空（SelectedValue 会回写 null），
    /// 必须落到仍存在的具名卡片，保证保存时解析出有效 ConfigKey。
    /// </summary>
    [Fact]
    public void RemovingSelectedCustomCard_FallsBackToRemainingNamedProvider()
    {
        var vm = new SettingsViewModel();
        vm.ApiKeys.Add(new ApiKeyRow("custom", "A", "sk-a", "https://a/v1"));
        vm.ApiKeys.Add(new ApiKeyRow("custom", "B", "sk-b", "https://b/v1"));
        vm.ProviderDisplay = "自定义: B";
        Assert.Equal("自定义: B", vm.ProviderDisplay);

        vm.ApiKeys.RemoveAt(1);

        Assert.Equal("自定义: A", vm.ProviderDisplay);
    }
}
