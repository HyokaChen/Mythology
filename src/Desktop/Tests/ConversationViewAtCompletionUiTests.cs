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
            },
            () => _vm.Suggestions.Count > 0
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
            },
            () => _vm.Suggestions.Count > 0
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

                // 全量历史可达：程序化归零后窗口应换到最早一段，顶部 spacer 归零。
                sv.Offset = sv.Offset.WithY(0);
                PumpUntil(
                    () => list.TopSpacerHeight <= 0.5 && list.Items.Count > 0,
                    "滚到顶部应实例化最早的行"
                );
                Assert.Contains(
                    list.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.Contains("历史 0") == true
                );
                Assert.DoesNotContain(
                    list.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.Contains("##") == true
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

                var detailButton = view.GetVisualDescendants()
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
                Assert.NotNull(detailButton);
                var click = detailButton!.Button.TranslatePoint(
                    new Point(
                        detailButton.Button.Bounds.Width / 2,
                        detailButton.Button.Bounds.Height / 2
                    ),
                    window
                );
                Assert.NotNull(click);
                window.MouseMove(click!.Value);
                window.MouseDown(click.Value, MouseButton.Left);
                window.MouseUp(click.Value, MouseButton.Left);

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
    /// 用户在底部时切到别的窗口，期间继续追加消息；切回来后必须仍贴底，
    /// 不能停在失焦前的中间 offset。
    /// </summary>
    [Fact]
    public void WindowDeactivateReactivate_AtBottom_StaysAtBottom()
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
                    $"历史 {i} " + string.Join(" ", Enumerable.Repeat("正文内容", 12))
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
                // 模拟“人在底部但跟随已暂停”（例如刚点过过程/详情）。
                // 失焦期间新增消息后，激活时仍应回到用户离开时的底部。
                list.PauseFollow();

                // Headless Window 不会真的抢焦点；直接触发 WindowBase 事件，
                // 验证生产接线与 offset 恢复逻辑。
                var field = typeof(WindowBase).GetField(
                    "Deactivated",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                var deactivated = field?.GetValue(window) as EventHandler;
                Assert.NotNull(deactivated);
                deactivated(window, EventArgs.Empty);
                Dispatcher.UIThread.RunJobs();

                // 失焦期间继续追加，旧 offset 不再对应底部
                for (var i = 0; i < 30; i++)
                {
                    var message = new Desktop.Models.ChatMessage(
                        Desktop.Models.MessageSender.Agent
                    );
                    message.AppendContent($"失焦期间新增 {i} " + new string('文', 40));
                    _vm.Messages.Add(message);
                }
                Dispatcher.UIThread.RunJobs();

                field = typeof(WindowBase).GetField(
                    "Activated",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
                var activated = field?.GetValue(window) as EventHandler;
                Assert.NotNull(activated);
                activated(window, EventArgs.Empty);
                PumpUntil(() => AtBottom(sv), "切回窗口后仍贴底");
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
                PumpUntil(() => !AtBottom(sv), "滚轮上滚");

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
                        ReferenceEquals(text.DataContext, stepRow) && text.Text == "工具 0"
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
