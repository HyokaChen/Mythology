using System.Reflection;
using Core.Interactions;
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
        var (hub, interaction, lease) = CreateClaimed([
            new QuestionDef
            {
                Header = "方向",
                Question = "选择要保留的线索",
                Options = ["身世", "师门", "旧案"],
                MultiSelect = true,
            },
        ]);
        var slot = interaction.Questions!.Items[0];
        var questionId = slot.Question.Id;

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.ShowAsync(hub, interaction.Id, lease, [slot]);
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
        Assert.Contains(await CaptureFrameAsync(), line =>
            line.Contains('☐') && line.Contains("身世")
        );

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Space });
        await WaitForConditionAsync(() => selection!.Checked[0], "空格勾选第一项");

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Space });
        await WaitForConditionAsync(() => selection!.Checked[2], "空格勾选第三项");

        Assert.False(await OnUiAsync(() => selection!.Checked[1]), "未操作的第二项不应被勾选");
        Assert.Contains(await CaptureFrameAsync(), line =>
            line.Contains('☑') && line.Contains("身世")
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

        var answered = await hub.WaitForQuestionAnswerAsync(interaction.Id, questionId)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["身世", "旧案"], answered);
        Assert.Equal(
            ["身世", "旧案"],
            hub.Get(interaction.Id)!.Questions!.Items[0].Question.SelectedAnswers
        );
    }

    [Fact]
    public async Task SingleSelect_EnterConfirmsSelectedRadioAnswer()
    {
        var (hub, interaction, lease) = CreateClaimed([
            new QuestionDef
            {
                Header = "视角",
                Question = "选择叙事视角",
                Options = ["第一人称", "第三人称"],
            },
        ]);
        var slot = interaction.Questions!.Items[0];
        var questionId = slot.Question.Id;

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.ShowAsync(hub, interaction.Id, lease, [slot]);
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
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var answered = await hub.WaitForQuestionAnswerAsync(interaction.Id, questionId)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["第三人称"], answered);
    }

    [Fact]
    public async Task SingleSelect_EnterOnCustomOption_OpensInputAndSubmitsTypedAnswer()
    {
        var (hub, interaction, lease) = CreateClaimed([
            new QuestionDef
            {
                Header = "方向",
                Question = "选择方向",
                Options = ["都市修真", "仙侠"],
            },
        ]);
        var slot = interaction.Questions!.Items[0];
        var questionId = slot.Question.Id;

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.ShowAsync(hub, interaction.Id, lease, [slot]);
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
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var input = await WaitForValueAsync(
            () => App!.FocusedElement as MaskedInput,
            "Enter 打开并聚焦自定义输入框"
        );
        Assert.NotNull(input);

        await OnUiAsync(() =>
        {
            input!.Value = "赛博仙侠";
            return 0;
        });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var answered = await hub.WaitForQuestionAnswerAsync(interaction.Id, questionId)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["赛博仙侠"], answered);
    }

    [Fact]
    public async Task MultipleQuestions_ShareOneDialog_AndSubmitTogether()
    {
        var (hub, interaction, lease) = CreateClaimed([
            new QuestionDef
            {
                Header = "方向",
                Question = "选择方向",
                Options = ["都市修真", "仙侠"],
            },
            new QuestionDef
            {
                Header = "视角",
                Question = "选择叙事视角",
                Options = ["第一人称", "第三人称"],
            },
            new QuestionDef
            {
                Header = "线索",
                Question = "选择要保留的线索",
                Options = ["身世", "旧案"],
                MultiSelect = true,
            },
        ]);
        var items = interaction.Questions!.Items;

        await OnUiAsync(() =>
        {
            _ = AskUserDialog.ShowAsync(hub, interaction.Id, lease, items);
            return 0;
        });

        await WaitForConditionAsync(
            () =>
                App!.Root.EnumerateVisualsDepthFirst().OfType<RadioButtonList<string>>().Count()
                    == 2
                && App.Root.EnumerateVisualsDepthFirst().OfType<SelectionList<string>>().Any(),
            "三道题渲染在同一个对话框"
        );
        var singleLists = await OnUiAsync(() =>
            App!.Root.EnumerateVisualsDepthFirst().OfType<RadioButtonList<string>>().ToList()
        );
        var multiList = await OnUiAsync(() =>
            App!.Root.EnumerateVisualsDepthFirst().OfType<SelectionList<string>>().Single()
        );
        var titles = await OnUiAsync(() =>
            App!
                .Root.EnumerateVisualsDepthFirst()
                .OfType<Markup>()
                .Select(markup => markup.Text)
                .ToList()
        );
        Assert.Contains(titles, title => title.Contains("方向"));
        Assert.Contains(titles, title => title.Contains("视角"));
        Assert.Contains(titles, title => title.Contains("线索"));

        Assert.Same(singleLists[0], await OnUiAsync(() => App!.FocusedElement));

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
        await WaitForConditionAsync(
            () => App!.FocusedElement == singleLists[1],
            "Enter 跳到未答的第二题"
        );

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Down });
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
        await WaitForConditionAsync(
            () => App!.FocusedElement == multiList,
            "Enter 跳到未答的多选题"
        );

        var frame = await CaptureFrameAsync();
        Assert.Contains(frame, line => line.Contains("线索"));

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Space });
        await WaitForConditionAsync(() => multiList.Checked[0], "空格勾选第一项");

        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Tab });
        await WaitForConditionAsync(
            () =>
                App!.FocusedElement is Button button
                && button.Content is Markup markup
                && markup.Text.Trim() == "确认",
            "Tab 聚焦确认按钮"
        );
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });

        var answers = await Task.WhenAll(
                items.Select(item =>
                    hub.WaitForQuestionAnswerAsync(interaction.Id, item.Question.Id)
                )
            )
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["都市修真"], answers[0]);
        Assert.Equal(["第三人称"], answers[1]);
        Assert.Equal(["身世"], answers[2]);
    }

    private static (
        InteractionHub Hub,
        InteractionSnapshot Interaction,
        string LeaseId
    ) CreateClaimed(QuestionDef[] definitions)
    {
        var hub = new InteractionHub();
        hub.CurrentContext = new InteractionContext("test", "s1");
        var interaction = hub.CreateQuestionSet(hub.CurrentContext, definitions);
        Assert.Equal(SubmitStatus.Accepted, hub.TryClaim(interaction.Id, "test", out var claimed));
        return (hub, interaction, claimed!.Claim!.LeaseId);
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
