using System.Collections.Frozen;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Desktop.ViewModels;
using Desktop.Views;

namespace Desktop;

/// <summary>
/// View 定位器：FrozenDictionary 硬编码 VM→View 映射（AOT 安全，无反射）。
/// </summary>
public class ViewLocator : IDataTemplate
{
    private static readonly FrozenDictionary<string, Func<Control>> ViewFactories = new Dictionary<
        string,
        Func<Control>
    >
    {
        [nameof(MainWindowViewModel)] = () => new MainWindow(),
        [nameof(ConversationViewModel)] = () => new ConversationView(),
        [nameof(SetUpPanelViewModel)] = () => new SetUpPanelView(),
        [nameof(SettingsViewModel)] = () => new SettingsView(),
    }.ToFrozenDictionary();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        return ViewFactories.TryGetValue(param.GetType().Name, out var factory)
            ? factory()
            : new TextBlock { Text = "Not Found: " + param.GetType().Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
