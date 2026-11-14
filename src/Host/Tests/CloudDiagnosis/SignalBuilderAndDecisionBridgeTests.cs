using CloudDiagnosis;
using Core.Cloud;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

public sealed class SignalBuilderTests
{
    private static DiagnosisFinding Finding(
        string category = "角色",
        string severity = "high",
        string action = "fix-now"
    ) =>
        new(
            "f_0123456789abcdef",
            category,
            severity,
            severity == "high" ? "严重"
                : severity == "medium" ? "中等"
                : "轻微",
            action,
            action == "fix-now" ? "直接修改"
                : action == "suggest" ? "建议修改"
                : "只报告",
            "第 2 章 · 第 3 段",
            "主角在本章没有做出主动选择。",
            "他站在门口，看着两个人争吵。",
            "让主角做一个会改变局面的选择。"
        );

    private static DiagnosisReport Report(
        string action = "revise",
        IReadOnlyList<DiagnosisFinding>? findings = null,
        string? decisionQuestion = null,
        IReadOnlyList<string>? decisionOptions = null
    ) =>
        new(
            action == "proceed" ? "通过"
                : action == "ask-author" ? "需要作者决定"
                : "需要修改",
            action,
            "主角连续两章没有主动决策。",
            82,
            findings ?? [Finding()],
            decisionQuestion,
            decisionOptions,
            null,
            null
        );

    [Fact]
    public void Format_Proceed_OmitsFindingList()
    {
        var text = SignalBuilder.Format(Report("proceed", []), []);

        Assert.Contains("结果：通过", text);
        Assert.DoesNotContain("问题 1", text);
    }

    [Fact]
    public void Format_Revise_UsesOnlyAuthorFacingChinese()
    {
        var text = SignalBuilder.Format(Report(), []);

        Assert.Contains("结果：需要修改", text);
        Assert.Contains("检查：云端诊断", text);
        Assert.Contains("问题 1（角色 · 严重）", text);
        Assert.DoesNotContain("A-1", text);
        Assert.DoesNotContain("status=", text);
        Assert.DoesNotContain("story.check", text);
        Assert.DoesNotContain("云端故事检查", text);
        Assert.DoesNotContain("f_0123456789abcdef", text);
    }

    [Fact]
    public void HumanizerUnavailableMessage_DoesNotLeakClassName()
    {
        var text = SignalBuilder.HumanizerUnavailableMessage;

        Assert.Contains("写作风格规范", text);
        Assert.DoesNotContain("HumanizerPrompt", text);
    }

    [Fact]
    public void Format_AskAuthor_EmitsQuestionAndOptions()
    {
        var text = SignalBuilder.Format(
            Report(
                "ask-author",
                [],
                "要按建议继续，还是先修改本章？",
                ["按建议继续", "先修改本章"]
            ),
            []
        );

        Assert.Contains("需要作者决定", text);
        Assert.Contains("要按建议继续，还是先修改本章？", text);
        Assert.Contains("1. 按建议继续", text);
        Assert.Contains("2. 先修改本章", text);
    }

    [Fact]
    public void Format_LocalFinding_DoesNotLeakTrigger()
    {
        var local = Finding() with
        {
            Id = "",
            Trigger = "A-3",
            Action = "suggest",
            ActionLabel = "建议修改",
        };
        var text = SignalBuilder.Format(Report("revise", []), [local]);

        Assert.Contains("问题 1（角色 · 严重）", text);
        Assert.DoesNotContain("A-3", text);
    }
}

public sealed class HumanDecisionBridgeTests
{
    private static DiagnosisReport Decision =>
        new(
            "需要作者决定",
            "ask-author",
            "本章质量未达到自动推进要求。",
            38,
            [],
            "要按建议继续，还是先修改本章？",
            ["按建议继续", "先修改本章"],
            null,
            null
        );

    [Fact]
    public void ShouldSkipDecision_ThresholdsAreHonored()
    {
        Assert.Null(HumanDecisionBridge.ShouldSkipDecision(Decision, 5, 6));
        var characterSkip = HumanDecisionBridge.ShouldSkipDecision(
            Decision,
            5,
            6,
            null,
            minCharacters: 6
        );
        Assert.Contains("5 个角色", characterSkip);
        var orderSkip = HumanDecisionBridge.ShouldSkipDecision(
            Decision,
            5,
            6,
            null,
            minChapterOrder: 10
        );
        Assert.Contains("前 10 章", orderSkip);
    }

    [Fact]
    public async Task DecideAsync_UsesReportQuestionAndPersistsAnswer()
    {
        var store = new DecisionTestStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);

        var task = bridge.DecideAsync(Decision, CancellationToken.None);
        for (var i = 0; i < 100 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);

        var pending = Assert.Single(store.GetPending());
        Assert.Equal("要按建议继续，还是先修改本章？", pending.Question);
        Assert.Equal(["按建议继续", "先修改本章"], pending.Options);

        Assert.True(store.Answer(pending.Id, ["按建议继续"]));
        var verdict = await task;

        Assert.StartsWith("[人工裁决]", verdict);
        Assert.Contains("结论：本章质量未达到自动推进要求。", verdict);
        Assert.DoesNotContain("repairIntent", verdict);
        Assert.Single(bridge.DecisionHistory);
    }

    [Fact]
    public async Task DecideAsync_SameChapterQuestion_ReusesAnswer()
    {
        var store = new DecisionTestStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);

        var firstTask = bridge.DecideAsync(Decision, CancellationToken.None, "n1", chapterOrder: 8);
        for (var i = 0; i < 100 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        var firstPending = Assert.Single(store.GetPending());
        Assert.True(store.Answer(firstPending.Id, ["先修改本章"]));
        await firstTask;

        // 同一章、同一问题：不再创建第二张问题，只注入继续文本。
        var repeated = await bridge.DecideAsync(
            Decision,
            CancellationToken.None,
            "n1",
            chapterOrder: 8
        );
        Assert.Contains("不再重复询问", repeated);
        Assert.Contains("先修改本章", repeated);
        Assert.Empty(store.GetPending());
        Assert.Single(bridge.DecisionHistory);

        // 换章后是新的决策，不继承上一章的回答。
        var nextTask = bridge.DecideAsync(Decision, CancellationToken.None, "n1", chapterOrder: 9);
        for (var i = 0; i < 100 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        var nextPending = Assert.Single(store.GetPending());
        Assert.True(store.Answer(nextPending.Id, ["先修改本章"]));
        await nextTask;
    }

    [Fact]
    public async Task TryAutoContinue_RequiresPriorAuthorization()
    {
        var store = new DecisionTestStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);

        Assert.Null(bridge.TryAutoContinue(Decision, "n1"));

        var task = bridge.DecideAsync(Decision, CancellationToken.None, "n1");
        for (var i = 0; i < 100 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        store.Answer(store.GetPending()[0].Id, ["按建议继续"]);
        await task;

        var auto = bridge.TryAutoContinue(Decision, "n1");
        Assert.NotNull(auto);
        Assert.Contains("沿用上次", auto);
    }
}
