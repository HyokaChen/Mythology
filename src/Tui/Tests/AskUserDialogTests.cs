using System.Reflection;
using Core.Shared;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Rendering;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ask_user 对话框的真实 TUI 交互测试：
/// 多选必须使用 checkbox 列表，空格切换勾选，确认后一次提交所有选中项。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class AskUserDialogTests : TerminalUiFixture
{
    protected override TerminalSize Size => new(80, 24);

    protected override Visual CreateRoot() => new Markup("ask_user dialog test");

    protected override TerminalAppOptions CreateOptions() =>
        new() { HostKind = TerminalHostKind.Fullscreen };

    [Fact]
    public async Task MultiSelect_TogglesCheckedRows_AndSubmitsAllAnswers()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "方向",
                Question = "选择要保留的线索",
                Options = ["身世", "师门", "旧案"],
                MultiSelect = true,
            },
        ]);

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.Show(store, questions);
            return 0;
        });

        var selection = await WaitForValueAsync(
            () =>
                App!
                    .Root.EnumerateVisualsDepthFirst()
                    .OfType<SelectionList<string>>()
                    .FirstOrDefault(),
            "多选列表已渲染"
        );
        Assert.NotNull(selection);

        Assert.Same(selection, await OnUiAsync(() => App!.FocusedElement));
        Assert.Contains(
            await CaptureFrameAsync(),
            line => line.Contains('☐') && line.Contains("身世")
        );

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Space });
        await WaitForConditionAsync(() => selection!.Checked[0], "空格勾选第一项");

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Space });
        await WaitForConditionAsync(() => selection!.Checked[2], "空格勾选第三项");

        Assert.False(await OnUiAsync(() => selection!.Checked[1]), "未操作的第二项不应被勾选");
        Assert.Contains(
            await CaptureFrameAsync(),
            line => line.Contains('☑') && line.Contains("身世")
        );

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Tab });
        await WaitForConditionAsync(
            () =>
                App!.FocusedElement is Button button
                && button.Content is Markup markup
                && markup.Text.Trim() == "确认",
            "Tab 聚焦确认按钮"
        );
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var answered = await store
            .WaitForAnswerAsync(questions[0].Id)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["身世", "旧案"], answered);
        Assert.Equal(["身世", "旧案"], store.Get(questions[0].Id)!.SelectedAnswers);
    }

    [Fact]
    public async Task SingleSelect_EnterConfirmsSelectedRadioAnswer()
    {
        var store = new UserQuestionStore();
        var questions = store.CreateQuestions([
            new QuestionDef
            {
                Header = "视角",
                Question = "选择叙事视角",
                Options = ["第一人称", "第三人称"],
            },
        ]);

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.Show(store, questions);
            return 0;
        });

        var list = await WaitForValueAsync(
            () =>
                App!
                    .Root.EnumerateVisualsDepthFirst()
                    .OfType<RadioButtonList<string>>()
                    .FirstOrDefault(),
            "单选列表已渲染"
        );
        Assert.NotNull(list);
        Assert.Same(list, await OnUiAsync(() => App!.FocusedElement));

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var answered = await store
            .WaitForAnswerAsync(questions[0].Id)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["第三人称"], answered);
    }

    private async Task<IReadOnlyList<string>> CaptureFrameAsync()
    {
        await OnUiAsync(() =>
        {
            App!.RequestFullRender();
            return 0;
        });
        await Task.Delay(100);
        return await OnUiAsync(() =>
            (
                (CellBuffer)
                    typeof(TerminalApp)
                        .GetField("_renderBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(App)!
            ).ToMarkupLines()
        );
    }

    private async Task<T?> WaitForValueAsync<T>(Func<T?> read, string what)
        where T : class
    {
        await WaitForConditionAsync(() => read() is not null, what);
        return await OnUiAsync(read);
    }

    private async Task WaitForConditionAsync(Func<bool> read, string what)
    {
        await WaitUntilAsync(async () => await OnUiAsync(read), what);
    }
}
