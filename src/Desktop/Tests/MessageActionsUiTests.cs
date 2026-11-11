using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Controls;
using Desktop.Models;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 消息动作的进程内渲染冒烟：内联编辑替换气泡、hover 才启用动作条、
/// 助手正文的「重试」只在回合结束后出现。
/// </summary>
public sealed class MessageActionsUiTests
{
    [Fact]
    public void EditingUserMessage_SwapsBubbleForInlineInput()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var message = new ChatMessage(MessageSender.User);
            message.Content.Append("原始问题");
            message.EditDraft = "原始问题";
            message.IsEditing = true;

            var view = CreateView(new ConversationRowCollection(message));
            var window = Show(view);
            try
            {
                Pump();

                var editor = view.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(box => box.Name == "UserEditBox");
                Assert.True(editor.IsEffectivelyVisible);
                Assert.Equal("原始问题", editor.Text);

                var bubble = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("UserBubble"));
                Assert.False(bubble.IsEffectivelyVisible);

                var send = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button =>
                        button.DataContext is ConversationRow
                        && button
                            .GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(text => text.Text == "发送")
                );
                Assert.True(send.IsEffectivelyVisible);
                Assert.Contains(
                    send.GetVisualDescendants(),
                    visual => visual is Lucide.Avalonia.LucideIcon
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HoveringMessage_EnablesItsActionBarOnly()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var user = new ChatMessage(MessageSender.User);
            user.Content.Append("问题");
            var agent = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
            agent.AppendContent("回答");

            var view = CreateView(new ConversationRowCollection(user, agent));
            var window = Show(view);
            try
            {
                Pump();

                var bars = view.GetVisualDescendants().OfType<MessageActionBar>().ToList();
                Assert.Equal(2, bars.Count);
                var userBar = bars.Single(bar =>
                    bar.DataContext is ConversationRow { Kind: ConversationRowKind.User }
                );
                var agentBar = bars.Single(bar =>
                    bar.DataContext is ConversationRow { Kind: ConversationRowKind.Actions }
                );
                Assert.False(userBar.IsHitTestVisible);
                Assert.False(agentBar.IsHitTestVisible);

                var bubble = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("UserBubble"));
                var text = Assert.Single(
                    bubble.GetVisualDescendants().OfType<SelectableTextBlock>()
                );
                Assert.Equal(15, text.FontSize);
                var foreground = Assert.IsType<SolidColorBrush>(text.Foreground);
                Assert.Equal(Color.Parse("#F8F9F5"), foreground.Color);

                var point = bubble.TranslatePoint(
                    new Point(bubble.Bounds.Width / 2, bubble.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(point);

                window.MouseMove(point.Value);
                Pump();
                Assert.True(userBar.IsHitTestVisible);
                Assert.False(agentBar.IsHitTestVisible);

                var agentBubble = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("AgentBubble"));
                var agentPoint = agentBubble.TranslatePoint(
                    new Point(agentBubble.Bounds.Width / 2, agentBubble.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(agentPoint);
                window.MouseMove(agentPoint.Value);
                Pump();
                Assert.False(userBar.IsHitTestVisible);
                Assert.True(agentBar.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AssistantActionBar_ShowsRetryOnlyWhenTurnCompleted()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var agent = new ChatMessage(MessageSender.Agent);
            agent.AppendContent("回答");
            var view = CreateView(new ConversationRowCollection(agent));
            var window = Show(view);
            try
            {
                Pump();

                Assert.Empty(view.GetVisualDescendants().OfType<MessageActionBar>());

                agent.IsCompleted = true;
                Pump();

                var bar = Assert.Single(view.GetVisualDescendants().OfType<MessageActionBar>());
                Assert.True(bar.ShowCopy);
                Assert.True(bar.ShowRetry);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InterruptedAssistant_KeepsContentCardAndRetryCopyBar()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var agent = new ChatMessage(MessageSender.Agent)
            {
                IsCompleted = true,
                IsInterrupted = true,
            };
            agent.AppendContent("已经生成的部分回答");
            var view = CreateView(new ConversationRowCollection(agent));
            var window = Show(view);
            try
            {
                Pump();

                var bubble = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("AgentBubble"));
                Assert.True(bubble.Bounds.Height > 0);

                var bar = Assert.Single(view.GetVisualDescendants().OfType<MessageActionBar>());
                Assert.True(bar.ShowCopy);
                Assert.True(bar.ShowRetry);

                var interrupted = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text => text.Text == "⏹ 中断生成");
                Assert.True(interrupted.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InterruptedAssistantWithoutContent_StillHasRetryCopyActionsRow()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var agent = new ChatMessage(MessageSender.Agent)
            {
                IsCompleted = true,
                IsInterrupted = true,
            };
            var user = new ChatMessage(MessageSender.User);
            user.Content.Append("问题");
            var view = CreateView(new ConversationRowCollection(user, agent));
            var window = Show(view);
            try
            {
                Pump();

                var bar = Assert.Single(
                    view.GetVisualDescendants().OfType<MessageActionBar>(),
                    candidate => candidate.ShowRetry
                );
                var userBar = Assert.Single(
                    view.GetVisualDescendants().OfType<MessageActionBar>(),
                    candidate =>
                        candidate.DataContext is ConversationRow { Kind: ConversationRowKind.User }
                );
                Assert.True(bar.ShowCopy);
                Assert.True(bar.ShowRetry);
                Assert.False(bar.IsHitTestVisible);

                var interrupted = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text => text.Text == "⏹ 中断生成");
                Assert.True(interrupted.IsEffectivelyVisible);
                var point = interrupted.TranslatePoint(
                    new Point(interrupted.Bounds.Width / 2, interrupted.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(point);

                window.MouseMove(point.Value);
                Pump();
                Assert.True(agent.IsPointerOverMessage);
                Assert.True(bar.IsHitTestVisible);

                var userBubble = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("UserBubble"));
                var userPoint = userBubble.TranslatePoint(
                    new Point(userBubble.Bounds.Width / 2, userBubble.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(userPoint);
                window.MouseMove(userPoint.Value);
                Pump();
                Assert.False(bar.IsHitTestVisible);
                Assert.True(userBar.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HoveringThinkingStep_RevealsMessageRetryCopy()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var agent = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
            var step = new AgentStep
            {
                Kind = StepKind.Thinking,
                Status = AgentStepStatus.Completed,
            };
            step.ThinkingBuilder.Append("思考一下");
            step.NotifyThinkingChanged();
            agent.AgentSteps.Add(step);

            var view = CreateView(new ConversationRowCollection(agent));
            var window = Show(view);
            try
            {
                Pump();

                var bar = Assert.Single(
                    view.GetVisualDescendants().OfType<MessageActionBar>(),
                    candidate => candidate.ShowRetry
                );
                Assert.False(bar.IsHitTestVisible);

                var title = view.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(text => text.Text == "思考过程");
                var point = title.TranslatePoint(
                    new Point(title.Bounds.Width / 2, title.Bounds.Height / 2),
                    window
                );
                Assert.NotNull(point);

                window.MouseMove(point.Value);
                Pump();
                Assert.True(agent.IsPointerOverMessage);
                Assert.True(bar.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ConversationView CreateView(ConversationRowCollection rows)
    {
        var view = new ConversationView();
        var list = view.FindControl<AutoScrollItemsControl>("MessageList")!;
        list.Rows = rows;
        list.IsVisible = true;
        return view;
    }

    private static Window Show(ConversationView view)
    {
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = view,
        };
        window.Show();
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(2);
        }
    }
}
