using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
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
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
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
    public void AtToken_TakesPrecedenceOverSlashCommandCompletion()
    {
        RunWithInput(
            "/new @序",
            (_, _) =>
            {
                // 命令不能吞掉 @ token；/new 带参数时 Enter 会拒绝执行（不会丢输入）。
                Assert.True(_vm.IsSuggestionPopupVisible);
                Assert.False(_vm.IsCommandPopupVisible);
                Assert.Contains(_vm.Suggestions, s => s.Name == "序章.txt");
            }
        );
    }

    [Fact]
    public void ManyMessages_AutoScrollsToBottom_OnRestoreAndAfterSend()
    {
        Ui.Run(() =>
        {
            // 真实高度各不相同、含 Markdown 的对话行：虚拟化的 extent 估算会低估总高，
            // ScrollToEnd 停在中间，随后的校正还会把跟随状态误判成“用户上滚”。
            for (var i = 0; i < 120; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(string.Join(" ", Enumerable.Repeat($"消息 {i} 的正文", 20)));
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
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList");
                Assert.NotNull(list);
                var sv = FindMessageScroll(view);

                // 可变高度对话行不用 VSP（extent 估算会振荡）；窗口化 StackPanel 精确贴底。
                Assert.Empty(list.GetVisualDescendants().OfType<VirtualizingStackPanel>());
                Assert.NotEmpty(list.GetVisualDescendants().OfType<StackPanel>());
                Assert.InRange(list.RealizedCount, 1, 80);

                PumpUntil(() => AtBottom(sv), "初始加载自动贴底");

                // 回车发送：用户气泡 + agent 等待行，列表必须继续贴底
                var sent = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User);
                sent.AppendContent("刚发送的一条消息");
                _vm.Messages.Add(sent);
                _vm.Messages.Add(
                    new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
                    {
                        IsWaiting = true,
                    }
                );

                PumpUntil(() => AtBottom(sv), "发消息后自动贴底");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WheelUp_StopsFollow_UntilUserReturnsToBottom()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 80; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(string.Join(" ", Enumerable.Repeat($"历史 {i}", 15)));
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
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                    "MessageList"
                )!;
                var sv = FindMessageScroll(view);
                PumpUntil(() => AtBottom(sv), "初始贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5)); // 上滚一格
                PumpUntil(() => !AtBottom(sv), "用户上滚应停止跟随");

                // 上滚后新增消息不能被强行拉回底部
                _vm.Messages.Add(new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User));
                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }
                Assert.False(AtBottom(sv), "用户上滚后仍被自动拉到底部");

                // 用户滚回底部 → 恢复跟随
                for (var i = 0; i < 10 && !AtBottom(sv); i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -5));
                    for (var j = 0; j < 20; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(10);
                    }
                }
                Assert.True(AtBottom(sv), "滚轮无法回到底部");

                // 恢复后新消息应继续贴底
                _vm.Messages.Add(new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User));
                PumpUntil(() => AtBottom(sv), "回到底部后恢复跟随");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 2000 条消息只保留尾部窗口；窗口内每一行都是 LiveMarkdown，历史不做纯文本降级。
    /// </summary>
    [Fact]
    public void LargeConversation_WindowsAndPinsToBottom()
    {
        Ui.Run(() =>
        {
            void AddHistory(int count, string tag)
            {
                for (var i = 0; i < count; i++)
                {
                    var message = new Desktop.Models.ChatMessage(
                        i % 2 == 0
                            ? Desktop.Models.MessageSender.User
                            : Desktop.Models.MessageSender.Agent
                    );
                    message.AppendContent(
                        $"{tag} {i} " + string.Join(" ", Enumerable.Repeat("正文内容", 20))
                    );
                    _vm.Messages.Add(message);
                }
                _vm.HasMessages = true;
            }

            AddHistory(2000, "历史");

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
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                    "MessageList"
                )!;
                var sv = FindMessageScroll(view);

                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "2000 条窗口贴底");
                Assert.InRange(list.Items.Count, 1, 200);
                Assert.Equal(2000, _vm.Messages.Count);
                Assert.DoesNotContain(
                    list.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.Contains("##") == true
                );

                // 跟随状态下追加：仍贴底，窗口不随全量历史膨胀
                _vm.Messages.Add(new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User));
                PumpUntil(() => AtBottom(sv), "新增消息后贴底");
                Assert.InRange(list.Items.Count, 1, 200);

                // 上滚停跟随，滚回底部恢复
                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "滚轮上滚应停跟随");
                Assert.True(sv.Offset.Y < sv.Extent.Height - sv.Viewport.Height - 1);
                for (var i = 0; i < 20 && !AtBottom(sv); i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -5));
                    for (var j = 0; j < 10; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(10);
                    }
                }
                Assert.True(AtBottom(sv), "滚轮可回到底部");

                // 同一 VM 上 Clear + 批量 Add（会话切换/恢复的真实形态）
                _vm.Messages.Clear();
                AddHistory(2000, "恢复");
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "批量恢复后贴底");
                Assert.InRange(list.Items.Count, 1, 200);

                // 数据上下文卸载/重挂（会话切换的 UI 形态）仍贴底
                view.DataContext = null;
                PumpUntil(() => list.Items.Count == 0, "清空数据上下文");
                view.DataContext = _vm;
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "重挂数据上下文后贴底");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ScrollViewer FindMessageScroll(Desktop.Views.ConversationView view) =>
        view.FindControl<ScrollViewer>("MessageScroll")
        ?? throw new InvalidOperationException("MessageScroll 未找到");

    private static bool AtBottom(ScrollViewer scroll) =>
        scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;

    [Fact]
    public void LiveProcess_AutoCollapsesAfterSettled_UnlessTouched()
    {
        Ui.Run(() =>
        {
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
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                message.AppendContent("最终答复");
                _vm.Messages.Add(message);
                _vm.HasMessages = true;
                PumpUntil(
                    () =>
                        view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                            "MessageList"
                        )!.Items.Count > 0,
                    "新消息行挂载"
                );

                _vm.IsProcessing = true;
                message.AgentSteps.Add(
                    new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.ToolCall,
                        Title = "编辑",
                        Status = Desktop.Models.AgentStepStatus.Completed,
                    }
                );
                for (var i = 0; i < 60; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }
                Assert.True(message.IsProcessExpanded, "回合未结束不得自动折叠过程");

                _vm.IsProcessing = false;
                PumpUntil(() => !message.IsProcessExpanded, "Turn 完成后 450ms 自动折叠过程");

                message.ProcessTouchedByUser = true;
                message.AgentSteps.Add(
                    new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.ToolCall,
                        Title = "搜索",
                        Status = Desktop.Models.AgentStepStatus.Completed,
                    }
                );
                for (var i = 0; i < 60; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }
                Assert.False(message.IsProcessExpanded, "用户手动操作后不得自动折叠");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LiveStream_PreservesMarkdownRenderer_WhenStepsArrive()
    {
        Ui.Run(() =>
        {
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
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                message.AppendContent("最终答复");
                _vm.Messages.Add(message);
                _vm.HasMessages = true;
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                            .Any(),
                    "正文 MarkdownRenderer"
                );
                var renderer = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                    .Single();

                message.AgentSteps.Add(
                    new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.ToolCall,
                        Title = "编辑",
                        Status = Desktop.Models.AgentStepStatus.Completed,
                    }
                );
                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }

                for (var i = 0; i < 12; i++)
                {
                    message.AppendContent($"\n\n流式段落 {i}");
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                Assert.Single(
                    view.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                );
                Assert.Contains(
                    renderer,
                    view.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AllConversationContent_UsesLiveMarkdown()
    {
        Ui.Run(() =>
        {
            var user = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User);
            user.AppendContent("用户可见内容");
            var agent = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            agent.AppendContent("## 历史标题\n\n历史正文");
            _vm.Messages.Add(user);
            _vm.Messages.Add(agent);
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
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(t => t.Text == "用户可见内容"),
                    "用户消息行"
                );
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                            .Any(),
                    "历史正文 LiveMarkdown"
                );
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.Contains("## 历史标题") == true
                );
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
