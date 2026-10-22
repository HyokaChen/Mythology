using System.Text.Json;
using CloudDiagnosis;
using Core.Cloud;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// HARNESS-CLOUD §10 P1-F1 验收：§4.2 规则表逐条 stub JSON 命中/不命中、
/// camelCase 断言、封闭集合外维度 Warnings 透传、三段式「证据/处方/边界」格式。
/// </summary>
public sealed class SignalRulesTests
{
    /// <summary>中性事实基座：不触发任何本地规则；各用例按需 with 注入专属数据。</summary>
    private static readonly LocalFacts Neutral = new()
    {
        ProtagonistName = "林云",
        CurrentOrder = 10,
        MaxChapterOrder = 10,
        OpenPlotlines =
        [
            new PlotlineFact("线一", PlannedRevealOrder: 20),
            new PlotlineFact("线二", PlannedRevealOrder: 40),
            new PlotlineFact("线三", PlannedRevealOrder: null),
        ],
        ActiveInfoGaps = 1,
    };

    /// <summary>含未到期暗线与在场配角的常规事实（A-3 不命中）。</summary>
    private static readonly LocalFacts Facts = new()
    {
        ProtagonistName = "林云",
        CurrentOrder = 10,
        MaxChapterOrder = 10,
        OpenPlotlines = [new PlotlineFact("师门血案", PlannedRevealOrder: 25)],
        ActiveInfoGaps = 2,
    };

    private static HarnessResult ContextRetrieve(string json) =>
        new()
        {
            Capability = "fiction.context.retrieve",
            Result = JsonSerializer.Deserialize<JsonElement>(json),
        };

    private static HarnessResult StoryCheck(string json) =>
        new()
        {
            Capability = "fiction.story.check",
            Result = JsonSerializer.Deserialize<JsonElement>(json),
        };

