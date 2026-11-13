using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Interactions;
using Core.Shared;
using Core.Tools;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Services;
using Desktop.Views;
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
                Assert.Contains(code.GetVisualDescendants().OfType<TextBlock>(), block =>
                    block.Text == "diff"
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
    public void CodeBlock_UsesAdalinePaperSurfaceAndMonoMetrics()
    {
        Ui.Value.Run(() =>
        {
            var markdown = """
                ```text
                已写：第1章(4793字)
                未写：第3章《银发转学生》
                ```
                """;

            var (window, renderer) = Render(markdown);
            try
            {
                var code = Assert.Single(renderer.GetVisualDescendants().OfType<CodeBlock>());
                var panel = Assert.Single(code.GetVisualDescendants().OfType<Border>(), border =>
                    border.Name == "PART_CodePanel"
                );
                var header = Assert.Single(code.GetVisualDescendants().OfType<Border>(), border =>
                    border.Name == "PART_CodeHeader"
                );
                var body = Assert.IsType<MarkdownTextBlock>(code.CodeTextBlock);

                _output.WriteLine(
                    $"code={code.Bounds} panel={panel.Bounds} header={header.Bounds} body={body.Bounds}"
                );
                _output.WriteLine(
                    $"font={body.FontFamily.Name} size={body.FontSize} line={body.LineHeight}"
                );

                Assert.Equal(Color.Parse("#fdfefb"), ((SolidColorBrush)panel.Background!).Color);
                Assert.Equal(Color.Parse("#e1e6df"), ((SolidColorBrush)panel.BorderBrush!).Color);
                Assert.Equal(new Thickness(1), panel.BorderThickness);
                Assert.Equal(new CornerRadius(0), panel.CornerRadius);

                Assert.Equal(Color.Parse("#e1e6df"), ((SolidColorBrush)header.BorderBrush!).Color);
                Assert.Contains("Fragment Mono", body.FontFamily.Name, StringComparison.Ordinal);
                Assert.Equal(11, body.FontSize);
                Assert.Equal(18, body.LineHeight);
                Assert.Equal(Color.Parse("#0a1d08"), ((SolidColorBrush)body.Foreground!).Color);

                Assert.NotNull(
                    code.GetVisualDescendants()
                        .OfType<Button>()
                        .SingleOrDefault(button => button.Name == "PART_CopyButton")
                );
                Assert.NotNull(
                    code.GetVisualDescendants()
                        .OfType<ToggleButton>()
                        .SingleOrDefault(button => button.Name == "PART_ToggleTextWrapButton")
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CodeBlockEstimator_MatchesRenderedFenceIncludingMargin()
    {
        Ui.Value.Run(() =>
        {
            var markdown = """
                ```csharp
                var x = 1;
                var y = 2;
                ```
                """;

            var (window, renderer) = Render(markdown);
            try
            {
                var code = Assert.Single(renderer.GetVisualDescendants().OfType<CodeBlock>());
                var rendered = code.Bounds.Height + code.Margin.Top + code.Margin.Bottom;
                var estimated = Desktop.Controls.MarkdownHeightEstimator.EstimateBlock(
                    markdown,
                    code.Bounds.Width
                );

                Assert.InRange(estimated, rendered - 0.01, rendered + 0.01);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InlineCode_UsesEucalyptusFieldNoteChip()
    {
        Ui.Value.Run(() =>
        {
            var (window, renderer) = Render("前文 `inline code` 后文");
            try
            {
                var block = Assert.Single(
                    renderer.GetVisualDescendants().OfType<MarkdownTextBlock>()
                );
                var inline = Assert.Single(block.Inlines.OfType<CodeInline>());

                Assert.Equal(Color.Parse("#c9d5c5"), ((SolidColorBrush)inline.Background!).Color);
                Assert.Equal(Color.Parse("#c5ccb6"), ((SolidColorBrush)inline.BorderBrush!).Color);
                Assert.Equal(Color.Parse("#0a1d08"), ((SolidColorBrush)inline.Foreground!).Color);
                Assert.Equal(1, inline.BorderThickness);
                Assert.Equal(new CornerRadius(4), inline.CornerRadius);
                Assert.Contains("Fragment Mono", inline.FontFamily.Name, StringComparison.Ordinal);
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
    public void ThinkingStep_StreamsFixedPreview_AndCanExpand()
    {
        Ui.Value.Run(() =>
        {
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.Thinking,
                Status = Desktop.Models.AgentStepStatus.Processing,
                IsDetailsExpanded = false,
            };
            step.ThinkingBuilder.Append("这是思考内容，流式中也必须直接可见。");
            step.NotifyThinkingChanged();

            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                _output.WriteLine("思考步骤流式态（固定预览）：");
                _output.WriteLine(streaming);

                Assert.Contains("思考中…", streaming);
                Assert.Contains("这是思考内容，流式中也必须直接可见。", streaming);
                Assert.Contains("展开", streaming);
                var preview = view.GetVisualDescendants()
                    .OfType<Desktop.Controls.StreamingReasoningPreview>()
                    .Single();
                Assert.Equal("这是思考内容，流式中也必须直接可见。", preview.Text);
                Assert.Equal(40, preview.Height);

                step.IsDetailsExpanded = true;
                Pump();
                var expanded = RenderedText(view);
                _output.WriteLine("思考步骤展开态：");
                _output.WriteLine(expanded);
                Assert.Contains("收起", expanded);
                Assert.Contains("这是思考内容，流式中也必须直接可见。", expanded);

                step.Status = Desktop.Models.AgentStepStatus.Completed;
                Pump();
                var completed = RenderedText(view);
                _output.WriteLine("思考步骤完成态：");
                _output.WriteLine(completed);
                Assert.Contains("思考过程", completed);
                Assert.Contains("这是思考内容，流式中也必须直接可见。", completed);
                Assert.DoesNotContain("思考中…", completed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 尾部跟随时展开步骤详情：内容长高会把视口追到新的底部，刚展开的正文反而
    /// 跑到视口上方。展开必须暂停跟随，并把详情第一行留在视口内，不能等用户滚动。
    /// </summary>
    [Fact]
    public void ExpandingStepDetailsAtTail_RevealsContentWithoutExtraScroll()
    {
        Ui.Value.Run(() =>
        {
            var messages = new List<ChatMessage>();
            for (var i = 0; i < 10; i++)
            {
                var message = new ChatMessage(MessageSender.Agent);
                for (var j = 0; j < 3; j++)
                {
                    var step = new AgentStep
                    {
                        Kind = StepKind.Thinking,
                        Status = AgentStepStatus.Completed,
                        Title = $"思考过程 {i}-{j}",
                    };
                    step.ThinkingBuilder.Append(
                        string.Join("\n", Enumerable.Repeat($"长思考 {i}-{j}。", 40))
                    );
                    step.NotifyThinkingChanged();
                    message.AgentSteps.Add(step);
                }
                message.IsProcessExpanded = true;
                messages.Add(message);
            }

            var view = new ConversationView();
            var list = view.FindControl<AutoScrollItemsControl>("MessageList")!;
            list.Rows = new ConversationRowCollection(messages.ToArray());
            list.IsVisible = true;

            var window = new Window
            {
                Width = 900,
                Height = 420,
                Content = view,
            };
            window.Show();
            try
            {
                Pump();
                var scroll = view.FindControl<ScrollViewer>("MessageScroll")!;
                scroll.Offset = scroll.Offset.WithY(scroll.Extent.Height);
                Pump();
                Assert.True(list.IsFollowing, "初始应处于尾部跟随");

                var header = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(text =>
                        text.DataContext is ConversationRow { Step: { Kind: StepKind.Thinking } }
                        && text.Text?.Contains("展开", StringComparison.Ordinal) == true
                    )
                    .Select(text => new
                    {
                        Text = text,
                        Click = text.TranslatePoint(
                            new Point(text.Bounds.Width / 2, text.Bounds.Height / 2),
                            window
                        ),
                        Top = text.TranslatePoint(new Point(0, 0), scroll),
                    })
                    .Where(item =>
                        item.Click is { } click
                        && item.Top is { } top
                        && top.Y >= 0
                        && top.Y < scroll.Viewport.Height
                    )
                    .OrderByDescending(item => item.Top!.Value.Y)
                    .FirstOrDefault();
                Assert.NotNull(header.Text);

                window.MouseMove(header.Click!.Value);
                window.MouseDown(header.Click.Value, MouseButton.Left);
                window.MouseUp(header.Click.Value, MouseButton.Left);
                Pump();

                var row = list
                    .Items.OfType<ConversationRow>()
                    .First(r => r.Step is { IsDetailsExpanded: true });
                Assert.True(row.Step!.IsDetailsExpanded);
                Assert.False(list.IsFollowing, "手动展开后必须暂停尾部跟随");
                var details = view.GetVisualDescendants()
                    .OfType<StackPanel>()
                    .SingleOrDefault(panel =>
                        panel.Classes.Contains("StepDetails")
                        && ReferenceEquals(panel.DataContext, row.Step)
                    );
                Assert.NotNull(details);
                var detailText = details!
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(t =>
                        t.Text?.StartsWith("长思考", StringComparison.Ordinal) == true
                    );
                Assert.NotNull(detailText);
                var detailPoint = detailText!.TranslatePoint(new Point(0, 0), scroll);
                Assert.NotNull(detailPoint);
                Assert.True(
                    detailPoint.Value.Y >= 0 && detailPoint.Value.Y < scroll.Viewport.Height,
                    $"展开的详情第一行不在视口内：y={detailPoint.Value.Y:0.0}, "
                        + $"viewport={scroll.Viewport.Height:0.0}, offset={scroll.Offset.Y:0.0}"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StreamingReasoning_KeepsConversationHeightBounded()
    {
        Ui.Value.Run(() =>
        {
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.Thinking,
                Status = Desktop.Models.AgentStepStatus.Processing,
                IsDetailsExpanded = false,
            };
            step.ThinkingBuilder.Append("开始思考。");
            step.NotifyThinkingChanged();
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var scroll = view.FindControl<ScrollViewer>("MessageScroll")!;
                var before = scroll.Extent.Height;

                for (var i = 0; i < 40; i++)
                    step.ThinkingBuilder.Append("模型继续推理，这一行不会改变预览高度。 ");
                step.NotifyThinkingChanged();
                Pump();

                Assert.InRange(scroll.Extent.Height - before, -2, 2);
                var preview = view.GetVisualDescendants()
                    .OfType<Desktop.Controls.StreamingReasoningPreview>()
                    .Single();
                Assert.Equal(step.ReasoningPreview, preview.Text);
                Assert.Equal(40, preview.Height);

                step.IsDetailsExpanded = true;
                Pump();
                var expandedBefore = scroll.Extent.Height;
                for (var i = 0; i < 100; i++)
                    step.ThinkingBuilder.Append("展开后继续推理，内部滚动而不是继续撑高对话。 ");
                step.NotifyThinkingChanged();
                Pump();
                Assert.InRange(scroll.Extent.Height - expandedBefore, -2, 330);
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var toolIndex = texts.FindIndex(t =>
                    t.Contains("搜索工具", StringComparison.Ordinal)
                );
                Assert.True(contentIndex >= 0, "中间正文未渲染");
                Assert.True(toolIndex >= 0, "工具步骤未渲染");
                Assert.True(
                    contentIndex < toolIndex,
                    $"中间正文必须位于工具步骤之前，实际 content={contentIndex}, tool={toolIndex}"
                );

                // 中间正文与最终流式正文同一张 Bone 卡：冻结到步骤列表时背景不再从
                // 黄色突变为无底色。文字仍用 14/24 轻量排版，不抢最终答复。
                var markdown = Assert.Single(
                    view.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.MarkdownRenderer>(),
                    m => m.Classes.Contains("StepContentMarkdown")
                );
                var body = markdown
                    .GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>()
                    .First();
                Assert.Equal(14, body.FontSize);
                Assert.Equal(24, body.LineHeight);
                Assert.Equal(new Thickness(0, 0, 0, 8), body.Margin);

                var contentCard = Assert.Single(body.GetVisualAncestors().OfType<Border>(), b =>
                    b.Classes.Contains("AgentBubble")
                );
                var cardBackground = Assert.IsType<SolidColorBrush>(contentCard.Background);
                Assert.Equal(Color.Parse("#EFF2E8"), cardBackground.Color);
                Assert.Equal(new Thickness(0, 4, 0, 4), contentCard.Margin);

                var toolTitle = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("搜索工具", StringComparison.Ordinal) == true
                );
                var bodyBottom = body.TranslatePoint(new Point(0, body.Bounds.Height), view)?.Y
                    ?? 0;
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
    public void NoticeStep_RendersBetweenToolSteps_AsCompactStatusRow()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent);
            message.AgentSteps.Add(
                new AgentStep
                {
                    Kind = StepKind.ToolCall,
                    Title = "情节线管理 A",
                    Status = AgentStepStatus.Completed,
                }
            );
            message.AgentSteps.Add(AgentStep.CreateNotice("📝 修改情节线「卷一」"));
            message.AgentSteps.Add(
                new AgentStep
                {
                    Kind = StepKind.ToolCall,
                    Title = "情节线管理 B",
                    Status = AgentStepStatus.Completed,
                }
            );

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var firstTitle = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text?.Contains("情节线管理 A", StringComparison.Ordinal) == true
                );
                var noticeText = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text == "📝 修改情节线「卷一」"
                    && t.IsEffectivelyVisible
                    && t.Classes.Contains("AgentStepNoticeText")
                );
                var secondTitle = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.Contains("情节线管理 B", StringComparison.Ordinal) == true
                );

                var firstY = firstTitle.TranslatePoint(new Point(0, 0), view);
                var noticeY = noticeText.TranslatePoint(new Point(0, 0), view);
                var secondY = secondTitle.TranslatePoint(new Point(0, 0), view);
                Assert.NotNull(firstY);
                Assert.NotNull(noticeY);
                Assert.NotNull(secondY);
                Assert.True(
                    firstY.Value.Y < noticeY.Value.Y,
                    $"提示必须排在第一个工具步骤之后：tool={firstY.Value.Y:F1}, notice={noticeY.Value.Y:F1}"
                );
                Assert.True(
                    noticeY.Value.Y < secondY.Value.Y,
                    $"提示必须排在下一个工具步骤之前：notice={noticeY.Value.Y:F1}, tool={secondY.Value.Y:F1}"
                );

                Assert.True(noticeText.IsEffectivelyVisible);
                Assert.Contains("SystemNoticeText", noticeText.Classes);
                Assert.Contains("AgentStepNoticeText", noticeText.Classes);
                Assert.Equal(13, noticeText.FontSize);
                Assert.Equal(20, noticeText.LineHeight);
                Assert.Equal(new Thickness(18, 1, 0, 1), noticeText.Margin);
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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

                var body = view.GetVisualDescendants()
                    .OfType<LiveMarkdown.Avalonia.MarkdownTextBlock>()
                    .First();
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var quote = Assert.Single(view.GetVisualDescendants().OfType<Border>(), b =>
                    b.Classes.Contains("QuoteBlock")
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b =>
                    b.Classes.Contains("QuoteBlock") || b.Classes.Contains("InterruptedNote")
                );

                var text = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text == "⏹ 中断生成"
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var text = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), t =>
                    t.Text == "📝 新建角色「测试」" && t.Classes.Contains("SystemNoticeText")
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

    [Fact]
    public void SystemEventError_UsesFullWidthAmberCard()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent) { IsProcessExpanded = true };
            message.AgentSteps.Add(
                AgentStep.CreateSystemEvent(
                    "⚠️ 调用出错：HTTP 429 · rate limit reached",
                    isError: true
                )
            );

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var card = Assert.Single(view.GetVisualDescendants().OfType<Border>(), border =>
                    border.Classes.Contains("SystemNoticeCard") && border.Classes.Contains("Error")
                );
                var background = Assert.IsType<SolidColorBrush>(card.Background);
                Assert.Equal(Color.Parse("#80581C"), background.Color);
                Assert.Equal(0.13, background.Opacity, 2);
                Assert.Equal(new Thickness(1), card.BorderThickness);
                Assert.Equal(new CornerRadius(10), card.CornerRadius);
                Assert.True(card.Bounds.Width > 600, $"card width={card.Bounds.Width:0.0}");
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

    private static void PumpUntilRows(Desktop.Controls.AutoScrollItemsControl list, int count)
    {
        for (var i = 0; i < 100 && list.Items.Count < count; i++)
            PumpOnce();
    }

    private static void PumpOnce()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(5);
    }

    [Fact]
    public void SystemProgressRow_ShowsSpinnerUntilCompletion()
    {
        Ui.Value.Run(() =>
        {
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.System)
            {
                IsSpinnerVisible = true,
            };
            message.AppendContent("上下文压缩中…");

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                Assert.Contains("上下文压缩中…", RenderedText(view));

                var spinner = Assert.Single(
                    view.GetVisualDescendants().OfType<Desktop.Controls.BusySpinner>(),
                    s =>
                        s.DataContext is Desktop.Models.ConversationRow { Message: var m }
                        && ReferenceEquals(m, message)
                );
                Assert.True(spinner.IsEffectivelyVisible);
                Assert.True(spinner.IsActive);

                message.IsSpinnerVisible = false;
                message.ResetContent();
                message.AppendContent("上下文压缩完成：202 → 20 条");
                Pump();

                var completedSpinner = Assert.Single(
                    view.GetVisualDescendants().OfType<Desktop.Controls.BusySpinner>(),
                    s =>
                        s.DataContext is Desktop.Models.ConversationRow { Message: var m }
                        && ReferenceEquals(m, message)
                );
                Assert.False(completedSpinner.IsVisible);
                Assert.False(completedSpinner.IsActive);
                Assert.Contains("上下文压缩完成：202 → 20 条", RenderedText(view));
            }
            finally
            {
                window.Close();
            }
        });
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                    s =>
                        s.DataContext is Desktop.Models.ConversationRow { Message: var m }
                        && ReferenceEquals(m, message)
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                Assert.Empty(list.Items);

                // 流式首批 chunk 常是换行/空格：不得因此渲染空白 Bone 卡。
                message.AppendContent("\n\n  ");
                Pump();
                Assert.Empty(list.Items);

                message.AppendContent("收到，我来处理。");
                PumpUntilRows(list, 1);
                Pump();

                var bubble = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Border>()
                        .Where(border => border.Classes.Contains("AgentBubble"))
                );
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
            var (hub, interaction, window) = CreateAskUserWindow([
                new QuestionDef
                {
                    Header = "题材",
                    Question = "想要哪些元素？",
                    Options = ["修真", "悬疑", "日常"],
                    MultiSelect = true,
                },
            ]);
            window.Show();
            try
            {
                var checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
                Assert.Equal(3, checks.Count);
                checks[0].IsChecked = true;
                checks[2].IsChecked = true;

                var confirm = window.FindControl<Button>("ConfirmButton")!;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(
                    ["修真", "日常"],
                    hub.Get(interaction.Id)!.Questions!.Items[0].Question.SelectedAnswers
                );
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
            var (hub, interaction, window) = CreateAskUserWindow([
                new QuestionDef
                {
                    Header = "方向",
                    Question = "想写哪种？",
                    Options = ["都市修真", "仙侠"],
                },
            ]);
            window.Show();
            try
            {
                // 两个选项 + 单选模式下的「自定义输入」
                var radios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
                Assert.Equal(3, radios.Count);
                Assert.All(radios, r =>
                    Assert.Equal(14, Assert.IsType<TextBlock>(r.Content).FontSize)
                );
                var question = window.FindControl<TextBlock>("QuestionText")!;
                Assert.Equal(16, question.FontSize);
                radios[1].IsChecked = true;

                var confirm = window.FindControl<Button>("ConfirmButton")!;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var answered = hub.Get(interaction.Id)!.Questions!.Items[0].Question;
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
                var (_, _, window) = CreateAskUserWindow([
                    new QuestionDef
                    {
                        Header = "方向",
                        Question = "这次开篇想走哪种方向？",
                        Options = Enumerable.Range(1, count).Select(i => $"选项 {i}").ToArray(),
                    },
                ]);
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                    .First(panel =>
                        panel.DataContext is Desktop.Models.ConversationRow { Step: not null }
                    );
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
    public void ToolStep_DetailsToggleClick_Expands()
    {
        Ui.Value.Run(() =>
        {
            var step = new Desktop.Models.AgentStep
            {
                Kind = Desktop.Models.StepKind.ToolCall,
                Title = "编辑文件",
                Status = Desktop.Models.AgentStepStatus.Completed,
                ArgumentsSummary = "chapter.md",
                FullArguments = """{"path":"chapter.md"}""",
                ResultSummary = "已更新 chapter.md",
                FullResult = "已更新 chapter.md",
            };
            var message = new Desktop.Models.ChatMessage(Desktop.Models.MessageSender.Agent);
            message.AgentSteps.Add(step);

            var view = new Desktop.Views.ConversationView();
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var toggle = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text =>
                        text.Text == "详情"
                        && text.DataContext is Desktop.Models.ConversationRow { Step: var stepRow }
                        && ReferenceEquals(stepRow, step)
                    );
                var point = toggle
                    .TranslatePoint(
                        new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2),
                        window
                    )!
                    .Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();

                Assert.True(step.IsDetailsExpanded);
                Assert.Contains(view.GetVisualDescendants().OfType<StackPanel>(), panel =>
                    panel.Classes.Contains("StepDetails") && panel.IsVisible
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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
                var rowTexts = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(text =>
                        text.DataContext is Desktop.Models.ConversationRow { Step: var rowStep }
                        && ReferenceEquals(rowStep, step)
                    )
                    .ToList();
                var title = Assert.Single(rowTexts, text => text.Text == step.Title);
                var arguments = Assert.Single(rowTexts, text => text.Text == step.ArgumentsSummary);
                _output.WriteLine($"步骤参数摘要宽度={arguments.Bounds.Width:F1}");
                Assert.True(
                    arguments.Bounds.Width > 600,
                    $"步骤参数摘要宽度 {arguments.Bounds.Width:F1} 仍受旧 MaxWidth 限制"
                );
                Assert.True(title.Bounds.Width > 0);

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

    /// <summary>
    /// 已完成过程行是滚动密集区：每行只保留图标、标题、参数、详情入口和结果预览；
    /// 不得为每行创建 Button、详情 ScrollViewer 或可见的思考预览。否则换窗时
    /// 会为几十行重复实例化整棵详情控件树，滚动明显掉帧。
    /// </summary>
    [Fact]
    public void SettledProcessRows_UseLeanHeaderWithoutPerRowDetailsChrome()
    {
        Ui.Value.Run(() =>
        {
            var message = new ChatMessage(MessageSender.Agent);
            for (var i = 0; i < 12; i++)
            {
                message.AgentSteps.Add(
                    new AgentStep
                    {
                        Kind = StepKind.ToolCall,
                        Title = $"工具 {i}",
                        Status = AgentStepStatus.Completed,
                        ArgumentsSummary = "chapter.md",
                        ResultSummary = "完成",
                    }
                );
            }
            message.IsProcessExpanded = true;

            var view = new ConversationView();
            var list = view.FindControl<AutoScrollItemsControl>("MessageList")!;
            list.Rows = new ConversationRowCollection(message);
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

                var stepRows = list
                    .Items.OfType<ConversationRow>()
                    .Where(row => row.Kind == ConversationRowKind.Step)
                    .ToList();
                Assert.Equal(12, stepRows.Count);
                Assert.All(stepRows, row =>
                {
                    var texts = view.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Count(text => ReferenceEquals(text.DataContext, row));
                    Assert.InRange(texts, 1, 8);
                });
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button =>
                    button.DataContext is ConversationRow { Kind: ConversationRowKind.Step }
                );
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<ScrollViewer>(), scroll =>
                    scroll.DataContext is AgentStep
                );
                Assert.DoesNotContain(
                    view.GetVisualDescendants()
                        .OfType<Desktop.Controls.StreamingReasoningPreview>(),
                    preview => preview.DataContext is ConversationRow && preview.IsVisible
                );
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
            var list = view.FindControl<Desktop.Controls.AutoScrollItemsControl>("MessageList")!;
            list.Rows = new Desktop.Models.ConversationRowCollection(message);
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

    /// <summary>翻页：Translate.X 从 ±10 → 0，必须出现严格介于其间的值（UI 线程回退分支）。</summary>
    [Fact]
    public void PageEnter_SlidesHorizontallyThroughIntermediateValues()
    {
        var slides = new List<double>();

        Ui.Value.Run(() =>
        {
            // 关掉合成器通道，确定性地走 UI 线程 RenderTransform 分支
            Desktop.Styles.Motion.CompositionMotionEnabled = false;
            try
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
            }
            finally
            {
                Desktop.Styles.Motion.CompositionMotionEnabled = true;
            }
        });

        // 同上：headless 时钟有限，断言「离开起点 10」即证明滑入接线生效
        Assert.True(
            slides.Any(x => x < 9.8d),
            $"Translate.X 从未离开起点（翻页滑入没跑）：[{string.Join(",", slides.Select(x => x.ToString("F2")))}]"
        );
    }

    /// <summary>
    /// 渲染线程通道契约：合成器可用时，位移只交给 Translation，绝不再建 RenderTransform
    /// （两个通道同时跑会让位移翻倍、并出现两个时钟抢同一视觉）。
    /// 无头绘制平台的合成器是空实现（连 Opacity 动画都不推进），位移过程值在此不可观测——
    /// 真机（Metal）才有时钟，可见性由真机冒烟覆盖。
    /// </summary>
    [Fact]
    public void PageEnter_CompositionChannel_DoesNotBuildRenderTransform()
    {
        var composited = false;
        ITransform? renderTransform = null;

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

            // 平台探测必须自己做（不能借生产的 TryRunTranslation：那样关掉生产通道就等于关掉本测试）
            composited = ProbeComposition(panel);
            if (composited)
            {
                _ = Desktop.Styles.Motion.PlayPageEnter(panel, 24);
                for (var i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                    Thread.Sleep(MotionFrameMs);
                }
            }
            // Visual 有线程亲和性：必须在 UI 线程内取属性，供 lambda 外断言
            renderTransform = panel.RenderTransform;
            window.Close();
        });

        if (!composited)
        {
            _output.WriteLine(
                "本平台没有可用合成器：渲染线程通道不可测（回退分支由 PageEnter_Slides… 覆盖）"
            );
            return;
        }

        Assert.Null(renderTransform);
    }

    /// <summary>读 TabControl 选中内容的横向 RenderTransform（无则以 0 计）。</summary>
    private static double SlideX(TabControl tabs) =>
        (tabs.SelectedContent as Visual)?.RenderTransform is TranslateTransform t ? t.X : 0;

    /// <summary>平台探测：合成器能否接受 Translation 关键帧动画（与生产实现无关，避免自我失效）。</summary>
    private static bool ProbeComposition(Visual visual)
    {
        try
        {
            var cv = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(visual);
            if (cv is null)
                return false;
            var probe = cv.Compositor.CreateVector3DKeyFrameAnimation();
            probe.Duration = TimeSpan.FromMilliseconds(1);
            probe.InsertKeyFrame(0f, default);
            probe.InsertKeyFrame(1f, default);
            cv.StartAnimation("Translation", probe);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>TabSwitch 附加属性：直接内容型 TabControl，翻页滑入必须真的播在选中内容上。</summary>
    [Fact]
    public void TabSwitch_DirectContent_SlidesResolvedVisual()
    {
        var slides = new List<double>();
        var start = 0d;
        var animated = false;

        Ui.Value.Run(() =>
        {
            Desktop.Styles.Motion.CompositionMotionEnabled = false;
            try
            {
                var tabs = new TabControl();
                tabs.Items.Add(
                    new TabItem
                    {
                        Header = "A",
                        Content = new Border { Width = 40, Height = 16 },
                    }
                );
                tabs.Items.Add(
                    new TabItem
                    {
                        Header = "B",
                        Content = new Border { Width = 40, Height = 16 },
                    }
                );
                Desktop.Styles.Motion.SetTabSwitch(tabs, true);
                var window = new Window
                {
                    Width = 240,
                    Height = 140,
                    Content = tabs,
                };
                window.Show();
                Pump(); // Loaded → Ready

                tabs.SelectedIndex = 1; // 0 → 1 前进，起点 +24
                // 无头时钟会把整段动画在单个调度周期内跑完，起手值抓不到；
                // 能确定抓到的证据是「选中内容上被创建了 TranslateTransform」（只可能来自翻页滑入）
                Dispatcher.UIThread.RunJobs();
                start = SlideX(tabs);
                animated = (tabs.SelectedContent as Visual)?.RenderTransform is TranslateTransform;
                for (var i = 0; i < MotionFrames; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                    Thread.Sleep(MotionFrameMs);
                    slides.Add(SlideX(tabs));
                }
                window.Close();
            }
            finally
            {
                Desktop.Styles.Motion.CompositionMotionEnabled = true;
            }
        });

        // 没接线则选中内容上不会出现任何 RenderTransform
        Assert.True(
            animated,
            $"Tab 翻页滑入没播（选中内容没有 TranslateTransform）：start={start:F2} samples=[{string.Join(",", slides.Select(x => x.ToString("F2")))}]"
        );
        // 滑入必须收敛回原位（停在偏移上就是残影）
        Assert.True(
            Math.Abs(slides[^1]) < 0.1d,
            $"Tab 翻页未收敛回 0：[{string.Join(",", slides.Select(x => x.ToString("F2")))}]"
        );
    }

    /// <summary>TabSwitch 数据模板分支：SelectedContent 是 VM 对象，必须解析到承载它的 ContentPresenter.Child。</summary>
    [Fact]
    public void TabSwitch_ResolvesTemplatedContentToPresenterChild()
    {
        Ui.Value.Run(() =>
        {
            var tabs = new TabControl
            {
                ItemsSource = new object[] { "A", "B" },
                SelectedIndex = 0,
            };
            var window = new Window
            {
                Width = 240,
                Height = 140,
                Content = tabs,
            };
            window.Show();
            Pump();

            var resolved = Desktop.Styles.Motion.ResolveTabContent(tabs);
            Assert.NotNull(resolved);
            Assert.IsAssignableFrom<Visual>(resolved);
            Assert.Contains(resolved, tabs.GetVisualDescendants().OfType<Visual>());
            // 必须命中内容宿主，而不是 TabItem 里的 Header 呈现器（后者会把动画播到标签文字上）
            Assert.Null(resolved.FindAncestorOfType<TabItem>());

            window.Close();
        });
    }

    /// <summary>窗口退场：首次 Closing 必须被取消（播淡出），且动画后一定真正关闭。</summary>
    [Fact]
    public void WindowExit_CancelsClosingThenCloses()
    {
        Ui.Value.Run(() =>
        {
            var window = new Window
            {
                Width = 200,
                Height = 120,
                Content = new Border(),
            };
            window.Show();
            Pump();

            var canceled = false;
            window.Closing += (_, e) => canceled |= e.Cancel;

            Desktop.Styles.Motion.AttachWindowMotion(window);
            window.Close();
            Pump();

            Assert.True(canceled, "Closing 未被取消：窗口退场淡出没接上（关闭是硬切）");
            // 退场动画靠渲染时钟推进：必须 tick，且用墙钟兜底（固定 pump 次数会随机器负载抖动）
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (window.IsVisible && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Thread.Sleep(10);
            }
            Assert.False(window.IsVisible, "退场动画后窗口未关闭");
        });
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
    /// App 级主题不得把 ScrollViewer 换成别的控件类型或改名：
    /// 类型还是 ScrollViewer、名字还是 PART_ScrollViewer——否则流式自动跟随会静默失效
    /// （OnApplyTemplate 的 Find&lt;ScrollViewer&gt;("PART_ScrollViewer") 返回 null）。
    /// </summary>
    [Fact]
    public void AutoScrollTemplateContract_KeepsNamedScrollViewer()
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
            // 主题不得把 ScrollViewer 换成别的类型，否则 AutoScrollItemsControl 失效
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

    private static (
        InteractionHub Hub,
        InteractionSnapshot Interaction,
        Desktop.Controls.AskUserWindow Window
    ) CreateAskUserWindow(QuestionDef[] definitions)
    {
        var hub = new InteractionHub();
        hub.CurrentContext = new InteractionContext("test", "s1");
        var interaction = hub.CreateQuestionSet(hub.CurrentContext, definitions);
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(interaction.Id, "test", out var claimed));
        var window = new Desktop.Controls.AskUserWindow(
            hub,
            interaction.Id,
            claimed!.Claim!.LeaseId,
            interaction.Questions!.Items[0]
        );
        return (hub, interaction, window);
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

    /// <summary>共享的 Avalonia headless UI 线程（进程内唯一 Application，见 <see cref="HeadlessUiHost"/>）。</summary>
    private sealed class HeadlessUiThread
    {
        public void Run(Action action) => HeadlessUiHost.Instance.Run(action);
    }
}
