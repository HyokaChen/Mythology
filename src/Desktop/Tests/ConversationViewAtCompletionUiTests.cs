using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.Services;
using Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 输入区 @ 文件路径补全的真实 UI 冒烟（无头 Avalonia）：
/// 真实 ConversationView + 真实 TextBox 文本变更 → RefreshSuggestions → VM 文件补全 →
/// SuggestionPopup 打开 → Tab 应用候选（目录继续导航 / 文件插入）。
///
/// 覆盖的是「View 接线」这一层：解析器与 VM 已由 InputCompletionParserTests /
/// ConversationVmContractTests 覆盖，本类只钉住「文字变化真的驱动弹窗」与「选中真的写回输入框」。
/// </summary>
public sealed class ConversationViewAtCompletionUiTests : IDisposable
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-at-ui-{Guid.NewGuid():N}"
    );

    private readonly string _novelsRoot;
    private readonly ConversationViewModel _vm;

    public ConversationViewAtCompletionUiTests()
    {
        _novelsRoot = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        Directory.CreateDirectory(_novelsRoot);
        Directory.CreateDirectory(storeDir);
        Directory.CreateDirectory(Path.Combine(_novelsRoot, "卷一 风起"));
        File.WriteAllText(Path.Combine(_novelsRoot, "序章.txt"), "内容");
        File.WriteAllText(Path.Combine(_novelsRoot, "卷一 风起", "细纲.md"), "内容");

        var registry = new ToolRegistry();
        var agent = new Agent(registry, null);
        var novelStore = new FileNovelStore(_novelsRoot, storeDir);
        var engine = new SessionEngine(
            new DesktopAgentBridge(agent),
            new FileSessionStore(storeDir, _novelsRoot),
            novelStore,
            new TodoStore(),
            new ContextTokenCalibrator(),
            new ConfigStore(storeDir),
            NullLogger.Instance
        );
        _vm = new ConversationViewModel(
            new DesktopAgentBridge(agent),
            engine,
            novelStore,
            tools: registry,
            novelsRoot: _novelsRoot
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    /// <summary>等 VM 侧的异步文件补全落地（Task.Run IO + 版本守卫）。</summary>
    private static void PumpUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            if (condition())
                return;
        }
        Assert.Fail($"等待超时：{what}");
    }

    /// <summary>在真实视图里把输入框文本设成 <paramref name="text"/> 并等补全弹窗状态就绪。</summary>
    private void RunWithInput(string text, Action<Desktop.Views.ConversationView, TextBox> assert)
    {
        Ui.Run(() =>
        {
            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var input = view.FindControl<TextBox>("InputBox");
            Assert.NotNull(input);

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                input!.Text = text;
                input.CaretIndex = text.Length;
                PumpUntil(
                    () => !_vm.IsSuggestionPopupVisible || _vm.Suggestions.Count > 0,
                    "补全建议刷新"
                );

                assert(view, input);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BareAt_DrivesPopup_WithProjectRootEntries()
    {
        RunWithInput(
            "@",
            (view, _) =>
            {
                Assert.True(_vm.IsSuggestionPopupVisible, "@ 未打开补全弹窗");

                var popup = view.FindControl<Popup>("SuggestionPopup");
                Assert.NotNull(popup);
                Assert.True(popup!.IsOpen, "SuggestionPopup.IsOpen 未跟随 VM");

                Assert.Contains(_vm.Suggestions, s => s.Name == "序章.txt");
                Assert.Contains(_vm.Suggestions, s => s.Name == "卷一 风起/");
                // 右列展示真实绝对路径（不是把条目名拼到项目根就完事）
                var dirEntry = _vm.Suggestions.Single(s => s.Name == "卷一 风起/");
                Assert.Equal(Path.Combine(_novelsRoot, "卷一 风起"), dirEntry.FullPath);
            }
        );
    }

    [Fact]
    public void Tab_AppliesDirectoryCandidate_AndCascadesNextLevel()
    {
        RunWithInput(
            "@卷",
            (view, input) =>
            {
                Assert.True(_vm.IsSuggestionPopupVisible, "目录补全未开弹窗");
                _vm.SelectedSuggestionIndex = 0;

                // 键盘 Tab 经真实路由处理器应用候选
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab }
                );
                PumpUntil(() => input.Text == "@卷一 风起/", "Tab 应用目录候选");

                // 目录应用后自动展开下一级（细纲.md）
                PumpUntil(() => _vm.Suggestions.Any(s => s.Name == "细纲.md"), "下一级补全");
                Assert.True(_vm.IsSuggestionPopupVisible, "目录应用后未级联下一级弹窗");
            }
        );
    }

    [Fact]
    public void NonPathText_NoEntityFallback_PopupStaysClosed()
    {
        RunWithInput(
            "@张三",
            (view, _) =>
            {
                // 语义契约：@ 不再回退实体提及（引用实体请用 #）
                Assert.False(_vm.IsSuggestionPopupVisible);
                Assert.Empty(_vm.Suggestions);
                var popup = view.FindControl<Popup>("SuggestionPopup");
                Assert.False(popup!.IsOpen, "无候选时弹窗必须关闭");
            }
        );
    }

    [Fact]
    public void Hash_StillDrivesEntityRefPopup()
    {
        RunWithInput(
            "#",
            (view, _) =>
            {
                Assert.True(_vm.IsSuggestionPopupVisible, "# 未打开实体引用弹窗");
                Assert.Equal(
                    Desktop.Controls.EntityRefParser.DisplayTypes,
                    _vm.Suggestions.Select(s => s.Name).ToArray()
                );
                var popup = view.FindControl<Popup>("SuggestionPopup");
                Assert.True(popup!.IsOpen);
            }
        );
    }

    [Fact]
    public void Hash_PopupStartsAtFirstSuggestion_AndArrowKeysDoNotMoveCaret()
    {
        RunWithInput(
            "第一行\n第二行 #",
            (view, input) =>
            {
                input.Focus();
                Assert.True(_vm.IsSuggestionPopupVisible, "# 未打开实体引用弹窗");

                var list = view.FindControl<ListBox>("SuggestionList");
                Assert.NotNull(list);
                Assert.Equal(0, _vm.SelectedSuggestionIndex);
                Assert.Equal(0, list!.SelectedIndex);

                var caret = input.CaretIndex;
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
                );
                // 多行输入里 Up 必须先被弹窗消费，不能把光标移到上一行
                Assert.Equal(caret, input.CaretIndex);
                Assert.Equal(0, _vm.SelectedSuggestionIndex);

                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(caret, input.CaretIndex);
                Assert.Equal(1, _vm.SelectedSuggestionIndex);
            }
        );
    }

    [Fact]
    public void ManyMessages_UseVirtualizingPanel_NotAllContainersRealized()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent($"消息 {i}");
                _vm.Messages.Add(message);
            }
            _vm.HasMessages = true;

            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList");
                Assert.NotNull(list);
                var realized = list!.GetVisualDescendants().OfType<ContentPresenter>().Count();
                Assert.NotEmpty(list.GetVisualDescendants().OfType<VirtualizingStackPanel>());
                Assert.InRange(realized, 1, 40); // 200 条消息只能实例化视口附近
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Slash_PopupStartsAtFirstCommand_AndArrowKeysNavigate()
    {
        RunWithInput(
            "/",
            (view, input) =>
            {
                input.Focus();
                PumpUntil(
                    () => _vm.IsCommandPopupVisible && _vm.CommandSuggestions.Count > 0,
                    "斜杠命令弹窗"
                );

                var list = view.FindControl<ListBox>("CommandList");
                Assert.NotNull(list);
                Assert.Equal(0, _vm.SelectedCommandIndex);
                Assert.Equal(0, list!.SelectedIndex);

                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(1, _vm.SelectedCommandIndex);

                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
                );
                Assert.Equal(0, _vm.SelectedCommandIndex);
            }
        );
    }
}