    [Fact]
    public void ParseContextRetrieve_NormalizesScore_AndParsesSignalsAndDimensionScores()
    {
        var outcome = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """
                {"features":{"overall-score":0.73},
                 "signals":[{"dimension":"代入感","severity":"high","description":"主角行为不可理喻",
                   "suggestion":"补充动机铺垫","paragraph-index":3,"impact-score":88}],
                 "dimension-scores":{"代入感":55,"节奏":80}}
                """
            )
        );

        Assert.NotNull(outcome);
        Assert.Equal(73, outcome.OverallScore);
        var signal = Assert.Single(outcome.Signals);
        Assert.Equal("high", signal.Severity);
        Assert.Equal("补充动机铺垫", signal.Suggestion);
        Assert.Equal(88, signal.ImpactScore);
        Assert.Equal(2, outcome.DimensionScores.Count);
        Assert.True(outcome.DimensionScores.ContainsKey("代入感")); // 中文维度键
    }

    /// <summary>Q13（2026-08-28）：context.retrieve 的 features.absentCharacters 透传（root 路径）。</summary>
    [Fact]
    public void ParseContextRetrieve_ParsesAbsentCharactersFromRoot()
    {
        var outcome = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.7,"absent-characters":["冷月","青禾"]}}"""
            )
        );

        Assert.NotNull(outcome);
        Assert.Equal(["冷月", "青禾"], outcome.AbsentCharacters);
    }

    /// <summary>Q13：快照路径 ParseAbsentCharactersFromFeatures 直接读 features 对象。</summary>
    [Fact]
    public void ParseAbsentCharacters_FromFeatures_Direct()
    {
        var features = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            """{"absent-characters":["苏婉"],"overall-score":0.7}"""
        );

        Assert.Equal(["苏婉"], SignalBuilder.ParseAbsentCharactersFromFeatures(features));
        Assert.Empty(SignalBuilder.ParseAbsentCharactersFromFeatures(default));
    }

    [Fact]
    public void ParseStoryCheck_ParsesPageRankFlags()
    {
        var outcome = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"capability":"fiction.story.check",
                 "dimensions":{"pace":{"overall-score":72},
                   "network":{"character-page-rank":{"top-character":"苏若雪",
                     "protagonist-rank":4,"n1-protagonist-eclipsed":true,
                     "n2-network-concentrated":false}}}}
                """
            )
        );

        Assert.NotNull(outcome);
        Assert.NotNull(outcome.PageRank);
        Assert.Equal("苏若雪", outcome.PageRank.TopCharacter); // kebab JSON 键
        Assert.True(outcome.PageRank.N1ProtagonistEclipsed);
        Assert.False(outcome.PageRank.N2NetworkConcentrated);
    }

    // ── 规则层 ──

    [Fact]
    public void S1_HighSeveritySignal_TriggersPrescription()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.8},"signals":[{"dimension":"代入感","severity":"high","description":"说教过多","suggestion":"改为对话展现"}]}"""
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        var s1 = Assert.Single(rules.Actions);
        Assert.StartsWith("S-1", s1.Code);
        Assert.Contains("改为对话展现", s1.Prescription);
    }

    [Fact]
    public void S1_MediumSeverity_DoesNotTrigger()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.8},"signals":[{"dimension":"节奏","severity":"medium","description":"中段平缓"}]}"""
            )
        );

        Assert.Empty(SignalRules.Evaluate(null, contextRetrieve, null, Neutral).Actions);
    }

    [Fact]
    public void A1_N1Flag_TriggersEclipseRule()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"network":{"character-page-rank":{"top-character":"苏若雪","n1-protagonist-eclipsed":true}}}}"""
            )
        );

        var action = SignalRules.Evaluate(null, null, storyCheck, Neutral).Actions.Single();

        Assert.Equal("A-1", action.Code);
        Assert.Contains("林云", action.Evidence);
        Assert.Contains("rewrite_selection", action.Prescription);
    }

    [Fact]
    public void A1_NullPageRank_SkipsRule()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck("""{"dimensions":{"pace":{"overall-score":90}}}""")
        );

        Assert.Empty(SignalRules.Evaluate(null, null, storyCheck, Neutral).Actions);
    }

    [Fact]
    public void A3_OverduePlotline_TriggersPayoffOrNurture()
    {
        var facts = Facts with { MaxChapterOrder = 26 }; // 计划揭示 25 章已过

        var action = SignalRules
            .Evaluate(null, null, null, facts)
            .Actions.Single(a => a.Code.StartsWith("A-3"));

        Assert.Contains("师门血案", action.Evidence);
        Assert.Contains("逾期 1 章", action.Evidence);
        Assert.Contains("回收", action.Prescription);
        Assert.Contains("养", action.Prescription);
    }

    [Fact]
    public void A3_NotYetDue_DoesNotTrigger()
    {
        Assert.DoesNotContain(
            SignalRules.Evaluate(null, null, null, Facts).Actions,
            a => a.Code.StartsWith("A-3")
        );
    }

    /// <summary>§6.3 埋设章龄锚点（2026-08-28 增强）：无计划揭示章但正文首现超 30 章 → A-3。</summary>
    [Fact]
    public void A3_NoPlannedReveal_ButFirstAppearOverdue_Triggers()
    {
        var facts = Facts with
        {
            MaxChapterOrder = 36,
            OpenPlotlines =
            [
                new PlotlineFact("暗流", PlannedRevealOrder: null, FirstAppearOrder: 5),
            ],
        };

        var action = SignalRules
            .Evaluate(null, null, null, facts)
            .Actions.Single(a => a.Code.StartsWith("A-3"));

        Assert.Contains("暗流", action.Evidence);
        Assert.Contains("自第 5 章", action.Evidence);
        Assert.Contains("31 章", action.Evidence);
        Assert.Contains("埋设章龄", action.Boundary);
    }

    /// <summary>埋设未超 30 章（且无计划揭示章）→ 不触发 A-3。</summary>
    [Fact]
    public void A3_NoPlannedReveal_FirstAppearNotOverdue_DoesNotTrigger()
    {
        var facts = Facts with
        {
            MaxChapterOrder = 30,
            OpenPlotlines =
            [
                new PlotlineFact("暗流", PlannedRevealOrder: null, FirstAppearOrder: 5),
            ],
        };

        Assert.DoesNotContain(
            SignalRules.Evaluate(null, null, null, facts).Actions,
            a => a.Code.StartsWith("A-3")
        );
    }

    [Fact]
    public void A6_LowPaceScore_TriggersRhythmRule()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"pace":{"overall-score":52,"issues":[{"description":"中段张力塌陷"}]}}}"""
            )
        );

        var a6 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-6");

        Assert.Contains("张力塌陷", a6.Evidence);
        Assert.Contains("节奏", a6.Prescription);
    }

    [Fact]
    public void A7_ImmersionIssues_TriggersApproachabilityRule()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"immersion":{"overall-score":58,"issues":[{"description":"主角无代价获益"}]}}}"""
            )
        );

        var a7 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-7");

        Assert.Contains("无代价获益", a7.Evidence);
        Assert.Contains("合理性", a7.Prescription);
    }

    [Fact]
    public void A4_PlotlineDensity_UnderflowAndOverflow()
    {
        // 下溢：仅 1 条开放暗线
        var under = SignalRules
            .Evaluate(null, null, null, Facts)
            .Actions.Single(a => a.Code == "A-4");
        Assert.Contains("埋新线", under.Prescription);

        // 上溢：7 条开放暗线
        var many = Enumerable.Range(1, 7).Select(i => new PlotlineFact($"线{i}", null)).ToArray();
        var over = SignalRules
            .Evaluate(null, null, null, Facts with { OpenPlotlines = many })
            .Actions.Single(a => a.Code == "A-4");
        Assert.Contains("过载", over.Prescription);
    }

    [Fact]
    public void A5_ZeroInfoGap_WithOpenPlotlines_TriggersLeakSuspicion()
    {
        var facts = Facts with { ActiveInfoGaps = 0 };

        var a5 = SignalRules.Evaluate(null, null, null, facts).Actions.Single(a => a.Code == "A-5");

        Assert.Contains("泄底", a5.Prescription);
        Assert.Contains("师门血案", a5.Prescription);
    }

    [Fact]
    public void A8_ChooseFocus_MapsFocusAreasWithoutHumanIntervention()
    {
        var scenePlan = new ScenePlanOutcome(
            CloudSignalKind.Action,
            65,
            "choose_focus",
            false,
            null,
            ["代入感", "节奏"]
        );

        var a8 = SignalRules
            .Evaluate(scenePlan, null, null, Facts)
            .Actions.Single(a => a.Code == "A-8");

        Assert.Contains("代入感", a8.Evidence);
        Assert.Contains("节奏", a8.Evidence);
        Assert.DoesNotContain("人工", a8.Prescription);
    }

    [Fact]
    public void UnclosedSetDimensions_PassthroughAsWarnings()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"payoff":{"overall-score":40,"issues":[{"description":"爽点密度不足"}]},"style":{"overall-score":95}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        // payoff 无专属规则 → 透传；style 无问题且已覆盖语义之外 → 不透传
        var warning = Assert.Single(rules.PassthroughWarnings);
        Assert.Contains("payoff", warning);
        Assert.Contains("爽点密度不足", warning);
    }

    [Fact]
    public void RhetoricShadow_PassthroughAsInfo_NoAction()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"rhetoric":{"mode":"proxy","loop-state":"shadow","is-shadow":true,"structure-ai-flavor":0.62,"surface-ai-flavor":0.35,"conclusion":"逻辑层级偏平"}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        // 信息性透传：浓度只进 Warnings，不触发处方
        Assert.DoesNotContain(rules.Actions, a => a.Code == "A-9");
        var warning = Assert.Single(rules.PassthroughWarnings);
        Assert.Contains("RACE 修辞证据", warning);
        Assert.Contains("结构浓度 0.62", warning);
        Assert.Contains("逻辑层级偏平", warning);
    }

    [Fact]
    public void RhetoricCalibrated_BecomesA9Action()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"rhetoric":{"mode":"parsed","loop-state":"calibrated","is-shadow":false,"structure-ai-flavor":0.78,"surface-ai-flavor":0.3,"conclusion":"结构偏 LLM"}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        // 云端校准通过（isShadow=false）→ 自动转 A-9 处方（零代码切换接入位）
        var a9 = Assert.Single(rules.Actions, a => a.Code == "A-9");
        Assert.Contains("归因/时序/转折", a9.Prescription);
        Assert.Contains("RACE 校准后", a9.Boundary);
        Assert.Empty(rules.PassthroughWarnings); // 不再透传
    }

    [Fact]
    public void RhetoricCalibrated_LowConcentration_NoAction()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"rhetoric":{"mode":"parsed","loop-state":"calibrated","is-shadow":false,"structure-ai-flavor":0.3,"surface-ai-flavor":0.2}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        Assert.DoesNotContain(rules.Actions, a => a.Code == "A-9");
        Assert.Empty(rules.PassthroughWarnings);
    }

    [Fact]
    public void Priority_CapsAtThree_ByDocumentedRankOrder()
    {
        // 同时命中 S-1(high)/A-3(逾期)/A-4(过载) → 取前 3
        var facts = Facts with
        {
            ActiveInfoGaps = 0,
            MaxChapterOrder = 26,
        };

        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.6},"signals":[{"dimension":"开篇","severity":"high","description":"开局无力"}]}"""
            )
        );
        var scenePlan = new ScenePlanOutcome(
            CloudSignalKind.Action,
            60,
            "choose_focus",
            false,
            null,
            ["冲突"]
        );

        var actions = SignalRules.Evaluate(scenePlan, contextRetrieve, null, facts).Actions;

        Assert.Equal(3, actions.Count);
        Assert.True(actions[0].PriorityRank <= actions[1].PriorityRank);
        Assert.True(actions[1].PriorityRank <= actions[2].PriorityRank);
        Assert.Contains(actions, a => a.Code.StartsWith("S-1"));
        Assert.Contains(actions, a => a.Code.StartsWith("A-3"));
    }

    [Fact]
    public void F1_PaceLow_TriggersPaceWarning()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.7},""signals"":[],""dimension-scores"":{""节奏"":40,""冲突"":70}}"
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        var f1 = Assert.Single(rules.Actions, a => a.Code == "F-1");
        Assert.Contains("节奏偏慢", f1.Prescription);
    }

    [Fact]
    public void F2_ConflictLow_TriggersConflictWarning()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.7},""signals"":[],""dimension-scores"":{""节奏"":80,""冲突"":30}}"
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        var f2 = Assert.Single(rules.Actions, a => a.Code == "F-2");
        Assert.Contains("冲突感不足", f2.Prescription);
    }

    [Fact]
    public void F3_DialogueLow_TriggersDialogueWarning()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.7},""signals"":[],""dimension-scores"":{""对话"":30,""节奏"":80}}"
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        var f3 = Assert.Single(rules.Actions, a => a.Code == "F-3");
        Assert.Contains("对话占比偏低", f3.Prescription);
    }

    [Fact]
    public void F4_HookLow_TriggersHookWarning()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.7},""signals"":[],""dimension-scores"":{""章末钩子"":40,""节奏"":80}}"
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        var f4 = Assert.Single(rules.Actions, a => a.Code == "F-4");
        Assert.Contains("钩子力度不足", f4.Prescription);
    }

    [Fact]
    public void F5_ThreeWeakDimensions_TriggersCompositeWarning()
    {
        // 仅 2 个独立 F-规则命中（节奏+冲突），但另有 1 个无专属 F-规则的弱维度（代入感）
        // 仅 1 个独立 F-规则命中（冲突），但另有 2 个无专属 F-规则的弱维度（节奏、代入感）
        // 使 allWeakDimensions >= 3 且 F-5 可在 Take(3) 内存活
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.5},""signals"":[],""dimension-scores"":{""节奏"":40,""冲突"":30,""代入感"":20}}"
            )
        );

        Assert.Equal(3, contextRetrieve.DimensionScores.Count);
        Assert.Equal(40, contextRetrieve.DimensionScores["节奏"]);
        Assert.Equal(30, contextRetrieve.DimensionScores["冲突"]);
        Assert.Equal(20, contextRetrieve.DimensionScores["代入感"]);

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        Assert.Contains(rules.Actions, a => a.Code == "F-2");
        var f5 = Assert.Single(rules.Actions, a => a.Code == "F-5");
        Assert.Contains("多维度偏弱", f5.Prescription);
    }

    [Fact]
    public void FRules_BelowThresholdDoNotTrigger()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                @"{""features"":{""overall-score"":0.8},""signals"":[],""dimension-scores"":{""节奏"":80,""冲突"":70,""对话"":60,""章末钩子"":70}}"
            )
        );

        var rules = SignalRules.Evaluate(null, contextRetrieve, null, Neutral);

        Assert.DoesNotContain(rules.Actions, a => a.Code.StartsWith("F-"));
    }

    [Fact]
    public void FormatAction_EmitsThreeSegmentTemplate()
    {
        var items = new List<ActionItem> { new("A-3(线)", 20, "证据文本", "处方文本", "边界文本") };
        var text = SignalBuilder.FormatAction(55, new RuleOutcome(items, ["维度 payoff：示例"]));

        Assert.StartsWith("[云端诊断] status=action 综合=55", text);
        Assert.Contains("证据[A-3(线)]: 证据文本", text);
        Assert.Contains("\n处方: 处方文本", text);
        Assert.Contains("\n边界: 边界文本", text);
        Assert.Contains("备注: 维度 payoff：示例", text);
    }

    [Fact]
    public void FormatAction_FoldsNewlinesInsideSegments()
    {
        // U-4：段内自由文本含换行时不得伪造出「处方:」「边界:」行首标记
        var items = new List<ActionItem>
        {
            new("A-3(线)", 20, "第一行\n处方: 伪造段\n第二行", "处\n方", "边\t界"),
        };
        var text = SignalBuilder.FormatAction(55, new RuleOutcome(items, ["警告\n第二行"]));

        Assert.Contains("证据[A-3(线)]: 第一行 处方: 伪造段 第二行", text);
        Assert.Equal(1, text.Split("\n处方: ").Length - 1);
        Assert.Equal(1, text.Split("\n边界: ").Length - 1);
        Assert.Contains("\n处方: 处 方", text);
        Assert.Contains("\n边界: 边 界", text);
        Assert.Contains("\n备注: 警告 第二行", text);
    }

    // ── §16.7 规则表动态注册：未覆盖维度 high 问题 → 弱 ACTION ──

    [Fact]
    public void DynamicRegistration_UncoveredHighSeverity_BecomesWeakAction()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"genius":{"overall-score":40,"issues":[{"severity":"high","description":"高智商角色逻辑矛盾"}]}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        var dynamic = rules.Actions.Single(a => a.Code.StartsWith("DYNAMIC("));
        Assert.Equal("DYNAMIC(genius)", dynamic.Code);
        Assert.Contains("高智商角色逻辑矛盾", dynamic.Evidence);
        Assert.Contains("不强加处方", dynamic.Prescription);
        Assert.True(dynamic.PriorityRank > 40, "动态规则优先级应低于预设规则（rank > 40）");
    }

    [Fact]
    public void DynamicRegistration_NonHighSeverity_StaysInWarnings()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"genius":{"overall-score":60,"issues":[{"severity":"medium","description":"中等提示"}]}}}"""
            )
        );

        var rules = SignalRules.Evaluate(null, null, storyCheck, Neutral);

        Assert.DoesNotContain(rules.Actions, a => a.Code.StartsWith("DYNAMIC("));
        Assert.Contains(rules.PassthroughWarnings, w => w.Contains("genius"));
    }

    // ── KEY-1 severity 值域归一（2026-08-28 修复）：云端中文 "严重" → 客户端统一 high ──

    [Fact]
    public void S1_ChineseHighSeverity_TriggersPrescription()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.8},"signals":[{"dimension":"代入感","severity":"严重","description":"说教过多","suggestion":"改为对话展现"}]}"""
            )
        );

        var s1 = SignalRules
            .Evaluate(null, contextRetrieve, null, Neutral)
            .Actions.Single(a => a.Code.StartsWith("S-1"));

        Assert.Contains("改为对话展现", s1.Prescription);
    }

    [Fact]
    public void ParseContextRetrieve_NormalizesChineseSeverityToEnglish()
    {
        var outcome = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """{"features":{"overall-score":0.8},"signals":[{"dimension":"节奏","severity":"严重","description":"节奏混乱"}]}"""
            )
        );

        Assert.Equal("high", outcome.Signals.Single().Severity);
    }

    [Fact]
    public void DynamicRegistration_ChineseHighSeverity_BecomesWeakAction()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"genius":{"overall-score":40,"issues":[{"severity":"严重","description":"高智商角色逻辑矛盾"}]}}}"""
            )
        );

        var dynamic = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code.StartsWith("DYNAMIC("));

        Assert.Equal("DYNAMIC(genius)", dynamic.Code);
    }

    // ── A-7 补充分支：characters[].arc 角色弧光异常（§4.2 双源）──

    [Fact]
    public void A7_CharacterArcIssues_TriggersApproachabilityRule()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"characters":[{"name":"林云","arc":{"arc-completeness":40,"issues":[{"description":"主角缺乏动机"}]},"completeness":{"overall-score":80}}]}}"""
            )
        );

        var a7 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-7");

        Assert.Contains("arc", a7.Evidence);
        Assert.Contains("林云", a7.Evidence);
        Assert.Contains("合理性", a7.Prescription);
    }

    [Fact]
    public void A7_CharacterArcHealthy_DoesNotTrigger()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"characters":[{"name":"林云","arc":{"arc-completeness":90,"issues":[]},"completeness":{"overall-score":90}}]}}"""
            )
        );

        Assert.DoesNotContain(
            SignalRules.Evaluate(null, null, storyCheck, Neutral).Actions,
            a => a.Code == "A-7"
        );
    }

    // ── A-4 补充分支：threads 维度云端密度信号（本地健康区时）──

    [Fact]
    public void A4_ThreadsHighSeverityDensity_TriggersWhenLocalHealthy()
    {
        // 本地 3 条暗线（健康区 2-6）→ 主 A-4 不触发；threads 维度 high 密度问题 → 补充 A-4
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"threads":{"overall-score":40,"issues":[{"severity":"严重","description":"伏笔过载"}]}}}"""
            )
        );

        var a4 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-4");

        Assert.Contains("threads", a4.Evidence);
        Assert.Contains("伏笔过载", a4.Evidence);
    }

    // ═══════ G-3（§15.4）：SuggestedAction 处方映射 ═══════

    [Fact]
    public void G3_SuggestedAction_Accelerate_MapsToStandardPrescription()
    {
        // story.check pace 维度带 suggestedAction="accelerate" → 处方被标准模板覆盖
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"pace":{"overall-score":30,"confidence":0.9,
                  "issues":[{"severity":"严重","description":"节奏拖沓","suggested-action":"accelerate"}]}}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-6")
            .ToList();

        Assert.Single(actions);
        Assert.Contains("加速节奏", actions[0].Prescription);
        Assert.Contains("建议动作:accelerate", actions[0].Boundary);
    }

    [Fact]
    public void G3_SuggestedAction_RewriteSelection_MapsToPrescription()
    {
        // pace 维度带 rewrite_selection 动作（A-6 规则消费）→ 处方被映射覆盖
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"pace":{"overall-score":35,"confidence":0.8,
                  "issues":[{"severity":"严重","description":"节奏拖沓","suggested-action":"rewrite_selection"}]}}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-6")
            .ToList();

        Assert.Single(actions);
        Assert.Contains("rewrite_selection", actions[0].Prescription);
    }

    [Fact]
    public void G3_UnknownSuggestedAction_KeepsOriginalPrescription()
    {
        // 未知动作（封闭集合守卫）：保留调用方原文案，不现场发明
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"pace":{"overall-score":30,"confidence":0.9,
                  "issues":[{"severity":"严重","description":"节奏拖沓","suggested-action":"mystery_action"}]}}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-6")
            .ToList();

        Assert.Single(actions);
        Assert.Contains("下一章调整节奏", actions[0].Prescription); // 原文案保留
        Assert.Contains("建议动作:mystery_action", actions[0].Boundary); // 边界仍透传
    }

    [Fact]
    public void G3_BaseTierSignal_SuggestedAction_MapsPrescription()
    {
        // 基础档 context.retrieve signals 带 suggestedAction → S-1 处方被映射覆盖
        var outcome = SignalBuilder.ParseContextRetrieve(
            ContextRetrieve(
                """
                {"features":{"overall-score":0.4},
                 "signals":[{"dimension":"节奏","severity":"严重","description":"节奏偏慢",
                   "suggestion":"下章加快","suggested-action":"accelerate"}]}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, outcome, null, Neutral)
            .Actions.Where(a => a.Code.StartsWith("S-1"))
            .ToList();

        Assert.Single(actions);
        Assert.Contains("加速节奏", actions[0].Prescription);
    }

    // ═══════ G-1（§15.3）：A-1 network 置信度降级 ═══════

    [Fact]
    public void G1_A1_LowNetworkConfidence_DowngradesRank()
    {
        // network 维度 confidence=0.2（无完整角色数据）→ A-1 rank 30→40（软信号）
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"network":{"confidence":0.2,
                  "character-page-rank":{"top-character":"萧炎","protagonist-rank":5,"n1-protagonist-eclipsed":true}}}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-1")
            .ToList();

        Assert.Single(actions);
        Assert.Equal(40, actions[0].PriorityRank);
        Assert.Contains("置信度低", actions[0].Boundary);
    }

    [Fact]
    public void G1_A7_ArrayElementLowConfidence_DowngradesRank()
    {
        // characters 数组元素带 confidence=0.2（云端 2026-08-30 起对数组维度元素附加）
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"characters":[
                  {"name":"林云","confidence":0.2,
                   "arc":{"overall-score":30,"issues":[{"severity":"严重","description":"弧光断裂"}]}}]}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-7")
            .ToList();

        Assert.Single(actions);
        Assert.Equal(50, actions[0].PriorityRank); // 40 + 10 降级
        Assert.Contains("置信度低", actions[0].Boundary);
    }

    [Fact]
    public void G1_A7_ArrayElementHighConfidence_KeepsRank()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"characters":[
                  {"name":"林云","confidence":0.9,
                   "arc":{"overall-score":30,"issues":[{"severity":"严重","description":"弧光断裂"}]}}]}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-7")
            .ToList();

        Assert.Single(actions);
        Assert.Equal(40, actions[0].PriorityRank);
        Assert.DoesNotContain("置信度低", actions[0].Boundary);
    }

    [Fact]
    public void G1_A7_NoElementConfidence_DefaultHigh()
    {
        // 无 confidence 字段（旧云端/未附加）→ 默认 1.0 不降级
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """
                {"dimensions":{"characters":[
                  {"name":"林云",
                   "arc":{"overall-score":30,"issues":[{"severity":"严重","description":"弧光断裂"}]}}]}}
                """
            )
        );

        var actions = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Where(a => a.Code == "A-7")
            .ToList();

        Assert.Single(actions);
        Assert.Equal(40, actions[0].PriorityRank);
    }
}

/// <summary>
/// SignalRules 边界补充：A-1 同名不触发 / A-1 置信度降级 rank / A-6/A-7 confidence 降级 /
/// NormalizeSeverity 未知值 / A-3 恰在揭示章（gap=0）/ A-4 熵降收线分支 /
/// S-1 suggestedAction 映射处方（G-3 用户改动回归锚定）。
/// </summary>
public sealed class SignalRulesEdgeTests
{
    private static readonly LocalFacts Neutral = new()
    {
        ProtagonistName = "林云",
        CurrentOrder = 10,
        MaxChapterOrder = 10,
        OpenPlotlines = [new PlotlineFact("线一", PlannedRevealOrder: 20)],
        ActiveInfoGaps = 1,
    };

    private static HarnessResult StoryCheck(string json) =>
        new()
        {
            Capability = "fiction.story.check",
            Result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                json
            ),
        };

    [Fact]
    public void A1_TopCharacterEqualsProtagonist_DoesNotTrigger()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"network":{"character-page-rank":{"top-character":"林云","n1-protagonist-eclipsed":false,"n2-network-concentrated":false}}}}"""
            )
        );

        Assert.DoesNotContain(
            SignalRules.Evaluate(null, null, storyCheck, Neutral).Actions,
            a => a.Code == "A-1"
        );
    }

    [Fact]
    public void A1_LowConfidence_DowngradesRankAndNotesBoundary()
    {
        // §15.3：network 维度 confidence < 0.5 → A-1 rank 30 → 40（软信号降级）
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"network":{"character-page-rank":{"top-character":"苏若雪","n1-protagonist-eclipsed":true},"confidence":0.3}}}"""
            )
        );

        var a1 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-1");

        Assert.Equal(40, a1.PriorityRank); // 30 + 10 降级
        Assert.Contains("置信度低(0.30)", a1.Boundary);
    }

    [Fact]
    public void A6_LowConfidence_DowngradesRank()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck(
                """{"dimensions":{"pace":{"overall-score":45,"issues":[{"description":"中段平缓"}],"confidence":0.2}}}"""
            )
        );

        var a6 = SignalRules
            .Evaluate(null, null, storyCheck, Neutral)
            .Actions.Single(a => a.Code == "A-6");

        Assert.Equal(60, a6.PriorityRank); // 50 + 10 降级
        Assert.Contains("置信度低", a6.Boundary);
    }

    [Fact]
    public void A6_HealthyScoreAndNoIssues_DoesNotTrigger()
    {
        var storyCheck = SignalBuilder.ParseStoryCheck(
            StoryCheck("""{"dimensions":{"pace":{"overall-score":85,"issues":[]}}}""")
        );

        Assert.DoesNotContain(
            SignalRules.Evaluate(null, null, storyCheck, Neutral).Actions,
            a => a.Code == "A-6"
        );
    }

    [Fact]
    public void NormalizeSeverity_UnknownValue_ReturnsRawLowercased()
    {
        // 未知值（如 critical）→ 保留原值（lowercase），后续 == "high" 比较不命中 → 不触发
        Assert.Equal("critical", SignalRules.NormalizeSeverity("Critical"));
        Assert.Equal("high", SignalRules.NormalizeSeverity("严重"));
        Assert.Equal("high", SignalRules.NormalizeSeverity("HIGH"));
        Assert.Equal("low", SignalRules.NormalizeSeverity("轻微"));
        Assert.Equal("low", SignalRules.NormalizeSeverity(null));
        Assert.Equal("low", SignalRules.NormalizeSeverity("  "));
    }

    [Fact]
    public void A3_PlannedRevealAtCurrentChapter_GapZero_TriggersWithNoOverdueText()
    {
        var facts = Neutral with { MaxChapterOrder = 20 }; // planned=20，gap=0

        var a3 = SignalRules
            .Evaluate(null, null, null, facts)
            .Actions.Single(a => a.Code.StartsWith("A-3"));

        Assert.Contains("已至第 20 章", a3.Evidence);
        Assert.DoesNotContain("逾期", a3.Evidence); // gap=0 无逾期字样
        Assert.Contains("回收", a3.Prescription);
    }

    [Fact]
    public void A4_EntropyDeclining_PrescribesResolveInsteadOfPlant()
    {
        var facts = new LocalFacts { OpenPlotlines = [], EntropyDelta = -0.15 };

        var a4 = SignalRules.Evaluate(null, null, null, facts).Actions.Single(a => a.Code == "A-4");

        Assert.Contains("收束", a4.Prescription);
        Assert.Contains("暂缓埋新", a4.Prescription);
        Assert.DoesNotContain("埋新线（Plant）", a4.Prescription);
    }

    [Fact]
    public void A4_NullEntropyDelta_FallsBackToPlant()
    {
        var facts = new LocalFacts { OpenPlotlines = [] }; // EntropyDelta = null

        var a4 = SignalRules.Evaluate(null, null, null, facts).Actions.Single(a => a.Code == "A-4");

        Assert.Contains("埋新线（Plant）", a4.Prescription);
        Assert.DoesNotContain("熵差", a4.Evidence); // null 不展示
    }

    /// <summary>G-3（§15.4，用户 2026-08-30 改动回归锚定）：S-1 的 suggestedAction 映射标准处方。</summary>
    [Fact]
    public void S1_SuggestedAction_MapsToStandardPrescription()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            new HarnessResult
            {
                Capability = "fiction.context.retrieve",
                Result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    """{"features":{"overall-score":0.7},"signals":[{"dimension":"节奏","severity":"high","description":"节奏拖沓","suggestion":"原建议","suggested-action":"accelerate"}]}"""
                ),
            }
        );

        var s1 = SignalRules
            .Evaluate(null, contextRetrieve, null, Neutral)
            .Actions.Single(a => a.Code.StartsWith("S-1"));

        Assert.Contains("加速节奏", s1.Prescription); // 映射后的标准处方
        Assert.Contains("建议动作:accelerate", s1.Boundary);
    }

    /// <summary>G-3：未知 suggestedAction 保留 suggestion 原文（封闭集合守卫）。</summary>
    [Fact]
    public void S1_UnknownSuggestedAction_KeepsOriginalSuggestion()
    {
        var contextRetrieve = SignalBuilder.ParseContextRetrieve(
            new HarnessResult
            {
                Capability = "fiction.context.retrieve",
                Result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    """{"features":{"overall-score":0.7},"signals":[{"dimension":"节奏","severity":"high","description":"节奏拖沓","suggestion":"原建议文本","suggested-action":"mystery_action"}]}"""
                ),
            }
        );

        var s1 = SignalRules
            .Evaluate(null, contextRetrieve, null, Neutral)
            .Actions.Single(a => a.Code.StartsWith("S-1"));

        Assert.Contains("原建议文本", s1.Prescription); // 未映射 → 原文
    }
}

/// <summary>
/// review 轴消费验收（2026-10-18）：story.check 顶层 rule-findings / continuity-audit /
/// plan-conformance → 行动项（C-1/R-1/P-1）与三段式信号文本；
/// 段缺席（quick / 旧服务端）→ 既有规则与排序逐字节不变。
/// </summary>
public sealed class ReviewFindingsConsumptionTests
{
    /// <summary>中性事实：本地规则全不命中（隔离 review 段断言）。</summary>
    private static readonly LocalFacts Neutral = new()
    {
        ProtagonistName = "林云",
        CurrentOrder = 10,
        MaxChapterOrder = 10,
        // 活跃暗线落在健康区（2–6 条）→ 不触发 A-4；计划揭示章均未到期 → 不触发 A-3
        OpenPlotlines =
        [
            new PlotlineFact("线一", PlannedRevealOrder: 20),
            new PlotlineFact("线二", PlannedRevealOrder: 40),
            new PlotlineFact("线三", PlannedRevealOrder: null),
        ],
        ActiveInfoGaps = 1,
    };

    private static StoryCheckOutcome Parse(string json) =>
        SignalBuilder.ParseStoryCheck(
            new HarnessResult
            {
                Capability = "fiction.story.check",
                Result = JsonSerializer.Deserialize<JsonElement>(json),
            }
        )!;

    /// <summary>三段齐全的 story.check 响应（dimensions 只留 pace=50 → A-6 作对照规则）。</summary>
    private const string ReviewJson = """
        {"capability":"fiction.story.check","status":"warning",
         "dimensions":{"pace":{"overall-score":50}},
         "rule-findings":[
           {"dimension":"narrative.state-entropy","severity":"high",
            "description":"纯灌水章节：叙事熵 0.21 低于阈值","evidence":"2000 字无新事件",
            "suggestion":"删除灌水段，压缩为事件推进","impact-score":88.5}],
         "continuity-audit":{
           "findings":[
             {"kind":"conflict","rule":"continuity.setup-drift","severity":"medium",
              "chapters":["ch_3"],
              "description":"林云在第 3 章被杀，但设定状态仍为 active",
              "evidence":"第 3 章正文","suggestion":"修订第 3 章死亡描写或同步设定状态",
              "impact-score":60}],
           "revision-candidates":[{"chapter":"ch_3","reasons":["continuity.setup-drift"],"impact-score":60}]},
         "plan-conformance":{"planned-beat-count":2,"missed-beat-count":1,"findings":[]}}
        """;

    /// <summary>只有 review 段（无 dimensions）→ 隔离 R-1/P-1 断言（≤3 配额内不被维度规则挤占）。</summary>
    private const string RuleAndPlanJson = """
        {"capability":"fiction.story.check","status":"warning",
         "dimensions":{},
         "rule-findings":[
           {"dimension":"narrative.state-entropy","severity":"high",
            "description":"纯灌水章节：叙事熵 0.21 低于阈值","evidence":"2000 字无新事件",
            "suggestion":"删除灌水段，压缩为事件推进","impact-score":88.5}],
         "plan-conformance":{"planned-beat-count":2,"missed-beat-count":1,"findings":[]}}
        """;

    [Fact]
    public void ParseStoryCheck_ReadsReviewSections()
    {
        var outcome = Parse(ReviewJson);

        var finding = Assert.Single(outcome.RuleFindings);
        Assert.Equal("narrative.state-entropy", finding.Dimension);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("删除灌水段，压缩为事件推进", finding.Suggestion);
        Assert.Equal(88.5, finding.ImpactScore);

        var continuity = Assert.Single(outcome.ContinuityAudit!.Findings);
        Assert.Equal("conflict", continuity.Kind);
        Assert.Equal("continuity.setup-drift", continuity.Rule);
        Assert.Equal(["ch_3"], continuity.Chapters);
        Assert.Equal(["ch_3"], outcome.ContinuityAudit.RevisionChapters);

        Assert.Equal(2, outcome.PlanConformance!.PlannedBeatCount);
        Assert.Equal(1, outcome.PlanConformance.MissedBeatCount);
    }

    [Fact]
    public void ParseStoryCheck_AbsentReviewSections_YieldEmptyViews()
    {
        var outcome = Parse("""{"dimensions":{"pace":{"overall-score":80}}}""");

        Assert.Empty(outcome.RuleFindings);
        Assert.Null(outcome.ContinuityAudit);
        Assert.Null(outcome.PlanConformance);
    }

    [Fact]
    public void Evaluate_ContinuityConflict_OutranksPresetRules()
    {
        var rules = SignalRules.Evaluate(null, null, Parse(ReviewJson), Neutral);

        // C-1（conflict）= rank 12：压过 A-6（50）；三键同轮共存（≤3 配额内）
        Assert.StartsWith("C-1(continuity.setup-drift)", rules.Actions[0].Code);
        Assert.Equal(12, rules.Actions[0].PriorityRank);
        Assert.Contains("ch_3", rules.Actions[0].Evidence);
        Assert.Contains("候选修订章：ch_3", rules.Actions[0].Boundary);
        // ≤3 配额按 rank 截取：C-1(12) → A-6(50) → R-1(55)；P-1(60) 本轮被挤出（排序即优先级语义）
        Assert.Equal(3, rules.Actions.Count);
        Assert.Contains(rules.Actions, a => a.Code == "A-6");
        Assert.Contains(rules.Actions, a => a.Code.StartsWith("R-1("));
        Assert.DoesNotContain(rules.Actions, a => a.Code == "P-1");
    }

    [Fact]
    public void Evaluate_RuleFindingAndPlanConformance_EmitActions()
    {
        var rules = SignalRules.Evaluate(null, null, Parse(RuleAndPlanJson), Neutral);

        var r1 = Assert.Single(rules.Actions, a => a.Code.StartsWith("R-1("));
        Assert.Equal("R-1(narrative.state-entropy)", r1.Code);
        Assert.Equal(55, r1.PriorityRank);
        Assert.Contains("纯灌水章节", r1.Evidence);
        Assert.Contains("规则引擎", r1.Boundary);

        var p1 = Assert.Single(rules.Actions, a => a.Code == "P-1");
        Assert.Equal(60, p1.PriorityRank);
        Assert.Contains("2 个 beat", p1.Evidence);
    }

    [Fact]
    public void Evaluate_ReviewActions_NeverEnterPassthroughWarnings()
    {
        // 透传段是「规则表外维度键」的通道：review 段是顶层键，不得混入
        var rules = SignalRules.Evaluate(null, null, Parse(ReviewJson), Neutral);

        Assert.Empty(rules.PassthroughWarnings);
    }

    [Fact]
    public void Evaluate_StoryCheckWithoutReviewSections_KeepsLegacyActions()
    {
        // quick / 旧服务端：无三段 → 仅既有 A-6（这条即「零行为变化」门）
        var rules = SignalRules.Evaluate(
            null,
            null,
            Parse("""{"dimensions":{"pace":{"overall-score":50}}}"""),
            Neutral
        );

        var action = Assert.Single(rules.Actions);
        Assert.Equal("A-6", action.Code);
    }

    [Fact]
    public void Evaluate_PlanConformanceWithoutMissedBeats_EmitsNoAction()
    {
        // beat 全落地（missed=0）→ 不产出 P-1（避免噪声）
        var rules = SignalRules.Evaluate(
            null,
            null,
            Parse(
                """{"dimensions":{},"plan-conformance":{"planned-beat-count":3,"missed-beat-count":0,"findings":[]}}"""
            ),
            Neutral
        );

        Assert.DoesNotContain(rules.Actions, a => a.Code == "P-1");
    }

    [Fact]
    public void FormatAction_RendersReviewAction_AsThreeSegments()
    {
        // 用户可见文本（注入 LLM 的信号）：C-1 必须走证据/处方/边界三段模板
        var items = new List<ActionItem>
        {
            new("C-1(continuity.setup-drift)", 12, "证据文本", "处方文本", "边界文本"),
        };

        var text = SignalBuilder.FormatAction(60, new RuleOutcome(items, []));

        Assert.StartsWith("[云端诊断] status=action 综合=60", text);
        Assert.Contains("证据[C-1(continuity.setup-drift)]: 证据文本", text);
        Assert.Contains("处方: 处方文本", text);
        Assert.Contains("边界: 边界文本", text);
    }
}
