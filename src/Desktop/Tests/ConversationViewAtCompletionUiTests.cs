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
using Core.Stability;
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
    private void RunWithInput(
        string text,
        Action<Desktop.Views.ConversationView, TextBox> assert,
        Func<bool>? ready = null
    )
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
                    ready ?? (() => !_vm.IsSuggestionPopupVisible || _vm.Suggestions.Count > 0),
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
            },
            () => _vm.Suggestions.Count > 0
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
            },
            () => _vm.Suggestions.Count > 0
        );
    }

    [Fact]
    public void NonPathText_NoEntityFallback_PopupStaysClosed()
    {
        RunWithInput("@张三", (view, _) =>
        {
            // 语义契约：@ 不再回退实体提及（引用实体请用 #）
            Assert.False(_vm.IsSuggestionPopupVisible);
            Assert.Empty(_vm.Suggestions);
            var popup = view.FindControl<Popup>("SuggestionPopup");
            Assert.False(popup!.IsOpen, "无候选时弹窗必须关闭");
        });
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
            },
            () => _vm.Suggestions.Count > 0
        );
    }

    [Fact]
    public void Hash_PopupWrapsSelection_AndArrowKeysDoNotMoveCaret()
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
                var last = _vm.Suggestions.Count - 1;
                Assert.True(last > 0, "需要至少两个候选才能验证首尾循环");

                // 首项再向上 → 末项
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
                );
                // 多行输入里 Up 必须先被弹窗消费，不能把光标移到上一行
                Assert.Equal(caret, input.CaretIndex);
                Assert.Equal(last, _vm.SelectedSuggestionIndex);
                Assert.Equal(last, list.SelectedIndex);

                // 末项再向下 → 首项
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(caret, input.CaretIndex);
                Assert.Equal(0, _vm.SelectedSuggestionIndex);
                Assert.Equal(0, list.SelectedIndex);

                // 首项向下 → 第二项（普通前进仍正常）
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(caret, input.CaretIndex);
                Assert.Equal(1, _vm.SelectedSuggestionIndex);
            },
            () => _vm.Suggestions.Count > 0
        );
    }

    [Fact]
    public void At_PopupWrapsSelection_AtBothEnds()
    {
        RunWithInput(
            "@",
            (view, input) =>
            {
                input.Focus();
                Assert.True(_vm.IsSuggestionPopupVisible, "@ 未打开补全弹窗");

                var list = view.FindControl<ListBox>("SuggestionList");
                Assert.NotNull(list);
                Assert.Equal(0, _vm.SelectedSuggestionIndex);

                var last = _vm.Suggestions.Count - 1;
                Assert.True(last > 0, "需要至少两个候选才能验证首尾循环");

                // 列表重建瞬间索引可能为 -1：Down 应选首项而不是跳过
                _vm.SelectedSuggestionIndex = -1;
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(0, _vm.SelectedSuggestionIndex);
                Assert.Equal(0, list.SelectedIndex);

                // 首项再向上 → 末项
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
                );
                Assert.Equal(last, _vm.SelectedSuggestionIndex);
                Assert.Equal(last, list!.SelectedIndex);

                // 末项再向下 → 首项
                input.RaiseEvent(
                    new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
                );
                Assert.Equal(0, _vm.SelectedSuggestionIndex);
                Assert.Equal(0, list.SelectedIndex);
            },
            () => _vm.Suggestions.Count > 1
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
            },
            () => _vm.Suggestions.Any(s => s.Name == "序章.txt")
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
                Assert.True(
                    list.TopSpacerHeight > 0,
                    $"尾部窗口应有顶部 spacer：top={list.TopSpacerHeight}, items={list.Items.Count}, extent={sv.Extent.Height}"
                );
                Assert.True(
                    sv.Extent.Height > 10_000,
                    $"滚动条应覆盖全量历史：extent={sv.Extent.Height}, viewport={sv.Viewport.Height}"
                );
                Assert.Equal(2000, _vm.Messages.Count);
                Assert.DoesNotContain(list.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("##") == true
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

                // 全量历史可达：程序化归零后窗口应换到最早一段，顶部 spacer 归零。
                sv.Offset = sv.Offset.WithY(0);
                PumpUntil(
                    () => list.TopSpacerHeight <= 0.5 && list.Items.Count > 0,
                    "滚到顶部应实例化最早的行"
                );
                Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("历史 0") == true
                );
                Assert.DoesNotContain(list.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("##") == true
                );

                sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                // 到底部后再向下滚：这是用户明确回到底部的意图。
                for (var i = 0; i < 20 && !list.IsFollowing; i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -5));
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }
                PumpUntil(
                    () => AtBottom(sv) && list.BottomSpacerHeight <= 0.5,
                    "滚回底部应换回尾部窗口"
                );

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

    /// <summary>
    /// 滚动到历史中段后，实测高度回写不能改变当前 offset（anchorRow/anchorDelta）。
    /// </summary>
    [Fact]
    public void VirtualizedScroll_MiddleAnchor_StableAfterMeasure()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 8))
                );
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");

                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.5));
                PumpUntil(
                    () => list.TopSpacerHeight > 0 && list.BottomSpacerHeight > 0,
                    "中段窗口"
                );
                var before = sv.Offset.Y;
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                Assert.InRange(sv.Offset.Y, before - 3, before + 3);
                Assert.True(list.TopSpacerHeight > 0);
                Assert.True(list.BottomSpacerHeight > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 历史中段小幅下滚只能移动视口，不能因为窗口换页/高度校正把“接近底部”
    /// 误判成用户已回到底部，从而瞬间跳到最新消息。
    /// </summary>
    [Fact]
    public void VirtualizedScroll_WheelDownMidHistory_DoesNotJumpToTail()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 8))
                );
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");

                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.5));
                PumpUntil(
                    () => list.TopSpacerHeight > 0.5 && list.BottomSpacerHeight > 0.5,
                    "中段窗口"
                );

                for (var i = 0; i < 6; i++)
                {
                    var before = sv.Offset.Y;
                    window.MouseWheel(pointer, new Vector(0, -2));
                    for (var j = 0; j < 10; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }

                    Assert.False(list.IsFollowing, $"第 {i} 次小幅下滚后不应恢复跟随");
                    Assert.True(
                        sv.Offset.Y < Math.Max(0, sv.Extent.Height - sv.Viewport.Height) - 1,
                        $"第 {i} 次小幅下滚后不应跳到末尾：before={before}, offset={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ProcessExpand_ThenScrollDown_DoesNotGetPulledUp()
    {
        Ui.Run(() =>
        {
            for (var m = 0; m < 60; m++)
            {
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                for (var s = 0; s < 8; s++)
                {
                    if (s % 2 == 0)
                    {
                        var step = new Desktop.Models.AgentStep
                        {
                            Kind = Desktop.Models.StepKind.Thinking,
                            Status = Desktop.Models.AgentStepStatus.Completed,
                            Title = $"思考 {m}-{s}",
                        };
                        step.ThinkingBuilder.Append(
                            string.Join(" ", Enumerable.Repeat("展开后的详细过程正文。", 30))
                        );
                        step.NotifyThinkingChanged();
                        message.AgentSteps.Add(step);
                    }
                    else
                    {
                        message.AgentSteps.Add(
                            new Desktop.Models.AgentStep
                            {
                                Kind = Desktop.Models.StepKind.ToolCall,
                                Title = $"工具 {m}-{s}",
                                Status = Desktop.Models.AgentStepStatus.Completed,
                                ArgumentsSummary = $"参数 {m}-{s}",
                                ResultSummary = $"结果 {m}-{s}",
                            }
                        );
                    }
                }
                message.IsProcessExpanded = false;
                _vm.Messages.Add(message);
            }
            _vm.HasMessages = true;

            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                Content = view,
            };
            window.Show();
            try
            {
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                    "MessageList"
                )!;
                var sv = FindMessageScroll(view);
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始过程折叠");

                var pointer = new Point(500, 380);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");
                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.45));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                var button = view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b =>
                        b.DataContext
                            is Desktop.Models.ConversationRow
                            {
                                Kind: Desktop.Models.ConversationRowKind.Process
                            }
                    )
                    .Select(b => new { Button = b, Point = b.TranslatePoint(new Point(0, 0), sv) })
                    .Where(x => x.Point is { } p && p.Y >= 0 && p.Y < sv.Viewport.Height)
                    .OrderBy(x => x.Point!.Value.Y)
                    .FirstOrDefault();
                Assert.NotNull(button);
                var processRow = (Desktop.Models.ConversationRow)button!.Button.DataContext!;
                var processMessage = processRow.Message;
                var click = button.Button.TranslatePoint(
                    new Point(button.Button.Bounds.Width / 2, button.Button.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(click);
                window.MouseMove(click!.Value);
                window.MouseDown(click.Value, MouseButton.Left);
                window.MouseUp(click.Value, MouseButton.Left);
                for (var i = 0; i < 40; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }
                Assert.True(processMessage.IsProcessExpanded);
                Assert.False(list.IsFollowing);

                // 再展开当前可见的思考详情：详情内部是 320px 限高滚动区，
                // 外层行高变化最容易把 anchor 拉回上方。
                var detailsButton = view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b =>
                        b.DataContext
                            is Desktop.Models.ConversationRow
                            {
                                Step: { Kind: Desktop.Models.StepKind.Thinking },
                            }
                        && Equals(b.Content, "展开")
                    )
                    .Select(b => new { Button = b, Point = b.TranslatePoint(new Point(0, 0), sv) })
                    .Where(x => x.Point is { } p && p.Y >= 0 && p.Y < sv.Viewport.Height)
                    .FirstOrDefault();
                if (detailsButton is not null)
                {
                    var detailsClick = detailsButton.Button.TranslatePoint(
                        new Point(
                            detailsButton.Button.Bounds.Width / 2,
                            detailsButton.Button.Bounds.Height / 2
                        ),
                        window
                    );
                    Assert.NotNull(detailsClick);
                    window.MouseMove(detailsClick!.Value);
                    window.MouseDown(detailsClick.Value, MouseButton.Left);
                    window.MouseUp(detailsClick.Value, MouseButton.Left);
                    for (var i = 0; i < 40; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.True(
                        ((Desktop.Models.ConversationRow)detailsButton.Button.DataContext!)
                            .Step!
                            .IsDetailsExpanded
                    );
                    Assert.False(list.IsFollowing);
                }

                window.MouseMove(pointer);
                var before = sv.Offset.Y;
                for (var i = 0; i < 20; i++)
                {
                    if (AtBottom(sv))
                        break;
                    window.MouseWheel(pointer, new Vector(0, -2));
                    for (var j = 0; j < 12; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.False(
                        list.IsFollowing && !AtBottom(sv),
                        $"展开后第 {i} 次下滚在未到底时恢复跟随：offset={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    Assert.True(
                        sv.Offset.Y >= before - 3,
                        $"展开后下滚被拉回上方：before={before}, now={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    before = sv.Offset.Y;
                }
                Assert.True(AtBottom(sv), "展开过程后无法滚到底部");

                // 再次到中段，做一次大跨度快速滚轮：先在估算底部停住，
                // 随后的实测高度增长必须继续贴底，不能留下底部空隙。
                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.45));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }
                window.MouseWheel(pointer, new Vector(0, -60));
                PumpUntil(() => AtBottom(sv), "快速滚到底后不应留底部空隙");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// compact 系统行位于历史中段时，阅读到该边界再向下滚，窗口换页必须
    /// 以已实现行顶部为锚点；否则估算高度会把视口拉回 compact 上方。
    /// </summary>
    [Fact]
    public void CompactBoundary_WheelDownAfterReading_DoesNotJumpUp()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 240; i++)
            {
                if (i == 120)
                {
                    var compact = new Desktop.Models.ChatMessage(
                        Desktop.Models.MessageSender.System
                    );
                    compact.Content.Append(
                        Core.Data.SessionInternalMessages.FormatCompactSummaryPreview(
                            "## 压缩概览\n压缩前 1234 条消息，保留最近 20 条"
                        )
                    );
                    _vm.Messages.Add(compact);
                }

                if (i % 2 == 0)
                {
                    var user = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User);
                    user.AppendContent($"历史问题 {i}：继续写这一章");
                    _vm.Messages.Add(user);
                    continue;
                }

                // collapsed Process 行的真实高度与估算有明显差异，正好放大
                // compact 边界换页时的锚点误差。
                var agent = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                for (var s = 0; s < 4; s++)
                    agent.AgentSteps.Add(
                        new Desktop.Models.AgentStep
                        {
                            Kind = Desktop.Models.StepKind.ToolCall,
                            Title = $"工具 {i}-{s}",
                            Status = Desktop.Models.AgentStepStatus.Completed,
                            ArgumentsSummary = "参数",
                            ResultSummary = "结果",
                        }
                    );
                agent.IsProcessExpanded = false;
                _vm.Messages.Add(agent);
            }
            _vm.HasMessages = true;

            var (window, view, list, sv) = ShowConversation(height: 900);
            try
            {
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始贴底");
                var pointer = new Point(450, 450);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");

                var rows =
                    (System.Collections.ObjectModel.ObservableCollection<Desktop.Models.ConversationRow>)
                        typeof(Desktop.Views.ConversationView)
                            .GetField(
                                "_rows",
                                System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.NonPublic
                            )!
                            .GetValue(view)!;
                var heightIndex = (Desktop.Controls.HeightIndex<Desktop.Models.ConversationRow>)
                    typeof(Desktop.Views.ConversationView)
                        .GetField(
                            "_heightIndex",
                            System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.NonPublic
                        )!
                        .GetValue(view)!;
                var compactRow = Assert.Single(rows, row =>
                    row.Message.Content?.ToString()
                        .StartsWith(
                            Core.Data.SessionInternalMessages.CompactSummaryPrefix,
                            StringComparison.Ordinal
                        ) == true
                );
                var compactIndex = rows.IndexOf(compactRow);
                sv.Offset = sv.Offset.WithY(
                    Math.Max(0, heightIndex.OffsetOf(compactIndex) - sv.Viewport.Height * 0.5)
                );
                PumpUntil(
                    () => list.TopSpacerHeight > 0.5 && list.BottomSpacerHeight > 0.5,
                    "compact 中段窗口"
                );
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<ContentPresenter>()
                            .Any(p =>
                                ReferenceEquals(
                                    (p.DataContext as Desktop.Models.ConversationRow)?.Message,
                                    compactRow.Message
                                )
                            ),
                    "compact 行已进入 live window"
                );

                var before = sv.Offset.Y;
                for (var i = 0; i < 24; i++)
                {
                    if (AtBottom(sv))
                        break;
                    window.MouseWheel(pointer, new Vector(0, -2));
                    for (var j = 0; j < 12; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.False(
                        list.IsFollowing && !AtBottom(sv),
                        $"compact 边界下滚在未到底时恢复跟随：offset={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    Assert.True(
                        sv.Offset.Y >= before - 3,
                        $"compact 边界下滚被拉回上方：before={before}, now={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    before = sv.Offset.Y;
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 点击思考「展开」后立刻快速下滚：延迟的 BringStepDetailsIntoView
    /// 不能因为详情已跑到视口上方而把视口反向拽回。
    /// </summary>
    [Fact]
    public void ExpandDetailsThenFastWheel_DoesNotBounceBack()
    {
        Ui.Run(() =>
        {
            for (var m = 0; m < 30; m++)
            {
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                for (var s = 0; s < 8; s++)
                {
                    var step = new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.Thinking,
                        Status = Desktop.Models.AgentStepStatus.Completed,
                        Title = $"思考 {m}-{s}",
                    };
                    step.ThinkingBuilder.Append(
                        string.Join(" ", Enumerable.Repeat("展开后快速滚到底部的思考。", 20))
                    );
                    step.NotifyThinkingChanged();
                    message.AgentSteps.Add(step);
                }
                message.IsProcessExpanded = true;
                _vm.Messages.Add(message);
            }
            _vm.HasMessages = true;

            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                Content = view,
            };
            window.Show();
            try
            {
                var sv = FindMessageScroll(view);
                PumpUntil(() => AtBottom(sv), "初始详情贴底");
                var pointer = new Point(500, 380);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");

                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.4));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                var detailHeader = FindVisibleExpandHeader(view, window, sv);
                Assert.NotNull(detailHeader);
                window.MouseMove(detailHeader.Value.Point);
                window.MouseDown(detailHeader.Value.Point, MouseButton.Left);
                window.MouseUp(detailHeader.Value.Point, MouseButton.Left);

                // 不等延迟 reveal 执行，立刻快速下滚。
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, -60));
                PumpUntil(() => AtBottom(sv), "展开后快速下滚应到底且不回弹");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 展开大量步骤后快速滚到底：先到估算 extent 底部，随后实测高度继续增长，
    /// 不能因此停在离底部一小段的位置，必须继续跟随到真实底部。
    /// </summary>
    [Fact]
    public void FastWheelToBottom_AfterExpansion_ReachesRealBottom()
    {
        Ui.Run(() =>
        {
            for (var m = 0; m < 30; m++)
            {
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                for (var s = 0; s < 12; s++)
                {
                    var step = new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.Thinking,
                        Status = Desktop.Models.AgentStepStatus.Completed,
                        Title = $"思考 {m}-{s}",
                    };
                    step.ThinkingBuilder.Append(
                        string.Join(" ", Enumerable.Repeat("快速滚到底部的步骤正文。", 24))
                    );
                    step.NotifyThinkingChanged();
                    step.IsDetailsExpanded = true;
                    message.AgentSteps.Add(step);
                }
                message.IsProcessExpanded = true;
                _vm.Messages.Add(message);
            }
            _vm.HasMessages = true;

            var view = new Desktop.Views.ConversationView { DataContext = _vm };
            var window = new Window
            {
                Width = 1000,
                Height = 760,
                Content = view,
            };
            window.Show();
            try
            {
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                    "MessageList"
                )!;
                var sv = FindMessageScroll(view);
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "快速滚动初始贴底");

                var pointer = new Point(500, 380);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止跟随");
                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.5));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                window.MouseWheel(pointer, new Vector(0, -60));
                PumpUntil(() => AtBottom(sv), "快速滚到底后应贴住真实底部");
                Assert.True(list.IsFollowing || AtBottom(sv));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 在底部附近只向上滚一小步时，不能因为 128px 吸附窗口或自然滚动
    /// 符号反转又被自动拉回底部并闪烁。
    /// </summary>
    [Fact]
    public void BottomSmallWheelUp_DoesNotSnapToBottom()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 150; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 12))
                );
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
                window.MouseWheel(pointer, new Vector(0, 1));
                for (var i = 0; i < 40; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                Assert.True(
                    sv.Offset.Y < max - 1,
                    $"小幅上滚后又到底部：offset={sv.Offset.Y:0.0} max={max:0.0} following={list.IsFollowing}"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 触控板慢滚会连续产生亚像素/极小 delta；任何一次向上位移都必须
    /// 先退出跟随，不能等累积超过阈值后才判断。
    /// </summary>
    [Fact]
    public void BottomSlowTrackpadUp_DoesNotSnapToBottom()
    {
        Ui.Run(() =>
        {
            SeedHistory(150);
            var (window, _, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv) && list.IsFollowing, "初始底部跟随");
                var pointer = new Point(450, 350);
                window.MouseMove(pointer);

                // 每次只滚很小一格，模拟触控板慢速上推。
                for (var i = 0; i < 8; i++)
                {
                    window.MouseWheel(pointer, new Vector(0, 0.1));
                    for (var j = 0; j < 4; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(2);
                    }
                }

                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(2);
                }

                var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                Assert.False(list.IsFollowing, "慢速上滚必须停止跟随");
                Assert.True(
                    sv.Offset.Y < max - 0.01,
                    $"慢速上滚被吸附回底部：offset={sv.Offset.Y:0.00} max={max:0.00}"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 快速滚到底后可能停在 maxOffset-20；用户再向下滚时应吸附到底，
    /// 不能要求再滚一次。
    /// </summary>
    [Fact]
    public void BottomNearGap_WheelDown_SnapsToBottom()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 120; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 10))
                );
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
                list.PauseFollow();
                var max = sv.Extent.Height - sv.Viewport.Height;
                sv.Offset = sv.Offset.WithY(Math.Max(0, max - 20));

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, -1));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }
                var maxAfter = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                Assert.True(
                    AtBottom(sv),
                    $"offset={sv.Offset.Y:0.0} max={maxAfter:0.0} following={list.IsFollowing} extent={sv.Extent.Height:0.0} viewport={sv.Viewport.Height:0.0}"
                );
                Assert.True(list.IsFollowing);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 小于 1px 的向上位移也表达“用户要阅读历史”的意图。旧的 -0.5px
    /// 阈值会漏掉这种触控板慢滚，并在下一帧按接近底部重新吸附。
    /// </summary>
    [Fact]
    public void BottomSubPixelUp_StopsFollowWithoutSnap()
    {
        Ui.Run(() =>
        {
            SeedHistory(200);
            var (window, _, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv) && list.IsFollowing, "初始底部跟随");

                var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = sv.Offset.WithY(max - 0.4);
                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                Assert.False(list.IsFollowing, "亚像素上滚也必须停止跟随");
                Assert.True(
                    sv.Offset.Y < max - 0.01,
                    $"亚像素上滚被吸附回底部：offset={sv.Offset.Y:0.00} max={max:0.00}"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 失焦前已暂停跟随（例如刚展开详情），失焦期间继续追加消息；
    /// 切回来必须保持阅读位置，不能因为“离开时在底部”就 ResetFollow。
    /// </summary>
    [Fact]
    public void WindowDeactivateReactivate_Paused_DoesNotResetFollow()
    {
        Ui.Run(() =>
        {
            SeedHistory(120);
            var (window, _, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv), "初始贴底");
                list.PauseFollow();
                var pausedOffset = sv.Offset.Y;

                RaiseWindowEvent(window, "Deactivated");
                for (var i = 0; i < 30; i++)
                {
                    var message = new Desktop.Models.ChatMessage(
                        Desktop.Models.MessageSender.Agent
                    );
                    message.AppendContent($"失焦期间新增 {i} " + new string('文', 40));
                    _vm.Messages.Add(message);
                }
                Dispatcher.UIThread.RunJobs();

                Assert.False(list.IsFollowing, "暂停状态在失焦期间必须保持");
                Assert.False(AtBottom(sv), "暂停状态不得因后台追加自动贴底");

                RaiseWindowEvent(window, "Activated");
                PumpUntil(
                    () => !list.IsFollowing && Math.Abs(sv.Offset.Y - pausedOffset) <= 1,
                    "切回窗口后保持暂停位置"
                );
                Assert.False(AtBottom(sv), "暂停状态切回后不得回到底部");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 跟随状态与窗口焦点无关：失焦期间 agent 继续输出时仍要贴底，
    /// 不能等切回来再补一次跳转（那会和展开/布局竞争产生闪烁）。
    /// </summary>
    [Fact]
    public void WindowDeactivated_WhileFollowing_KeepsFollowingNewContent()
    {
        Ui.Run(() =>
        {
            SeedHistory(120);
            var (window, _, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv), "初始贴底");
                Assert.True(list.IsFollowing);

                RaiseWindowEvent(window, "Deactivated");
                for (var i = 0; i < 30; i++)
                {
                    var message = new Desktop.Models.ChatMessage(
                        Desktop.Models.MessageSender.Agent
                    );
                    message.AppendContent($"失焦期间新增 {i} " + new string('文', 40));
                    _vm.Messages.Add(message);
                }

                // 不触发 Activated：失焦期间也必须已经贴底。
                PumpUntil(() => AtBottom(sv), "失焦期间自动跟随");
                Assert.True(list.IsFollowing, "失焦不得关闭跟随");

                RaiseWindowEvent(window, "Activated");
                PumpUntil(() => AtBottom(sv) && list.IsFollowing, "切回窗口后仍贴底");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Running 中的流式更新通常不是 Messages.Add，而是同一条 Agent 消息里追加
    /// step/正文（ReplaceRowsForMessage）。失焦时跟随态必须继续同步窗口并贴底，
    /// 不能等用户回来后手动滚动才继续渲染。
    /// </summary>
    [Fact]
    public void WindowDeactivated_WhileFollowing_StreamingRowUpdatesKeepFollowing()
    {
        Ui.Run(() =>
        {
            SeedHistory(80);
            var live = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            live.AppendContent("开始生成");
            live.AgentSteps.Add(
                new Desktop.Models.AgentStep
                {
                    Kind = Desktop.Models.StepKind.ToolCall,
                    Title = "初始工具",
                    Status = Desktop.Models.AgentStepStatus.Completed,
                    ArgumentsSummary = "参数",
                    ResultSummary = "结果",
                }
            );
            _vm.Messages.Add(live);
            _vm.HasMessages = true;

            var (window, view, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv) && list.IsFollowing, "初始贴底");

                RaiseWindowEvent(window, "Deactivated");
                for (var i = 0; i < 20; i++)
                {
                    live.AppendContent($"\n\n流式段落 {i} " + new string('文', 80));
                    live.AgentSteps.Add(
                        new Desktop.Models.AgentStep
                        {
                            Kind = Desktop.Models.StepKind.ToolCall,
                            Title = $"流式工具 {i}",
                            Status = Desktop.Models.AgentStepStatus.Completed,
                            ArgumentsSummary = "参数",
                            ResultSummary = "结果",
                        }
                    );
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                PumpUntil(() => AtBottom(sv), "失焦流式行更新继续贴底");
                Assert.True(list.IsFollowing, "失焦期间跟随态不得被流式行替换关闭");
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<ContentPresenter>()
                            .Any(p =>
                                (p.DataContext as Desktop.Models.ConversationRow)?.Step?.Title
                                == "流式工具 19"
                            ),
                    "最新流式 step 已进入 live window"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Agent 运行中用户展开详情：立即中断跟随；失焦/新增内容/切回来都不能
    /// 把展开位置重置到底部，也不能让延迟 reveal 再次拽动视口。
    /// </summary>
    [Fact]
    public void ExpandedDetails_WhileRunning_SurviveWindowDeactivate()
    {
        Ui.Run(() =>
        {
            SeedHistory(40, "历史回答");

            var target = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.Thinking,
                Status = Desktop.Models.AgentStepStatus.Completed,
                Title = "展开中的思考",
                IsDetailsExpanded = false,
            };
            step.ThinkingBuilder.Append(
                string.Join(" ", Enumerable.Repeat("展开后需要保持阅读的思考正文。", 30))
            );
            step.NotifyThinkingChanged();
            target.AgentSteps.Add(step);
            target.IsProcessExpanded = true;
            target.AppendContent("正在生成中的回答");
            _vm.Messages.Add(target);
            _vm.HasMessages = true;

            var (window, view, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv), "初始贴底");

                var header = FindVisibleExpandHeader(view, window, sv, step);
                Assert.NotNull(header);
                window.MouseMove(header.Value.Point);
                window.MouseDown(header.Value.Point, MouseButton.Left);
                window.MouseUp(header.Value.Point, MouseButton.Left);
                PumpUntil(() => step.IsDetailsExpanded, "详情展开");
                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                Assert.False(list.IsFollowing, "展开详情必须中断自动跟随");
                // 最后一条消息的详情展开后内容仍可能填不满视口，此时 offset 恰好
                // 等于 max 不算回底；关键是跟随已关闭，后续新增内容不会改变位置。
                var expandedOffset = sv.Offset.Y;

                RaiseWindowEvent(window, "Deactivated");
                for (var i = 0; i < 30; i++)
                {
                    var message = new Desktop.Models.ChatMessage(
                        Desktop.Models.MessageSender.Agent
                    );
                    message.AppendContent($"失焦期间新增 {i} " + new string('文', 40));
                    _vm.Messages.Add(message);
                }
                Dispatcher.UIThread.RunJobs();

                Assert.False(list.IsFollowing);
                Assert.False(AtBottom(sv));

                RaiseWindowEvent(window, "Activated");
                PumpUntil(
                    () => !list.IsFollowing && Math.Abs(sv.Offset.Y - expandedOffset) <= 2,
                    "切回后保持展开位置"
                );
                Assert.True(step.IsDetailsExpanded);
                Assert.False(AtBottom(sv));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 过程展开后大量 Step 行的真实高度与固定估算差异很大；用户向下滚动时
    /// anchor 不能因为高度修正把视口反复拉回上方。
    /// </summary>
    [Fact]
    public void ExpandedProcess_ScrollDown_DoesNotGetPulledUp()
    {
        Ui.Run(() =>
        {
            for (var m = 0; m < 40; m++)
            {
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                for (var s = 0; s < 16; s++)
                {
                    if (s % 2 == 0)
                    {
                        var step = new Desktop.Models.AgentStep
                        {
                            Kind = Desktop.Models.StepKind.Thinking,
                            Status = Desktop.Models.AgentStepStatus.Completed,
                            Title = $"思考 {m}-{s}",
                        };
                        step.ThinkingBuilder.Append(
                            string.Join(" ", Enumerable.Repeat("这是一段较长的思考正文。", 20))
                        );
                        step.NotifyThinkingChanged();
                        step.IsDetailsExpanded = true;
                        message.AgentSteps.Add(step);
                    }
                    else
                    {
                        message.AgentSteps.Add(
                            new Desktop.Models.AgentStep
                            {
                                Kind = Desktop.Models.StepKind.ToolCall,
                                Title = $"工具 {m}-{s}",
                                Status = Desktop.Models.AgentStepStatus.Completed,
                                ArgumentsSummary = $"参数 {m}-{s}",
                                ResultSummary = $"结果 {m}-{s}",
                                FullResult = string.Join(
                                    "\n",
                                    Enumerable.Repeat("工具完整结果", 40)
                                ),
                            }
                        );
                    }
                }
                message.IsProcessExpanded = true;
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "展开过程贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止过程跟随");

                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.45));
                for (var i = 0; i < 40; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                var before = sv.Offset.Y;
                for (var i = 0; i < 8; i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -2));
                    for (var j = 0; j < 12; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.False(list.IsFollowing, $"过程第 {i} 次下滚不应恢复跟随");
                    Assert.True(
                        sv.Offset.Y >= before - 2,
                        $"过程下滚被拉回上方：before={before}, now={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    before = sv.Offset.Y;
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 单条超长消息会进入 block 级虚拟化；滚动到 block 边界时不能把外层
    /// 视口甩到尾部（block 高度在 realize 边界必须保持稳定）。
    /// </summary>
    [Fact]
    public void BlockVirtualizedLongMessage_ScrollDownAtBoundary_DoesNotJumpToTail()
    {
        Ui.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            for (var i = 0; i < 180; i++)
            {
                message.AppendContent(
                    $"## 段落 {i}\n\n"
                        + string.Join(
                            " ",
                            Enumerable.Repeat("这是用于撑开 block 虚拟化边界的长正文内容。", 12)
                        )
                        + "\n\n"
                );
            }
            _vm.Messages.Add(message);
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "长消息贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止长消息跟随");

                sv.Offset = sv.Offset.WithY(Math.Max(1000, sv.Extent.Height * 0.5));
                for (var i = 0; i < 30; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }

                var settled = sv.Offset.Y;
                for (var i = 0; i < 40; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }
                Assert.InRange(sv.Offset.Y, settled - 2, settled + 2);

                var before = sv.Offset.Y;
                for (var i = 0; i < 4; i++)
                {
                    window.MouseWheel(pointer, new Vector(0, -2));
                    for (var j = 0; j < 10; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.False(list.IsFollowing, $"长消息第 {i} 次下滚不应恢复跟随");
                    Assert.True(
                        sv.Offset.Y < Math.Max(0, sv.Extent.Height - sv.Viewport.Height) - 1,
                        $"长消息第 {i} 次下滚跳尾：before={before}, offset={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 完整历史虚拟化下，用户停在历史位置时追加消息只增长底部 spacer，
    /// 不能把视口拉回底部；这是自动跟随与虚拟化窗口的共同边界。
    /// </summary>
    [Fact]
    public void VirtualizedScroll_UserReading_NewMessageDoesNotMoveOffset()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 8))
                );
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始贴底");

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv) && !list.IsFollowing, "滚轮上滚并进入阅读态");

                var beforeOffset = sv.Offset.Y;
                var beforeExtent = sv.Extent.Height;
                var newMessage = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                newMessage.AppendContent("新消息");
                _vm.Messages.Add(newMessage);

                for (var i = 0; i < 20; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(10);
                }

                Assert.InRange(sv.Offset.Y, beforeOffset - 2, beforeOffset + 2);
                Assert.True(sv.Extent.Height > beforeExtent, "底部 spacer 应增长");
                Assert.True(!list.IsFollowing, "用户阅读历史时不得恢复跟随");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// LiveMarkdown 流式长高时，虚拟窗口和 extent 每帧变化，但跟随尾部必须始终贴底；
    /// 同时窗口不能因为正文长高而膨胀到全量行。
    /// </summary>
    [Fact]
    public void VirtualizedScroll_StreamingLiveMarkdown_StaysPinned()
    {
        Ui.Run(() =>
        {
            for (var i = 0; i < 120; i++)
            {
                var message = new Desktop.Models.ChatMessage(
                    i % 2 == 0
                        ? Desktop.Models.MessageSender.User
                        : Desktop.Models.MessageSender.Agent
                );
                message.AppendContent(
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文", 8))
                );
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
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "初始贴底");

                var stream = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                _vm.Messages.Add(stream);
                PumpUntil(() => AtBottom(sv) && list.Items.Count > 0, "流式行挂载");

                for (var i = 0; i < 80; i++)
                {
                    stream.AppendContent($"第 {i} 段流式正文，包含 **Markdown**。\n\n");
                    for (var j = 0; j < 4; j++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    Assert.True(
                        AtBottom(sv),
                        $"流式第 {i} 段后应贴底：offset={sv.Offset.Y}, extent={sv.Extent.Height}"
                    );
                    Assert.InRange(list.Items.Count, 1, 200);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 超长用户输入之后开始流式步骤：新步骤行与贴底必须在下一帧绘制前完成。
    /// 若行重建/追底留在 Background（绘制之后），大用户气泡会先按旧 offset
    /// 闪一整帧。这里只执行 Normal + Render，故意不跑 Background。
    /// </summary>
    [Fact]
    public void LargeUserMessage_StreamingStep_RebuildsAndPinsBeforeNextPaint()
    {
        Ui.Run(() =>
        {
            SeedHistory(20);
            var user = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User);
            user.AppendContent(
                string.Join(
                    "\n\n",
                    Enumerable.Repeat(
                        string.Concat(Enumerable.Repeat("这是一段很长的用户输入。", 8)),
                        60
                    )
                )
            );
            _vm.Messages.Add(user);
            var agent = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsWaiting = true,
            };
            _vm.Messages.Add(agent);
            _vm.HasMessages = true;

            var (window, view, list, sv) = ShowConversation();
            try
            {
                PumpUntil(() => AtBottom(sv) && list.IsFollowing, "初始贴底");

                agent.IsWaiting = false;
                agent.AgentSteps.Add(
                    new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.ToolCall,
                        Title = "流式首块",
                        Status = Desktop.Models.AgentStepStatus.Completed,
                        ArgumentsSummary = "参数",
                        ResultSummary = "结果",
                    }
                );

                // 真实帧顺序：Normal（事件/行更新）→ Render（布局/绘制）→
                // Background（旧的测量/追底）。这里故意只跑到 Render。
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
                using var stepFrame = window.CaptureRenderedFrame();

                Assert.Contains(list.Items.OfType<Desktop.Models.ConversationRow>(), row =>
                    row.Step?.Title == "流式首块"
                );
                Assert.True(
                    AtBottom(sv),
                    $"流式步骤后下一帧必须已贴底：offset={sv.Offset.Y}, "
                        + $"extent={sv.Extent.Height}, viewport={sv.Viewport.Height}"
                );

                // 正文块同样不能等 Background 才挂载/贴底。
                Dispatcher.UIThread.RunJobs();
                agent.AppendContent("第一段流式正文。");
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
                using var contentFrame = window.CaptureRenderedFrame();

                Assert.Contains(list.Items.OfType<Desktop.Models.ConversationRow>(), row =>
                    row.Kind == Desktop.Models.ConversationRowKind.Content
                    && ReferenceEquals(row.Message, agent)
                );
                Assert.True(
                    AtBottom(sv),
                    $"流式正文后下一帧必须已贴底：offset={sv.Offset.Y}, "
                        + $"extent={sv.Extent.Height}, viewport={sv.Viewport.Height}"
                );
            }
            finally
            {
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 单条 100k+ 字符消息必须按 Markdown block 虚拟化：视口附近才创建 MarkdownRenderer，
    /// 不能把整条正文变回一棵巨大视觉树；滚到首尾仍能读到对应段落。
    /// </summary>
    [Fact]
    public void LongMessage_BlockVirtualization_BoundsLiveRenderers()
    {
        Ui.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            for (var i = 0; i < 260; i++)
            {
                message.AppendContent($"## 段落 {i}\n\n");
                message.AppendContent(
                    string.Join(" ", Enumerable.Repeat($"第 {i} 段长文正文内容。", 12))
                );
                message.AppendContent("\n\n");
            }
            message.AppendContent("结尾标记");
            _vm.Messages.Add(message);
            _vm.HasMessages = true;
            Assert.True(message.Content.Length > 30_000);

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
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                            .Any(),
                    "长消息挂载"
                );

                var live = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                    .Count();
                Assert.True(live is >= 1 and <= 40, $"长消息应保持 block 窗口，实际 live={live}");
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(t =>
                                t.Text?.Contains("结尾标记") == true
                                || (
                                    t is LiveMarkdown.Avalonia.MarkdownTextBlock block
                                    && block.ActualText.Contains(
                                        "结尾标记",
                                        StringComparison.Ordinal
                                    )
                                )
                            ),
                    "长消息尾部段落"
                );

                var pointer = new Point(450, 350);
                window.MouseMove(pointer);
                window.MouseWheel(pointer, new Vector(0, 5));
                PumpUntil(() => !AtBottom(sv), "停止长消息跟随");
                sv.Offset = sv.Offset.WithY(0);
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(t =>
                                t.Text?.Contains("段落 0") == true
                                || (
                                    t is LiveMarkdown.Avalonia.MarkdownTextBlock block
                                    && block.ActualText.Contains("段落 0", StringComparison.Ordinal)
                                )
                            ),
                    "长消息顶部段落"
                );
                live = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                    .Count();
                Assert.InRange(live, 1, 40);
                Assert.InRange(list.Items.Count, 1, 10);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 高度估算的边界值：空文档、标题 margin、代码不换行、表格横向滚动条、
    /// inline code 列表的换行语义。数值来自真实 LiveMarkdown 测量。
    /// </summary>
    [Fact]
    public void EstimatorBoundary_HeadingsTableScrollbarAndCode()
    {
        Ui.Run(() =>
        {
            Assert.Equal(0, Desktop.Controls.MarkdownHeightEstimator.EstimateDocument("", 800));
            Assert.Equal(
                0,
                Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(" \n\n ", 800)
            );

            var h1 = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument("# 一级标题", 800);
            var h2 = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument("## 二级标题", 800);
            var h3 = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument("### 三级标题", 800);
            var h4 = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                "#### 四级标题",
                800
            );
            var h5 = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                "##### 五级标题",
                800
            );
            Assert.True(h1 > h2 && h2 > h3 && h3 >= h4 && h4 > h5);
            Assert.True(h5 > 0);

            var longCode =
                "```text\n"
                + string.Join("\n", Enumerable.Repeat(new string('x', 120), 3))
                + "\n```";
            var codeNarrow = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                longCode,
                120
            );
            var codeWide = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                longCode,
                2000
            );
            Assert.Equal(codeWide, codeNarrow, 1);

            var longTable =
                "| 项 | 状态 | 说明 |\n|----|------|------|\n"
                + "| 写作风格 | ✅ 已填 | 按「轻松向轻小说+情感内核」定调：日常甜中带刀、皮皮龙吐槽流、五感、拟声词节奏 |\n"
                + "| 目标读者 | ✅ 已填 | 治愈系/慢热情感向读者，不追装逼爽点，但要情感释放点黏性 |\n"
                + "| 世界规则 | ✅ 7条软法则 | 献祭/皮皮龙边界/蓝光/坏道循环/善恶二我动机/穿梭代价/恶意循环/太极终局 |";
            var tableNarrow = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                longTable,
                400
            );
            var tableWide = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                longTable,
                2000
            );
            Assert.True(tableNarrow > tableWide, "宽表格超过视口时应计入横向滚动条高度");
            Assert.True(tableWide > 0);
            Assert.True(
                Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                    "- 第一项\n  - 子项一\n  - 子项二\n- 第二项",
                    800
                ) > 0
            );

            var plainBullet = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                "- 唯一小瑕疵：石原学园 出现两次（b3b022463939 和 4ffd24143e6f 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆",
                818
            );
            var codeBullet = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                "- 唯一小瑕疵：`石原学园` 出现两次（`b3b022463939` 和 `4ffd24143e6f` 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆",
                818
            );
            Assert.True(codeBullet > 0);
            Assert.True(
                codeBullet < plainBullet,
                "列表中的 inline code chip 不参与父级换行，应少于普通文本高度"
            );
        });
    }

    /// <summary>
    /// 用真实会话里的 Markdown 形状做高度回归：标题、代码、宽表格、
    /// 嵌套列表、inline code、快速下滚会话正文。组件用例误差 ≤2px；
    /// 整段会话（含滚动条导致的内容宽度变化）允许 24px。
    /// </summary>
    [Fact]
    public void EstimatorCorpus_MatchesRenderedMarkdown()
    {
        Ui.Run(() =>
        {
            var docs = new (string Name, string Text)[]
            {
                ("plain", "第一段正文，包含中文和 English，用来验证换行。\n\n第二段正文。"),
                ("h1", "# 一级标题"),
                ("h2", "## 二级标题"),
                ("h3", "### 三级标题"),
                ("h4", "#### 四级标题"),
                ("h5", "##### 五级标题"),
                (
                    "table1",
                    "| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | 已填 | 按轻松向轻小说定调，日常甜中带刀 |"
                ),
                ("list-only", "- 第一项\n- 第二项\n- 第三项"),
                ("nested-list-only", "- 第一项\n  - 子项一\n  - 子项二\n- 第二项"),
                ("bullets-2", "- 第一项\n- 第二项"),
                ("bullet-nested-1", "- 第一项\n  - 子项一"),
                ("bullet-nested-2", "- 第一项\n  - 子项一\n  - 子项二"),
                ("bullet-nested-2-outer", "- 第一项\n  - 子项一\n  - 子项二\n- 第二项"),
                ("loose-list", "- 第一项\n\n- 第二项"),
                ("ordered-list", "1. 有序一\n2. 有序二"),
                (
                    "headings",
                    "# 一级标题\n\n## 二级标题\n\n### 三级标题\n\n#### 四级标题\n\n##### 五级标题"
                ),
                (
                    "table",
                    "| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | 已填 | 按轻松向轻小说定调，日常甜中带刀 |\n| 目标读者 | 已填 | 治愈系慢热读者 |"
                ),
                (
                    "nested-list",
                    "- 第一项\n  - 子项一\n  - 子项二\n- 第二项\n\n1. 有序一\n2. 有序二"
                ),
                ("quote", "> 引用第一行\n> 引用第二行\n>\n> 引用第三段"),
                ("code", "```csharp\nvar a = 1;\nvar b = 2;\n```"),
                (
                    "longcode",
                    "```text\n"
                        + string.Join(
                            "\n",
                            Enumerable.Repeat("很长的代码行 " + new string('x', 100), 3)
                        )
                        + "\n```"
                ),
                ("thematic", "第一段\n\n---\n\n第二段"),
                ("unclosed-code", "```csharp\nvar a = 1;\nvar b = 2;"),
                ("unclosed-table", "| 角色 | 身份 |\n| 林照 | 主角 |"),
                (
                    "mixed",
                    "## 本轮做了什么\n\n**粗体**、`inline`、[链接](https://example.com) 与普通文本。\n\n1. 第一项\n2. 第二项\n\n> 引用\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```"
                ),
                ("sf-h2", "## 本轮做了什么"),
                (
                    "sf-para",
                    "7 条规则都是 `setting + constraint` 级别，是全书一致性的锚点（角色行为、时间线、逻辑都要对得上）。"
                ),
                (
                    "sf-table",
                    "| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | ✅ 已填 | 按「轻松向轻小说+情感内核」定调：日常甜中带刀、皮皮龙吐槽流、五感、拟声词节奏 |\n| 目标读者 | ✅ 已填 | 治愈系/慢热情感向读者，不追装逼爽点，但要情感释放点黏性 |\n| 世界规则 | ✅ 7条软法则 | 献祭/皮皮龙边界/蓝光/坏道循环/善恶二我动机/穿梭代价/恶意循环/太极终局 |"
                ),
                ("sf-table-h", "| 项 | 状态 | 说明 |\n|----|------|------|"),
                (
                    "sf-table-1",
                    "| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | ✅ 已填 | 按「轻松向轻小说+情感内核」定调：日常甜中带刀、皮皮龙吐槽流、五感、拟声词节奏 |"
                ),
                (
                    "sf-table-2",
                    "| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | ✅ 已填 | 按「轻松向轻小说+情感内核」定调：日常甜中带刀、皮皮龙吐槽流、五感、拟声词节奏 |\n| 目标读者 | ✅ 已填 | 治愈系/慢热情感向读者，不追装逼爽点，但要情感释放点黏性 |"
                ),
                (
                    "sf-rest",
                    "## 深度盘点结论\n\n**大纲 / 细纲：已经很完整，不用再大改。**\n- 总纲五卷齐全，每卷有目标、暗线、卷末钩子\n- 细纲 96 章全有梗概，每章含冲突/走向/钩子，甚至到「你'她'了」台词密度、蓝光明灭、拟声词频率这种颗粒度\n- 14 条情节线全有 tension/转折/目标章，5 条卷线+主线+暗线齐\n\n**地点 / 组织：够用，缺的很小。**\n- 15 个地点覆盖五卷主要场景（石原市、湿地、樱花道、老人院、海边……）\n- 3 个组织（石原学园/开发商/雇佣兵小队）\n- 唯一小瑕疵：`石原学园` 出现两次（`b3b022463939` 和 `4ffd24143e6f` 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆"
                ),
                (
                    "sf-bullet-huge",
                    "- "
                        + string.Join(
                            " ",
                            Enumerable.Repeat("这是一个非常长的列表项内容，用来验证列表换行。", 12)
                        )
                ),
                (
                    "sf-bullets",
                    "- 总纲五卷齐全，每卷有目标、暗线、卷末钩子\n- 细纲 96 章全有梗概，每章含冲突/走向/钩子，甚至到「你'她'了」台词密度、蓝光明灭、拟声词频率这种颗粒度\n- 14 条情节线全有 tension/转折/目标章，5 条卷线+主线+暗线齐"
                ),
                ("sf-bold-para", "**大纲 / 细纲：已经很完整，不用再大改。**"),
                (
                    "sf-bullet-code",
                    "- 唯一小瑕疵：`石原学园` 出现两次（`b3b022463939` 和 `4ffd24143e6f` 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆"
                ),
                (
                    "sf-bullet-plain",
                    "- 唯一小瑕疵：石原学园 出现两次（b3b022463939 和 4ffd24143e6f 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆"
                ),
                (
                    "sf-bullets-long",
                    "- 15 个地点覆盖五卷主要场景（石原市、湿地、樱花道、老人院、海边……）\n- 3 个组织（石原学园/开发商/雇佣兵小队）\n- 唯一小瑕疵：`石原学园` 出现两次（`b3b022463939` 和 `4ffd24143e6f` 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆"
                ),
                (
                    "session-final",
                    "详情和世界规则都补齐了。给你一份深度盘点：\n\n## 本轮做了什么\n\n| 项 | 状态 | 说明 |\n|----|------|------|\n| 写作风格 | ✅ 已填 | 按「轻松向轻小说+情感内核」定调：日常甜中带刀、皮皮龙吐槽流、五感、拟声词节奏 |\n| 目标读者 | ✅ 已填 | 治愈系/慢热情感向读者，不追装逼爽点，但要情感释放点黏性 |\n| 世界规则 | ✅ 7条软法则 | 献祭/皮皮龙边界/蓝光/坏道循环/善恶二我动机/穿梭代价/恶意循环/太极终局 |\n\n7 条规则都是 `setting + constraint` 级别，是全书一致性的锚点（角色行为、时间线、逻辑都要对得上）。\n\n## 深度盘点结论\n\n**大纲 / 细纲：已经很完整，不用再大改。**\n- 总纲五卷齐全，每卷有目标、暗线、卷末钩子\n- 细纲 96 章全有梗概，每章含冲突/走向/钩子，甚至到「你'她'了」台词密度、蓝光明灭、拟声词频率这种颗粒度\n- 14 条情节线全有 tension/转折/目标章，5 条卷线+主线+暗线齐\n\n**地点 / 组织：够用，缺的很小。**\n- 15 个地点覆盖五卷主要场景（石原市、湿地、樱花道、老人院、海边……）\n- 3 个组织（石原学园/开发商/雇佣兵小队）\n- 唯一小瑕疵：`石原学园` 出现两次（`b3b022463939` 和 `4ffd24143e6f` 父子两个），学园图书馆/天台挂在后者下——这是层级设计没问题，但同名重复建议在正文里统一指代，避免读者混淆\n\n**场景层：按你的决定，不现在建。** 细纲梗概即场景，等写到具体章节再用 `build_scene` 补精细场景。\n\n## 一个真正的硬伤：genre 标签\n\n小说类型还挂在 **「都市异能（扮猪吃虎+装逼打脸）」**，但你定的是治愈情感向。这标签会导致：\n- 云端诊断按「商业爽文」权重跑，爽点检测会一直报警「缺少装逼打脸」\n- 但你明确不追求这个\n\n我建议把类型改准。两个选项：\n\n1. **直接改 genre** 为「都市·日常·治愈情感向」，让诊断权重对齐——**最干净**\n2. **保留都市异能，但靠已填的 writing-style/target-audience 拉回**，依赖诊断时手动忽略爽点维度\n\n你要我直接把类型字段改成对齐治愈向的标签吗？（推荐选 1）"
                ),
            };

            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AppendContent("probe");
            _vm.Messages.Add(message);
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
                            .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                            .Any(),
                    "probe renderer"
                );
                var failures = new System.Text.StringBuilder();
                foreach (var (name, text) in docs)
                {
                    message.ResetContent();
                    message.AppendContent(text);
                    for (var i = 0; i < 20; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Thread.Sleep(5);
                    }
                    var renderer = view.GetVisualDescendants()
                        .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                        .Last();
                    var actual = renderer.DesiredSize.Height;
                    var estimated = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                        text,
                        renderer.Bounds.Width
                    );
                    var tolerance = name == "session-final" ? 24 : 2;
                    if (actual <= 0 || Math.Abs(estimated - actual) > tolerance)
                        failures.AppendLine(
                            $"{name}: actual={actual:0.0} estimated={estimated:0.0} "
                                + $"delta={estimated - actual:0.0} tolerance={tolerance} width={renderer.Bounds.Width:0.0}"
                        );
                }
                Assert.True(failures.Length == 0, failures.ToString());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 估算器必须和真实 LiveMarkdown 的正文/表格布局对齐，而不是字符数除法。
    /// </summary>
    [Fact]
    public void MarkdownEstimator_MatchesRenderedNovelTextAndTable()
    {
        Ui.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AppendContent(
                """
                # 第一章 风起

                这是第一段正文，包含中文、English 和 **粗体**，用于验证换行与行高。

                这是第二段正文，长度足够触发多行换行。

                | 角色 | 身份 | 备注 |
                | --- | --- | --- |
                | 林照 | 主角 | 少年剑客 |
                | 苏晚 | 医者 | 山中来客 |

                - 第一项
                - 第二项
                - 第三项

                > 引用块第一行
                > 引用块第二行

                ```csharp
                var x = 1;
                var y = 2;
                ```
                """
            );
            _vm.Messages.Add(message);
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
                            .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                            .Any(),
                    "Markdown 正文挂载"
                );
                var renderer = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownRenderer>()
                    .Last();
                var code = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.CodeBlock>()
                    .Single();
                var codeBody = Assert.IsType<LiveMarkdown.Avalonia.MarkdownTextBlock>(
                    code.CodeTextBlock
                );
                Assert.Equal(11, codeBody.FontSize);
                Assert.Equal(18, codeBody.LineHeight);
                Assert.Contains(
                    "Fragment Mono",
                    codeBody.FontFamily.Name,
                    StringComparison.Ordinal
                );

                var actual = renderer.DesiredSize.Height;
                var estimated = Desktop.Controls.MarkdownHeightEstimator.EstimateDocument(
                    message.Content.ToString(),
                    renderer.Bounds.Width
                );

                Assert.True(actual > 0);
                Assert.True(
                    Math.Abs(estimated - actual) <= 2,
                    $"actual={actual:0.0} estimated={estimated:0.0} width={renderer.Bounds.Width:0.0}"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ProcessRows_ExpandClickedMessage_NotBottomMessage()
    {
        Ui.Run(() =>
        {
            var messages = new List<Desktop.Models.ChatMessage>();
            for (var i = 0; i < 10; i++)
            {
                var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
                message.AppendContent($"第 {i} 条回答");
                message.AgentSteps.Add(
                    new Desktop.Models.AgentStep
                    {
                        Kind = Desktop.Models.StepKind.ToolCall,
                        Title = $"工具 {i}",
                        Status = Desktop.Models.AgentStepStatus.Completed,
                    }
                );
                message.IsProcessExpanded = false;
                messages.Add(message);
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
                PumpUntil(
                    () =>
                        view.GetVisualDescendants()
                            .OfType<Button>()
                            .Any(b =>
                                b.DataContext
                                    is Desktop.Models.ConversationRow
                                    {
                                        Kind: Desktop.Models.ConversationRowKind.Process
                                    }
                            ),
                    "过程行挂载"
                );
                var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>(
                    "MessageList"
                )!;
                var scroll = FindMessageScroll(view);
                // 回到顶部，保证点击的是当前视口里真实可见的过程行。
                scroll.Offset = scroll.Offset.WithY(0);
                PumpUntil(() => scroll.Offset.Y <= 0.5, "回到顶部");

                var processButton = view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b =>
                        b.DataContext
                            is Desktop.Models.ConversationRow
                            {
                                Kind: Desktop.Models.ConversationRowKind.Process,
                                Message: var message,
                            }
                        && ReferenceEquals(message, messages[0])
                    )
                    .FirstOrDefault(b =>
                        b.TranslatePoint(new Point(0, 0), scroll) is { } point
                        && point.Y >= 0
                        && point.Y < scroll.Viewport.Height
                    );
                Assert.NotNull(processButton);
                processButton!.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)
                );
                for (var i = 0; i < 10; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(5);
                }
                Assert.True(messages[0].IsProcessExpanded);
                Assert.False(messages[1].IsProcessExpanded);
                Assert.False(list.IsFollowing);
                var stepRow = list
                    .Items.OfType<Desktop.Models.ConversationRow>()
                    .FirstOrDefault(row =>
                        ReferenceEquals(row.Message, messages[0])
                        && row.Kind == Desktop.Models.ConversationRowKind.Step
                    );
                Assert.NotNull(stepRow);
                var title = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(text =>
                        ReferenceEquals(text.DataContext, stepRow)
                        && text.Text?.Contains("工具 0", StringComparison.Ordinal) == true
                    );
                Assert.NotNull(title);
                var titlePoint = title!.TranslatePoint(new Point(0, 0), scroll);
                Assert.NotNull(titlePoint);
                Assert.InRange(titlePoint.Value.Y, 0, scroll.Viewport.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 动作条/标签/行距都是固定高度，必须进入虚拟化估算；否则未实测行
    /// 会让 extent、锚点和“接近底部”判断整体偏移。
    /// </summary>
    [Fact]
    public void RowHeightEstimates_MatchFixedActionBarLayout()
    {
        Ui.Run(() =>
        {
            var user = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.User);
            user.AppendContent("user-probe");
            _vm.Messages.Add(user);

            var content = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            content.AppendContent("agent-probe");
            _vm.Messages.Add(content);

            var stepContent = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsProcessExpanded = true,
            };
            stepContent.AgentSteps.Add(Desktop.Models.AgentStep.CreateContent("step-probe"));
            _vm.Messages.Add(stepContent);

            var stepHeader = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsProcessExpanded = true,
            };
            stepHeader.AgentSteps.Add(
                new Desktop.Models.AgentStep
                {
                    Kind = Desktop.Models.StepKind.ToolCall,
                    Title = "tool-probe",
                    Status = Desktop.Models.AgentStepStatus.Completed,
                }
            );
            _vm.Messages.Add(stepHeader);

            var actions = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsCompleted = true,
            };
            _vm.Messages.Add(actions);

            var system = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.System);
            system.AppendContent("system-probe");
            _vm.Messages.Add(system);

            var interrupted = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            interrupted.AppendContent("interrupted-probe");
            interrupted.IsInterrupted = true;
            _vm.Messages.Add(interrupted);

            var process = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsProcessExpanded = false,
            };
            process.AgentSteps.Add(
                new Desktop.Models.AgentStep
                {
                    Kind = Desktop.Models.StepKind.ToolCall,
                    Title = "process-probe",
                    Status = Desktop.Models.AgentStepStatus.Completed,
                }
            );
            _vm.Messages.Add(process);

            var waiting = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsWaiting = true,
            };
            _vm.Messages.Add(waiting);
            _vm.HasMessages = true;

            var (window, view, list, _) = ShowConversation(height: 1600);
            try
            {
                // RebuildRows 默认展开最后 4 条消息；显式还原本用例要覆盖的行状态。
                stepContent.IsProcessExpanded = true;
                stepHeader.IsProcessExpanded = true;
                process.IsProcessExpanded = false;
                PumpUntil(
                    () =>
                        list.Items.Count >= 9
                        && view.GetVisualDescendants()
                            .OfType<ContentPresenter>()
                            .Any(p =>
                                p.DataContext
                                    is Desktop.Models.ConversationRow
                                    {
                                        Message: var mountedMessage,
                                    }
                                && ReferenceEquals(mountedMessage, user)
                            ),
                    "探针行挂载"
                );

                var actualRows = view.GetVisualDescendants()
                    .OfType<ContentPresenter>()
                    .Where(p =>
                        p.DataContext is Desktop.Models.ConversationRow && p.Bounds.Height > 0.5
                    )
                    .GroupBy(p => (Desktop.Models.ConversationRow)p.DataContext!)
                    .Select(g => g.OrderBy(p => p.GetVisualAncestors().Count()).First())
                    .ToDictionary(
                        p => (Desktop.Models.ConversationRow)p.DataContext!,
                        p => p.Bounds.Height
                    );

                void AssertRow(
                    Desktop.Models.ConversationRow row,
                    double expectedHeight,
                    string label
                )
                {
                    Assert.True(actualRows.TryGetValue(row, out var actual), $"{label} 行未挂载");
                    Assert.InRange(
                        view.EstimateRowHeight(row),
                        Math.Min(expectedHeight, actual) - 1,
                        Math.Max(expectedHeight, actual) + 1
                    );
                    Assert.InRange(actual, expectedHeight - 1, expectedHeight + 1);
                }

                Desktop.Models.ConversationRow FindRow(
                    Desktop.Models.ChatMessage message,
                    Desktop.Models.ConversationRowKind kind
                ) =>
                    Assert.Single(actualRows.Keys, r =>
                        ReferenceEquals(r.Message, message) && r.Kind == kind
                    );

                AssertRow(FindRow(user, Desktop.Models.ConversationRowKind.User), 102, "user");
                AssertRow(
                    FindRow(content, Desktop.Models.ConversationRowKind.Content),
                    70,
                    "content"
                );
                // 正文步骤不再挂动作条：只保留气泡高度。
                AssertRow(
                    FindRow(stepContent, Desktop.Models.ConversationRowKind.Step),
                    78,
                    "step-content"
                );
                // 无详情按钮时标题行只有 16px 文本 + 1px 上边距。
                AssertRow(
                    FindRow(stepHeader, Desktop.Models.ConversationRowKind.Step),
                    29,
                    "step-header"
                );
                AssertRow(
                    FindRow(actions, Desktop.Models.ConversationRowKind.Actions),
                    36,
                    "actions"
                );
                AssertRow(FindRow(system, Desktop.Models.ConversationRowKind.System), 51, "system");
                AssertRow(
                    FindRow(interrupted, Desktop.Models.ConversationRowKind.Interrupted),
                    51,
                    "interrupted"
                );
                AssertRow(
                    FindRow(process, Desktop.Models.ConversationRowKind.Process),
                    32,
                    "process"
                );
                AssertRow(
                    FindRow(waiting, Desktop.Models.ConversationRowKind.Waiting),
                    28,
                    "waiting"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    private void SeedHistory(int count, string prefix = "历史")
    {
        for (var i = 0; i < count; i++)
        {
            var message = new Desktop.Models.ChatMessage(
                i % 2 == 0 ? Desktop.Models.MessageSender.User : Desktop.Models.MessageSender.Agent
            );
            message.AppendContent(
                $"{prefix} {i} " + string.Join(" ", Enumerable.Repeat("正文内容", 12))
            );
            _vm.Messages.Add(message);
        }
        _vm.HasMessages = true;
    }

    private (
        Window Window,
        Desktop.Views.ConversationView View,
        Desktop.Controls.AutoScrollItemsControl List,
        ScrollViewer Scroll
    ) ShowConversation(int height = 700)
    {
        var view = new Desktop.Views.ConversationView { DataContext = _vm };
        var window = new Window
        {
            Width = 900,
            Height = height,
            Content = view,
        };
        window.Show();
        return (
            window,
            view,
            view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!,
            FindMessageScroll(view)
        );
    }

    /// <summary>
    /// Headless Window 不会真的抢焦点；直接触发 WindowBase 的私有事件，
    /// 验证生产接线与 offset 恢复逻辑。
    /// </summary>
    private static void RaiseWindowEvent(Window window, string eventName)
    {
        var field = typeof(WindowBase).GetField(
            eventName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        var handler = field?.GetValue(window) as EventHandler;
        Assert.NotNull(handler);
        handler(window, EventArgs.Empty);
        Dispatcher.UIThread.RunJobs();
    }

    private static (TextBlock Header, Point Point)? FindVisibleExpandHeader(
        Desktop.Views.ConversationView view,
        Window window,
        ScrollViewer scroll,
        Desktop.Models.AgentStep? target = null
    )
    {
        return view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text =>
                text.DataContext is Desktop.Models.ConversationRow { Step: { } step }
                && (target is null || ReferenceEquals(step, target))
                && text.Text?.Contains("展开", StringComparison.Ordinal) == true
                && text.IsEffectivelyVisible
            )
            .Select(text => new
            {
                Text = text,
                Point = text.TranslatePoint(
                    new Point(text.Bounds.Width / 2, text.Bounds.Height / 2),
                    window
                ),
                Top = text.TranslatePoint(new Point(0, 0), scroll),
            })
            .Where(item =>
                item.Point is { } point
                && item.Top is { } top
                && top.Y >= 0
                && top.Y < scroll.Viewport.Height
            )
            .Select(item => (item.Text, item.Point!.Value))
            .LastOrDefault();
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
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("## 历史标题") == true
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Slash_PopupStartsAtFirstCommand_AndArrowKeysWrap()
    {
        RunWithInput("/", (view, input) =>
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

            var last = _vm.CommandSuggestions.Count - 1;
            Assert.True(last > 0, "需要至少两个命令才能验证首尾循环");

            // 首项再向上 → 末项
            input.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
            );
            Assert.Equal(last, _vm.SelectedCommandIndex);
            Assert.Equal(last, list.SelectedIndex);

            // 末项再向下 → 首项
            input.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
            );
            Assert.Equal(0, _vm.SelectedCommandIndex);
            Assert.Equal(0, list.SelectedIndex);

            // 首项向下 → 第二项（普通前进仍正常）
            input.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down }
            );
            Assert.Equal(1, _vm.SelectedCommandIndex);

            // 第二项向上 → 首项
            input.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up }
            );
            Assert.Equal(0, _vm.SelectedCommandIndex);
        });
    }
}
