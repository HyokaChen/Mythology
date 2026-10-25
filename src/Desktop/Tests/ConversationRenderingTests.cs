using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Shared;
using Core.Tools;
using Desktop.Models;
using Desktop.Services;
using LiveMarkdown.Avalonia;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 对话渲染验收：Markdown 表格/diff、工具详情、思考详情的实际控件与可见性。
/// 渲染在专用 Avalonia UI 线程上串行执行，避免和 xUnit 并行测试宿主抢 UI 线程。
/// </summary>
public class ConversationRenderingTests
{
    public ConversationRenderingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private readonly ITestOutputHelper _output;

    private static readonly Lazy<HeadlessUiThread> Ui = new(() => new HeadlessUiThread());

    [Fact]
    public void Table_RendersGridWithHeaderAndRows()
    {
        Ui.Value.Run(() =>
        {
            var markdown = """
                | 角色 | 年龄 | 定位 |
                |:-----|-----:|:-----|
                | 林月如 | 18 | 女主角 |
                | 赵灵儿 | 16 | 女娲后人 |
                """;

            var (window, renderer) = Render(markdown);
            try
            {
                var grid = Assert.Single(
                    renderer.GetVisualDescendants().OfType<MarkdownTableGrid>()
                );
                var cells = grid
                    .Children.OfType<Control>()
                    .Select(c => new
                    {
                        Row = Grid.GetRow(c),
                        Column = Grid.GetColumn(c),
                        Text = string.Concat(
                            c.GetVisualDescendants()
                                .OfType<TextBlock>()
                                .Select(t =>
                                    t is MarkdownTextBlock markdown ? markdown.ActualText : t.Text
                                )
                        ),
                    })
                    .OrderBy(c => c.Row)
                    .ThenBy(c => c.Column)
                    .ToList();

                _output.WriteLine("Markdown 表格实际渲染：");
                foreach (var row in cells.GroupBy(c => c.Row))
                    _output.WriteLine(
                        $"  row {row.Key}: {string.Join(" | ", row.Select(c => c.Text))}"
                    );

                _output.WriteLine($"表格网格尺寸：{grid.Bounds}");
                Assert.True(grid.Bounds.Width > 0 && grid.Bounds.Height > 0);
                Assert.Equal(9, cells.Count);
                Assert.Equal(3, cells.Select(c => c.Row).Distinct().Count());
                Assert.Equal(3, cells.Select(c => c.Column).Distinct().Count());
                Assert.Equal(3, cells.Count(c => c.Row == 0));
                Assert.Contains(cells, c => c is { Row: 0, Text: "角色" });
                Assert.Contains(cells, c => c is { Row: 0, Text: "年龄" });
                Assert.Contains(cells, c => c is { Row: 0, Text: "定位" });
                Assert.Contains(cells, c => c is { Row: 1, Text: "林月如" });
                Assert.Contains(cells, c => c is { Row: 1, Text: "18" });
                Assert.Contains(cells, c => c is { Row: 1, Text: "女主角" });
                Assert.Contains(cells, c => c is { Row: 2, Text: "赵灵儿" });
                Assert.Contains(cells, c => c is { Row: 2, Text: "16" });
                Assert.Contains(cells, c => c is { Row: 2, Text: "女娲后人" });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiffFence_RendersCodeBlockWithRemovedAndAddedLines()
    {
        Ui.Value.Run(() =>
        {
            var markdown = """
                ```diff
                --- a/chapter.md
                +++ b/chapter.md
                @@ -1,3 +1,3 @@
                -旧句子
                +新句子
                 上下文
                ```
                """;

            var (window, renderer) = Render(markdown);
            try
            {
                var code = Assert.Single(renderer.GetVisualDescendants().OfType<CodeBlock>());
                var rendered = string.Concat(
                    code.GetVisualDescendants()
                        .OfType<MarkdownTextBlock>()
                        .Select(block => block.ActualText)
                );

                _output.WriteLine(
                    $"diff 围栏实际渲染：language={code.Language} bounds={code.Bounds}"
                );
                _output.WriteLine(rendered);

                Assert.True(code.Bounds.Height > 0);
                Assert.Equal("diff", code.Language);
                Assert.Contains(
                    code.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Text == "diff"
                );
                Assert.Contains("-旧句子", rendered);
                Assert.Contains("+新句子", rendered);
                Assert.Contains(" 上下文", rendered);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AgentMessage_RendersMarkdownTableAndDiffInConversation()
    {
        Ui.Value.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AppendContent(
                """
                修改结果：

                | 文件 | 变更 |
                |------|------|
                | chapter.md | +1 / -1 |

                ```diff
                -旧句子
                +新句子
                ```
                """
            );

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var text = RenderedText(view);
                _output.WriteLine("对话正文中的表格与 diff：");
                _output.WriteLine(text);

                Assert.Single(view.GetVisualDescendants().OfType<MarkdownTableGrid>());
                Assert.Single(view.GetVisualDescendants().OfType<CodeBlock>());
                Assert.Contains("chapter.md", text);
                Assert.Contains("-旧句子", text);
                Assert.Contains("+新句子", text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThinkingStep_DefaultsExpanded_AndCanCollapse()
    {
        Ui.Value.Run(() =>
        {
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.Thinking,
                Status = Desktop.Models.AgentStepStatus.Processing,
                IsDetailsExpanded = true,
            };
            step.ThinkingBuilder.Append("这是思考内容，流式中也必须直接可见。");
            step.NotifyThinkingChanged();

            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var streaming = RenderedText(view);
                _output.WriteLine("思考步骤流式态（默认展开）：");
                _output.WriteLine(streaming);

                Assert.Contains("思考中…", streaming);
                Assert.Contains("这是思考内容，流式中也必须直接可见。", streaming);
                Assert.Contains("收起", streaming);
                Assert.DoesNotContain(VisibleTexts(view), text => text == "思考");

                step.IsDetailsExpanded = false;
                Pump();
                var collapsed = RenderedText(view);
                _output.WriteLine("思考步骤收起态：");
                _output.WriteLine(collapsed);

                Assert.Contains("思考中…", collapsed);
                Assert.Contains("展开", collapsed);
                Assert.DoesNotContain("这是思考内容，流式中也必须直接可见。", collapsed);

                step.IsDetailsExpanded = true;
                step.Status = Desktop.Models.AgentStepStatus.Completed;
                Pump();
                var completed = RenderedText(view);
                _output.WriteLine("思考步骤完成态：");
                _output.WriteLine(completed);

                Assert.Contains("思考过程", completed);
                Assert.Contains("这是思考内容，流式中也必须直接可见。", completed);
                Assert.DoesNotContain("思考中…", completed);
                Assert.Contains("收起", completed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ContentStep_RendersBeforeFollowingToolStep()
    {
        Ui.Value.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            var contentStep = Desktop.Models.AgentStep.CreateContent(
                "I'll start with the first search now."
            );
            message.AgentSteps.Add(contentStep);
            message.AgentSteps.Add(
                new Desktop.Models.AgentStep
                {
                    Kind = Desktop.Models.StepKind.ToolCall,
                    Title = "搜索工具",
                    Status = Desktop.Models.AgentStepStatus.Completed,
                    ArgumentsSummary = "project summary",
                }
            );

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                Pump();
                var texts = VisibleTexts(view);
                _output.WriteLine("中间正文 + 工具步骤：");
                _output.WriteLine(string.Join("\n", texts));

                var contentIndex = texts.IndexOf("I'll start with the first search now.");
                var toolIndex = texts.IndexOf("搜索工具");
                Assert.True(contentIndex >= 0, "中间正文未渲染");
                Assert.True(toolIndex >= 0, "工具步骤未渲染");
                Assert.True(
                    contentIndex < toolIndex,
                    $"中间正文必须位于工具步骤之前，实际 content={contentIndex}, tool={toolIndex}"
                );

                // 中间正文与最终流式正文同一张 Bone 卡：冻结到步骤列表时背景不再从
                // 黄色突变为无底色。文字仍用 14/24 轻量排版，不抢最终答复。
                var body = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t =>
                        t.Text == "I'll start with the first search now."
                        && t.Classes.Contains("StepContentText")
                );
                Assert.Equal(14, body.FontSize);
                Assert.Equal(24, body.LineHeight);
                Assert.Equal(new Thickness(0), body.Margin);

                var contentCard = Assert.Single(
                    body.GetVisualAncestors().OfType<Border>(),
                    b => b.Classes.Contains("AgentBubble")
                );
                var cardBackground = Assert.IsType<SolidColorBrush>(contentCard.Background);
                Assert.Equal(Color.Parse("#EFF2E8"), cardBackground.Color);
                Assert.Equal(new Thickness(0, 4, 0, 4), contentCard.Margin);

                var toolTitle = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text == "搜索工具"
                );
                var bodyBottom =
                    body.TranslatePoint(new Point(0, body.Bounds.Height), view)?.Y ?? 0;
                var toolTop = toolTitle.TranslatePoint(new Point(0, 0), view)?.Y ?? 0;
                Assert.True(
                    toolTop - bodyBottom >= 10,
                    $"正文与工具步骤间距过小：{toolTop - bodyBottom:F1}px"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InputText_LeftEdgeAlignsWithMessageBody()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent);
            message.AgentSteps.Add(AgentStep.CreateContent("正文左缘"));

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var input = view.FindControl<TextBox>("InputBox")!;
            input.Text = "输入左缘";

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                Pump();

                var body = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Classes.Contains("StepContentText")
                );
                var presenter = Assert.Single(input.GetVisualDescendants().OfType<TextPresenter>());
                var bodyX = body.TranslatePoint(new Point(0, 0), view)!.Value.X;
                var inputX = presenter.TranslatePoint(new Point(0, 0), view)!.Value.X;
                Assert.Equal(bodyX, inputX, 1.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Blockquote_HasNoNestedCardAndReadableContrast()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent);
            message.AppendContent("正常答复。\n\n> 这是一段引用设定");

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var quote = Assert.Single(
                    view.GetVisualDescendants().OfType<Border>(),
                    b => b.Classes.Contains("QuoteBlock")
                );
                var quoteText = Assert.Single(
                    quote.GetVisualDescendants().OfType<MarkdownTextBlock>()
                );
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(quote.Background);
                var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(quoteText.Foreground);

                // 引用块不再是第二张卡：透明底 + 无边框，引用语义由「▎」前缀承担。
                Assert.Equal(0, background.Color.A);
                Assert.Equal(new Thickness(0), quote.BorderThickness);

                // 默认引用块是深灰底；本文件的 Markdown 正文是深色 Olive Press，
                // 若沿用默认底色会变成深底深字。按正文底色上的合成色算对比度。
                var effectiveBackground = Blend(background.Color, Color.Parse("#EFF2E8"));
                var ratio = ContrastRatio(effectiveBackground, foreground.Color);
                _output.WriteLine(
                    $"引用块底色={effectiveBackground} 文字={foreground.Color} 对比度={ratio:F2}"
                );
                Assert.True(
                    ratio >= 4.5,
                    $"引用块文字对比度不足：{ratio:F2}（底色 {effectiveBackground}，文字 {foreground.Color}）"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InterruptedNote_RendersSystemStyleRowWithoutQuoteCard()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent);
            message.AppendContent("正常答复。");
            message.IsInterrupted = true;

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<Border>(),
                    b => b.Classes.Contains("QuoteBlock") || b.Classes.Contains("InterruptedNote")
                );

                var text = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text == "⏹ 中断生成"
                );
                Assert.Contains("SystemNoticeText", text.Classes);
                Assert.Equal(13, text.FontSize);
                Assert.Equal(20, text.LineHeight);
                var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
                Assert.Equal(Color.Parse("#6B7860"), foreground.Color);

                var row = Assert.IsType<StackPanel>(text.Parent);
                Assert.Single(row.Children.OfType<TextBlock>(), t => t.Text == "系统");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SystemNotice_UsesThirteenPixelStatusTypography()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.System);
            message.AppendContent("📝 新建角色「测试」");

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var text = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text == "📝 新建角色「测试」" && t.Classes.Contains("SystemNoticeText")
                );
                Assert.True(text.IsEffectivelyVisible);
                Assert.Equal(13, text.FontSize);
                Assert.Equal(20, text.LineHeight);
                var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
                Assert.Equal(Color.Parse("#6B7860"), foreground.Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Color Blend(Color foreground, Color background)
    {
        var a = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * a + background.R * (1 - a)),
            (byte)Math.Round(foreground.G * a + background.G * (1 - a)),
            (byte)Math.Round(foreground.B * a + background.B * (1 - a))
        );
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        var (light, dark) = l1 > l2 ? (l1, l2) : (l2, l1);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    [Fact]
    public void WaitingRow_ShowsSpinnerAndRunningText_AndHidesAfterFirstActivity()
    {
        Ui.Value.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent)
            {
                IsWaiting = true,
            };

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var waiting = RenderedText(view);
                _output.WriteLine("等待行：");
                _output.WriteLine(waiting);

                Assert.Contains("执行中", waiting);
                Assert.DoesNotContain("写作中", waiting);

                var spinner = Assert.Single(
                    view.GetVisualDescendants().OfType<Desktop.Controls.BusySpinner>(),
                    s => s.DataContext == message
                );
                Assert.True(spinner.IsEffectivelyVisible);

                message.IsWaiting = false;
                Pump();

                var hidden = RenderedText(view);
                Assert.DoesNotContain("执行中", hidden);
                Assert.DoesNotContain("写作中", hidden);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EmptyAgentPlaceholder_DoesNotRenderBubbleCard()
    {
        Ui.Value.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var bubble = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Border>()
                        .Where(border => border.Classes.Contains("AgentBubble"))
                );
                Assert.False(bubble.IsEffectivelyVisible);
                Assert.Equal(0, bubble.Bounds.Width);

                // 流式首批 chunk 常是换行/空格：不得因此渲染一张空白 Bone 卡。
                message.AppendContent("\n\n  ");
                Pump();
                Assert.False(bubble.IsEffectivelyVisible);
                Assert.Equal(0, bubble.Bounds.Width);

                message.AppendContent("收到，我来处理。");
                Pump();

                Assert.True(bubble.IsEffectivelyVisible);
                Assert.True(bubble.Bounds.Width > 0 && bubble.Bounds.Height > 0);
                Assert.Contains("收到，我来处理。", RenderedText(view));

                var inputCard = view.FindControl<Border>("InputCard")!;
                var inputX = inputCard.TranslatePoint(new Point(0, 0), view)!.Value.X;
                var bubbleX = bubble.TranslatePoint(new Point(0, 0), view)!.Value.X;
                Assert.Equal(inputX, bubbleX, 1);
                Assert.Equal(inputCard.Bounds.Width, bubble.Bounds.Width, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AskUserWindow_MultiSelectReturnsCheckedOptions()
    {
        Ui.Value.Run(() =>
        {
            var store = new UserQuestionStore();
            var pending = store.CreateQuestions([
                new QuestionDef
                {
                    Header = "题材",
                    Question = "想要哪些元素？",
                    Options = ["修真", "悬疑", "日常"],
                    MultiSelect = true,
                },
            ])[0];
            var window = new Desktop.Controls.AskUserWindow(store, pending);
            window.Show();
            try
            {
                var checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(3, checks.Count);
                checks[0].IsChecked = true;
                checks[2].IsChecked = true;

                var confirm = window.FindControl<Button>("ConfirmButton")!;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(["修真", "日常"], store.Get(pending.Id)!.SelectedAnswers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VariantReviewWindow_ShowsWideFullWidthPreview()
    {
        Ui.Value.Run(() =>
        {
            var explore = new ExploreDetails
            {
                Variants =
                [
                    new VariantResult
                    {
                        Id = "a",
                        Label = "克制版",
                        Content = "# 变体 A\n\n第一段。",
                    },
                    new VariantResult
                    {
                        Id = "b",
                        Label = "强烈版",
                        Content = "# 变体 B\n\n第二段。",
                    },
                ],
            };
            var window = new Desktop.Controls.VariantReviewWindow(explore);
            window.Show();
            try
            {
                Pump();
                Assert.True(window.Width >= 960);
                var tabs = window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b => b.Classes.Contains("NavPill"))
                    .ToList();
                Assert.Equal(2, tabs.Count);

                var markdown = Assert.Single(
                    window.GetVisualDescendants().OfType<MarkdownRenderer>()
                );
                _output.WriteLine(
                    $"变体窗宽={window.Width:F1}，Markdown 宽={markdown.Bounds.Width:F1}"
                );
                Assert.True(markdown.Bounds.Width > 700);
                Assert.Contains("变体 A", RenderedText(window));

                var active = Assert.Single(tabs.Where(t => t.Classes.Contains("Active")));
                Assert.Equal(
                    Color.Parse("#33C9D5C5"),
                    ((ISolidColorBrush)active.Background!).Color
                );
                Assert.All(tabs, t => Assert.Equal(1.0, t.Opacity));
                Assert.All(tabs, t => Assert.Equal(13, ((TextBlock)t.Content!).FontSize));
                var card = Assert.Single(
                    window
                        .GetVisualDescendants()
                        .OfType<Border>()
                        .Where(b => b.Classes.Contains("Card"))
                );
                Assert.Equal(new Thickness(20), card.Padding);

                var heading = Assert.Single(
                    window
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Where(t => t.Classes.Contains("Heading1"))
                );
                Assert.Equal(26, heading.FontSize);
                Assert.Equal(Color.Parse("#2b390a"), ((ISolidColorBrush)heading.Foreground!).Color);
                var paragraph = Assert.Single(
                    window
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Where(t => t.Classes.Contains("ParagraphBlock"))
                );
                Assert.Equal(18, paragraph.FontSize);
                Assert.Equal(
                    Color.Parse("#0a1d08"),
                    ((ISolidColorBrush)paragraph.Foreground!).Color
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AskUserWindow_ConfirmReturnsSelectedOption()
    {
        Ui.Value.Run(() =>
        {
            var store = new UserQuestionStore();
            var pending = store.CreateQuestions([
                new QuestionDef
                {
                    Header = "方向",
                    Question = "想写哪种？",
                    Options = ["都市修真", "仙侠"],
                },
            ])[0];
            var window = new Desktop.Controls.AskUserWindow(store, pending);
            window.Show();
            try
            {
                // 两个选项 + 单选模式下的「自定义输入」
                var radios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
                Assert.Equal(3, radios.Count);
                Assert.All(
                    radios,
                    r => Assert.Equal(14, Assert.IsType<TextBlock>(r.Content).FontSize)
                );
                var question = window.FindControl<TextBlock>("QuestionText")!;
                Assert.Equal(16, question.FontSize);
                radios[1].IsChecked = true;

                var confirm = window.FindControl<Button>("ConfirmButton")!;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var answered = store.Get(pending.Id)!;
                Assert.True(answered.IsAnswered);
                Assert.Equal(["仙侠"], answered.SelectedAnswers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AskUserWindow_FewOptionsDoNotStretchOptionsCard()
    {
        Ui.Value.Run(() =>
        {
            static (Desktop.Controls.AskUserWindow Window, Border Card) Create(int count)
            {
                var store = new UserQuestionStore();
                var pending = store.CreateQuestions([
                    new QuestionDef
                    {
                        Header = "方向",
                        Question = "这次开篇想走哪种方向？",
                        Options = Enumerable.Range(1, count).Select(i => $"选项 {i}").ToArray(),
                    },
                ])[0];
                var window = new Desktop.Controls.AskUserWindow(store, pending);
                window.Show();
                Pump();
                var card = window
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .Single(b => b.Classes.Contains("Card"));
                return (window, card);
            }

            var (fewWindow, fewCard) = Create(4);
            try
            {
                Assert.True(
                    fewWindow.Bounds.Height < 420,
                    $"选项少时窗口应随内容收紧，实际 {fewWindow.Bounds.Height}"
                );
                Assert.True(
                    fewCard.Bounds.Height < 250,
                    $"选项 Card 不应撑满窗口，实际 {fewCard.Bounds.Height}"
                );
            }
            finally
            {
                fewWindow.Close();
            }

            var (manyWindow, manyCard) = Create(12);
            try
            {
                Assert.InRange(manyCard.Bounds.Height, 359, 361);
                Assert.True(manyWindow.Bounds.Height < 620);
            }
            finally
            {
                manyWindow.Close();
            }
        });
    }

    [Fact]
    public void LongUserMessage_UsesFullMessageWidth()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.User);
            message.Content.Append(
                string.Concat(
                    Enumerable.Repeat(
                        "这是一段很长的用户回复，用来验证用户消息不再被固定宽度截成半张卡。",
                        12
                    )
                )
            );

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();

                var inputCard = view.FindControl<Border>("InputCard")!;
                var bubble = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Border>()
                        .Where(border => border.Classes.Contains("UserBubble"))
                );
                var inputX = inputCard.TranslatePoint(new Point(0, 0), view)!.Value.X;
                var bubbleX = bubble.TranslatePoint(new Point(0, 0), view)!.Value.X;
                _output.WriteLine(
                    $"输入卡片 {inputX:F1}/{inputCard.Bounds.Width:F1}，用户气泡 {bubbleX:F1}/{bubble.Bounds.Width:F1}"
                );

                // 消息区有滚动条时会占 5px，左侧差不能作为满宽判据；用右缘对齐 + 绝对宽度。
                Assert.Equal(inputX + inputCard.Bounds.Width, bubbleX + bubble.Bounds.Width, 1);
                Assert.True(
                    bubble.Bounds.Width > 720,
                    $"用户气泡宽度 {bubble.Bounds.Width:F1} 仍受旧 MaxWidth=720 限制"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StepDetails_AreInlineWithoutCard_AndAlignWithInputCard()
    {
        Ui.Value.Run(() =>
        {
            var step = new AgentStep
            {
                Kind = StepKind.ToolCall,
                ToolName = "edit",
                Title = "编辑文件",
                Status = AgentStepStatus.Completed,
                ArgumentsSummary = "chapter.md",
                FullArguments = """{"path":"chapter.md"}""",
                ResultSummary = "已更新 chapter.md",
                FullResult = "已更新 chapter.md",
            };
            step.IsDetailsExpanded = true;

            var message = new ChatMessage(MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();

                var inputCard = view.FindControl<Border>("InputCard")!;
                var stepHost = view.GetVisualDescendants()
                    .OfType<StackPanel>()
                    .First(panel => panel.DataContext is AgentStep);
                var inputX = inputCard.TranslatePoint(new Point(0, 0), view)!.Value.X;
                var stepX = stepHost.TranslatePoint(new Point(0, 0), view)!.Value.X;
                var details = view.GetVisualDescendants()
                    .OfType<StackPanel>()
                    .Single(panel => panel.Classes.Contains("StepDetails"));
                _output.WriteLine(
                    $"输入卡片左缘={inputX}，步骤左缘={stepX}，详情宽度={details.Bounds.Width:F1}，输入宽度={inputCard.Bounds.Width:F1}"
                );

                Assert.Equal(inputX, stepX, 1);
                Assert.True(
                    details.Bounds.Width >= inputCard.Bounds.Width - 24,
                    $"详情宽度 {details.Bounds.Width:F1} 未铺满消息区（输入卡片 {inputCard.Bounds.Width:F1}）"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ToolStep_DetailsAreCollapsedUntilExpanded()
    {
        Ui.Value.Run(() =>
        {
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.ToolCall,
                ToolName = "edit",
                Title = "编辑文件",
                Status = Desktop.Models.AgentStepStatus.Completed,
                ArgumentsSummary = "chapter.md",
                FullArguments =
                    """{"path":"chapter.md","edits":[{"oldText":"旧句子","newText":"新句子"}]}""",
                ResultSummary = "chapter.md · +1 -1",
                FullResult =
                    "```diff\n--- a/chapter.md\n+++ b/chapter.md\n@@ -1,2 +1,2 @@\n-旧句子\n+新句子\n```",
            };
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var arguments = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text => text.DataContext == step && text.Text == step.ArgumentsSummary);
                _output.WriteLine($"步骤参数摘要宽度={arguments.Bounds.Width:F1}");
                Assert.True(
                    arguments.Bounds.Width > 600,
                    $"步骤参数摘要宽度 {arguments.Bounds.Width:F1} 仍受旧 MaxWidth 限制"
                );

                var collapsed = RenderedText(view);
                _output.WriteLine("工具步骤折叠态：");
                _output.WriteLine(collapsed);

                Assert.Contains("编辑文件", collapsed);
                Assert.Contains("chapter.md · +1 -1", collapsed);
                Assert.Contains("详情", collapsed);
                Assert.DoesNotContain("参数", collapsed);
                Assert.DoesNotContain("结果", collapsed);
                Assert.DoesNotContain("oldText", collapsed);

                step.IsDetailsExpanded = true;
                Pump();
                var expanded = RenderedText(view);
                _output.WriteLine("工具步骤展开态：");
                _output.WriteLine(expanded);

                Assert.Contains("收起", expanded);
                Assert.Contains("参数", expanded);
                Assert.Contains("结果", expanded);
                Assert.Contains("oldText", expanded);
                Assert.Contains("-旧句子", expanded);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ToolDiff_RendersTuiAlgorithmLinesWithRedGreenColors()
    {
        Ui.Value.Run(() =>
        {
            const string oldText = "保留\n旧句子";
            const string newText = "保留\n新句子";
            var details = new TestDiffDetails(
                "chapter.md",
                [new EditHunk { OldText = oldText, NewText = newText }]
            );

            var diff = ToolDiffBuilder.Build(details);
            Assert.NotNull(diff);

            var expectedLines = DiffComputer.Compute(oldText, newText);
            var (expectedAdded, expectedRemoved) = DiffComputer.CountChanges(expectedLines);
            Assert.Equal(expectedAdded, diff!.Added);
            Assert.Equal(expectedRemoved, diff.Removed);
            Assert.Equal("chapter.md · +1 -1", diff.Summary);
            Assert.Contains(diff.Lines, line => line is { IsDelete: true, Text: "旧句子" });
            Assert.Contains(diff.Lines, line => line is { IsInsert: true, Text: "新句子" });

            var step = new AgentStep
            {
                Kind = StepKind.ToolCall,
                ToolName = "edit",
                Title = "编辑文件",
                Status = AgentStepStatus.Completed,
                ArgumentsSummary = "chapter.md",
                FullArguments =
                    """{"path":"chapter.md","edits":[{"oldText":"保留\n旧句子","newText":"保留\n新句子"}]}""",
                ResultSummary = diff.Summary,
                FullResult = "原始结果不应出现",
            };
            step.ApplyDiff(diff.Lines, diff.Summary);
            step.IsDetailsExpanded = true;

            Assert.True(step.HasDiff);
            Assert.False(step.ShowRawResultDetail);

            var message = new ChatMessage(MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<ItemsControl>("MessageList")!;
            list.ItemsSource = new[] { message };
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var text = RenderedText(view);
                _output.WriteLine("工具结构化 diff 展开态：");
                _output.WriteLine(text);

                var diffTexts = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(t => t.DataContext is DiffLineView && t.IsEffectivelyVisible)
                    .ToList();

                var deleted = Assert.Single(diffTexts, t => t.Text == "旧句子");
                var inserted = Assert.Single(diffTexts, t => t.Text == "新句子");
                var deleteColor = ((ISolidColorBrush)deleted.Foreground!).Color;
                var insertColor = ((ISolidColorBrush)inserted.Foreground!).Color;

                _output.WriteLine($"删除行颜色：{deleteColor}，插入行颜色：{insertColor}");

                Assert.Equal(Color.Parse("#CC241D"), deleteColor);
                Assert.Equal(Color.Parse("#4E7E3E"), insertColor);
                Assert.Contains("DiffDelete", deleted.Classes);
                Assert.Contains("DiffInsert", inserted.Classes);

                var deletedRow = deleted.GetVisualAncestors().OfType<Border>().First();
                var insertedRow = inserted.GetVisualAncestors().OfType<Border>().First();
                Assert.Equal(
                    Color.Parse("#1AFB4934"),
                    ((ISolidColorBrush)deletedRow.Background!).Color
                );
                Assert.Equal(
                    Color.Parse("#1A8EC07C"),
                    ((ISolidColorBrush)insertedRow.Background!).Color
                );

                Assert.Contains("-", diffTexts.Select(t => t.Text));
                Assert.Contains("+", diffTexts.Select(t => t.Text));
                Assert.Contains("chapter.md · +1 -1", text);
                Assert.Contains("旧句子", text);
                Assert.Contains("新句子", text);
                Assert.DoesNotContain("原始结果不应出现", text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════
    //  动效运行期验收（与本类共用同一个 Ui harness：同进程只初始化一次 headless 平台）
    //
    //  关键约束：**必须断言中间态**。只断言终态时，动画根本没挂上也会恒真通过——
    //  终态 Opacity=1 / Translate.X=0 与「从未动画」的观感完全一致。
    //  因此断言「值已离开起点」：没接线时属性恒定在起点，接线生效时一定会被改写。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>翻页：Translate.X 从 ±10 → 0，必须出现严格介于其间的值。</summary>
    [Fact]
    public void PageEnter_SlidesHorizontallyThroughIntermediateValues()
    {
        var slides = new List<double>();

        Ui.Value.Run(() =>
        {
            var panel = new StackPanel { Children = { new TextBlock { Text = "page" } } };
            var window = new Window
            {
                Width = 240,
                Height = 140,
                Content = panel,
            };
            window.Show();
            Pump();

            // PlayPageEnter 是「复用同一视觉树」的显式播放入口：入树后调用即可
            _ = Desktop.Styles.Motion.PlayPageEnter(panel, 10);
            for (var i = 0; i < MotionFrames; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Thread.Sleep(MotionFrameMs);
                slides.Add((panel.RenderTransform as TranslateTransform)?.X ?? 0);
            }
            window.Close();
        });

        // 同上：headless 时钟有限，断言「离开起点 10」即证明滑入接线生效
        Assert.True(
            slides.Any(x => x < 9.8d),
            $"Translate.X 从未离开起点（翻页滑入没跑）：[{string.Join(",", slides.Select(x => x.ToString("F2")))}]"
        );
    }

    /// <summary>SukiUI 抢走 ListBoxItem 类型键主题后，行自身的 Background 过渡必须仍在，且行是扁平的。</summary>
    [Fact]
    public void ListRowItem_DeclaresBackgroundTransitionAtRuntime()
    {
        Ui.Value.Run(() =>
        {
            var list = new ListBox { ItemsSource = new[] { "aaa", "bbb" } };
            var window = new Window
            {
                Width = 300,
                Height = 200,
                Content = list,
            };
            window.Show();
            Pump();

            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                var hasBackground = item
                    .Transitions?.OfType<BrushTransition>()
                    .Any(t => t.Property == Border.BackgroundProperty);
                Assert.True(
                    hasBackground == true,
                    "ListBoxItem 自身缺少 Background 过渡（hover 薄雾会硬跳）"
                );

                // 扁平行：内边距与圆角必须归零（SukiUI 行主题自带 4,2,4,2 + 圆角高亮）
                Assert.Equal(new Thickness(0), item.Padding);
                Assert.Equal(new CornerRadius(0), item.CornerRadius);
            }

            window.Close();
        });
    }

    /// <summary>聚焦环：SukiUI 的 TextBox 模板没有 Border#PART_Border，目标必须指向真实命名元素。</summary>
    [Fact]
    public void TextBoxAdaline_FocusRingTargetsRealTemplateElement()
    {
        Ui.Value.Run(() =>
        {
            var box = new TextBox { Classes = { "Adaline" } };
            var window = new Window
            {
                Width = 300,
                Height = 120,
                Content = box,
            };
            window.Show();
            Pump();

            var names = box.GetVisualDescendants()
                .OfType<Border>()
                .Select(b => b.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            Assert.Contains("PART_ClipBorder", names);
            Assert.Contains("PART_BorderCardLight", names);
            Assert.DoesNotContain("PART_Border", names);

            window.Close();
        });
    }

    /// <summary>逐项入场：容器挂 Stagger 后，每行都要被播放一次，并收敛到可见。</summary>
    [Fact]
    public void Stagger_PlaysEnterForEachGeneratedRow()
    {
        Ui.Value.Run(() =>
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
            Desktop.Styles.Motion.SetStagger(list, true);
            var window = new Window
            {
                Width = 300,
                Height = 240,
                Content = list,
            };
            window.Show();
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Thread.Sleep(MotionFrameMs);
            }

            // headless 的动画时钟只推进有限帧（实测 2~3 帧后停住），无法断言收敛到 1；
            // 但每行的 Opacity 必须已离开 0——停在 0 意味着 Stagger 没接上、行不可见。
            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.NotEmpty(items);
            foreach (var item in items)
                Assert.True(
                    item.Opacity > 0.02d,
                    $"行入场未离开起点（Stagger 没接上，行会不可见）：Opacity={item.Opacity:F2}"
                );

            window.Close();
        });
    }

    /// <summary>
    /// 退场动效：MarkdownDialog 关闭时必须先取消 Closing（播淡出）、且最终一定关闭。
    /// 不断言 Opacity 采样——同进程其他动效测试共用 Motion 的静态动画表，
    /// 采样值会互相干扰；「Closing 被取消 + 最终关闭」才是稳定且有意义的回归信号
    /// （漏接退场 = 直接关没有淡出；Guard 写错 = 窗口卡住关不掉，比没动效更糟）。
    /// </summary>
    [Fact]
    public void MarkdownDialog_CancelsClosingForExitThenCloses()
    {
        Ui.Value.Run(() =>
        {
            var dialog = new Desktop.Controls.MarkdownDialog("测试", "# 标题\n\n正文");
            dialog.Show();
            Pump();

            var firstClosingCanceled = false;
            dialog.Closing += (_, e) =>
            {
                firstClosingCanceled |= e.Cancel;
            };

            dialog.Close();
            Pump();

            Assert.True(firstClosingCanceled, "Closing 未被取消：退场淡出没接上（关闭是硬切）");

            // 动画播完后窗口必须真的消失（Guard 失效时会卡在这里）
            for (var i = 0; i < 10 && dialog.IsVisible; i++)
                Pump();
            Assert.False(dialog.IsVisible, "退场动画后窗口未关闭");
        });
    }

    /// <summary>动效采样的帧数（headless 下动画时钟只推进有限帧，14 帧足够抓到「离开起点」）。</summary>
    /// <summary>
    /// SmoothScroll 惯性滚动主题接入后，ScrollViewer 必须仍是 AutoScrollItemsControl 认得的模板：
    /// 类型还是 ScrollViewer、名字还是 PART_ScrollViewer——否则流式自动跟随会静默失效
    /// （OnApplyTemplate 的 Find&lt;ScrollViewer&gt;("PART_ScrollViewer") 返回 null）。
    /// </summary>
    [Fact]
    public void SmoothScrollTheme_KeepsAutoScrollTemplateContract()
    {
        Ui.Value.Run(() =>
        {
            var list = new Desktop.Controls.AutoScrollItemsControl
            {
                ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string>
                {
                    "a",
                    "b",
                    "c",
                },
                // 与 ConversationView.axaml 同一份模板（含 PART_ScrollViewer 命名）
                Template =
                    new Avalonia.Controls.Templates.FuncControlTemplate<Desktop.Controls.AutoScrollItemsControl>(
                        (_, _) =>
                            new ScrollViewer
                            {
                                Name = "PART_ScrollViewer",
                                Content = new ItemsPresenter(),
                            }
                    ),
            };
            var window = new Window
            {
                Width = 300,
                Height = 200,
                Content = list,
            };
            window.Show();
            Pump();

            var sv = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            Assert.NotNull(sv);
            Assert.Equal("PART_ScrollViewer", sv!.Name);
            // SmoothScroll 主题不得把 ScrollViewer 换成别的类型，否则 AutoScrollItemsControl 失效
            Assert.IsType<Avalonia.Controls.ScrollViewer>(sv);

            window.Close();
        });
    }

    private const int MotionFrames = 14;

    /// <summary>动效采样的帧间隔（毫秒）。</summary>
    private const int MotionFrameMs = 20;

    private sealed record TestDiffDetails(string? Path, EditHunk[]? Hunks) : IDiffDetails;

    [Fact]
    public void TableAndDiffTogether_BothRenderInOneDocument()
    {
        Ui.Value.Run(() =>
        {
            var markdown = """
                修改结果：

                | 文件 | 变更 |
                |------|------|
                | chapter.md | +1 / -1 |

                ```diff
                -旧句子
                +新句子
                ```
                """;

            var (window, renderer) = Render(markdown);
            try
            {
                Assert.Single(renderer.GetVisualDescendants().OfType<MarkdownTableGrid>());
                Assert.Single(renderer.GetVisualDescendants().OfType<CodeBlock>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Window Window, MarkdownRenderer Renderer) Render(string markdown)
    {
        var renderer = new MarkdownRenderer
        {
            MarkdownBuilder = new ObservableStringBuilder(markdown),
        };
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = new ScrollViewer { Content = renderer },
        };
        window.Show();
        Pump();
        return (window, renderer);
    }

    /// <summary>LiveMarkdown 的渲染是异步批处理（MarkdownUpdateProducer 去抖），多泵几轮。</summary>
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static string RenderedText(Visual root) => string.Join("\n", VisibleTexts(root));

    private static List<string> VisibleTexts(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t is MarkdownTextBlock markdown ? markdown.ActualText : t.Text)
            .ToList();

    /// <summary>专用 Avalonia UI 线程：初始化 headless 平台，串行执行渲染动作。</summary>
    private sealed class HeadlessUiThread
    {
        private readonly BlockingCollection<Action> _work = [];
        private readonly ManualResetEventSlim _ready = new(false);
        private Exception? _initError;

        public HeadlessUiThread()
        {
            var thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Mythology.Desktop.Headless",
            };
            thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Avalonia headless UI 线程初始化超时");
            if (_initError is not null)
                throw new InvalidOperationException("Avalonia headless 初始化失败", _initError);
        }

        public void Run(Action action)
        {
            using var done = new ManualResetEventSlim(false);
            ExceptionDispatchInfo? error = null;

            _work.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("Avalonia headless 渲染动作超时");
            error?.Throw();
        }

        private void Pump()
        {
            try
            {
                // 用真实 App：加载 MythologyTheme/Styles + LiveMarkdown 默认样式，
                // 让 CodeBlock 等模板化控件也走生产主题，而不是空的测试 Application。
                AppBuilder
                    .Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                _initError = ex;
                _ready.Set();
                return;
            }

            _ready.Set();
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // 动作内部自行捕获断言异常；这里只防止 UI 线程退出。
                }
            }
        }
    }
}
