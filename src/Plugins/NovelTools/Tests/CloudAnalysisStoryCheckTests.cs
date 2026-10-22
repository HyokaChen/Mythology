using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 云端契约对齐测试：NovelTools 分析工具必须走 fiction.story.check，
/// 并从 result.dimensions.<分组> 解析结果（不再解析旧 features/dimensionScores/signals）。
/// </summary>
public sealed class CloudAnalysisStoryCheckTests
{
    private static readonly string StoryCheckJson = """
        {
          "status": "pass",
          "capability": "fiction.story.check",
          "dimensions": {
            "pace": { "overall-score": 72.5 },
            "characters": [
              {
                "name": "林云",
                "arc": {
                  "character-id": "林云",
                  "character-name": "林云",
                  "arc-completeness": 88,
                  "has-starting-state": true,
                  "has-driving-force": true,
                  "has-obstacles": true,
                  "has-turning-point": true,
                  "has-ending-state": false,
                  "has-power-weakness-balance": true,
                  "arc-events": [
                    { "type": "成长", "chapter-title": "第一章" },
                    { "type": "成长", "chapter-title": "第二章" },
                    { "type": "障碍", "chapter-title": "第一章" }
                  ],
                  "issues": [
                    {
                      "element": "终点状态",
                      "severity": "medium",
                      "description": "角色缺乏终点状态",
                      "suggestion": "补一个终点"
                    }
                  ],
                  "strengths": [ "起点明确" ]
                },
                "completeness": {
                  "overall-score": 85,
                  "field-completeness-score": 90,
                  "tragic-health-score": 80,
                  "arc-balance-score": 85,
                  "field-checks": [
                    { "field": "Name", "label": "姓名", "importance": "high", "is-filled": true }
                  ],
                  "issues": [
                    {
                      "dimension": "字段完整度",
                      "severity": "high",
                      "description": "缺少弱点",
                      "suggestion": "补充弱点"
                    }
                  ],
                  "strengths": [ "关键字段已填写" ],
                  "tragic-analysis": {
                    "has-tragic-elements": true,
                    "has-resurrection-taboo": false
                  }
                }
              }
            ],
            "supporting-characters": {
              "overall-score": 66,
              "total-supporting": 1,
              "typed-supporting": 1,
              "tool-character-count": 0,
              "characters": [
                {
                  "character-id": "c2",
                  "character-name": "苏瑶",
                  "primary-type": "mentor",
                  "primary-label": "导师型",
                  "function": "传授技能",
                  "type-confidence": 0.8,
                  "matched-types": [ "mentor" ],
                  "is-tool-character": false
                }
              ],
              "issues": [],
              "strengths": []
            }
          }
        }
        """;

    private static readonly string NetworkStoryCheckJson = """
        {
          "status": "pass",
          "capability": "fiction.story.check",
          "dimensions": {
            "network": {
              "character-page-rank": {
                "top-character": "林云",
                "protagonist-rank": 1,
                "rank-gini": 0.45,
                "hub-ratio": 1.8,
                "n1-protagonist-eclipsed": false,
                "n2-network-concentrated": false
              },
              "chapter-hubs": {
                "hub-chapter-count": 1,
                "hub-titles": [ "第三章" ],
                "rank-gini": 0.62,
                "n4-hub-poverty": false
              },
              "foreshadowing-network": {
                "plotline-ranks": { "主线": 0.72, "暗线": 0.28 },
                "n5-plotline-drift": false
              },
              "protagonist-arc": {
                "sliding-decline-chapters": 3,
                "n3-protagonist-marginalized": true
              },
              "golden-sentences": {
                "golden-sentence-density-per-thousand": 1.2,
                "last-sentence-hook-score": 0.86
              }
            }
          }
        }
        """;

    private static FakeNovelStore BuildStore()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "青云之路",
                Characters =
                [
                    new Character
                    {
                        Id = "c1",
                        Name = "林云",
                        Role = CharacterRole.Protagonist,
                    },
                    new Character
                    {
                        Id = "c2",
                        Name = "苏瑶",
                        Role = CharacterRole.Supporting,
                    },
                ],
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Order = 1,
                Title = "第一章",
                Content = "林云握紧拳头走向山门。",
            }
        );
        return store;
    }

    [Fact]
    public async Task CheckCharacterCompleteness_UsesStoryCheckAndParsesCompletenessDimension()
    {
        var harness = new StubHarness(_ => JsonResult(StoryCheckJson));
        var tool = new CheckCharacterCompletenessTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckCharacterCompletenessParams { NovelId = "n1", CharacterId = "c1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        Assert.Equal(85, result.TypedDetails!.OverallScore);
        Assert.Equal(90, result.TypedDetails!.FieldCompletenessScore);
        Assert.True(result.TypedDetails!.HasTragicElements);
        Assert.False(result.TypedDetails!.HasResurrectionTaboo);
        Assert.Single(result.TypedDetails!.FieldChecks);
        Assert.Single(result.TypedDetails!.Issues);
        Assert.Single(result.TypedDetails!.Strengths);
        Assert.NotNull(harness.LastRequest.Context?.CharacterDetails);
    }

    [Fact]
    public async Task TrackCharacterArc_UsesStoryCheckAndParsesCharacterArcs()
    {
        var harness = new StubHarness(_ => JsonResult(StoryCheckJson));
        var tool = new TrackCharacterArcTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-2",
            new TrackCharacterArcParams { NovelId = "n1", CharacterIds = ["c1"] },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var details = result.TypedDetails!;
        Assert.Equal(1, details.CharacterCount);
        Assert.Equal(88, details.AvgCompleteness);
        var entry = Assert.Single(details.Characters);
        Assert.Equal("林云", entry.CharacterName);
        Assert.True(entry.HasStartingState);
        Assert.Equal(2, entry.KeyEvents.Count);
        Assert.Single(entry.Issues);
        Assert.Single(entry.Strengths);
    }

    [Fact]
    public async Task AnalyzeSupportingCharacter_UsesStoryCheckAndParsesSupportingDimension()
    {
        var harness = new StubHarness(_ => JsonResult(StoryCheckJson));
        var tool = new AnalyzeSupportingCharacterTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-3",
            new AnalyzeSupportingCharacterParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var details = result.TypedDetails!;
        Assert.Equal(66, details.OverallScore);
        Assert.Equal(1, details.TotalSupporting);
        Assert.Equal(1, details.TypedSupporting);
        var entry = Assert.Single(details.Characters);
        Assert.Equal("苏瑶", entry.CharacterName);
        Assert.Equal("导师型", entry.PrimaryLabel);
        Assert.False(entry.IsToolCharacter);
    }

    [Fact]
    public async Task AnalyzePacing_UsesStoryCheckAndParsesPaceDimension()
    {
        var harness = new StubHarness(_ => JsonResult(StoryCheckJson));
        var tool = new AnalyzePacingTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-4",
            new AnalyzePacingParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        Assert.Equal(72.5, result.TypedDetails!.OverallScore);
    }

    [Fact]
    public async Task AnalyzeNetwork_ParsesNetworkDimension()
    {
        var harness = new StubHarness(_ => JsonResult(NetworkStoryCheckJson));
        var tool = new AnalyzeNetworkTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-5",
            new AnalyzeNetworkParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var details = result.TypedDetails!;
        Assert.NotNull(details.CharacterPageRank);
        Assert.Equal("林云", details.CharacterPageRank!.TopCharacter);
        Assert.Equal(1, details.CharacterPageRank.ProtagonistRank);
        Assert.Equal(3, details.ProtagonistArc.SlidingDeclineChapters);
        Assert.True(details.ProtagonistArc.N3ProtagonistMarginalized);
        Assert.Equal(1, details.ChapterHubs!.HubChapterCount);
        Assert.Equal(2, details.ForeshadowingNetwork!.PlotlineRanks.Count);
        Assert.Equal(1.2, details.GoldenSentences.GoldenSentenceDensityPerThousand, 1);
        Assert.Contains(details.Issues, i => i.Code == "N3");
    }

    private static HarnessResult JsonResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new HarnessResult { Result = doc.RootElement.Clone() };
    }

    [Fact]
    public async Task AuthError_ShowsLoginHint_WithoutRawDetail()
    {
        var harness = new ThrowingHarness(
            new CloudApiException("unauthorized", "The API key is invalid, expired, or revoked.")
        );
        var tool = new CheckCharacterCompletenessTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckCharacterCompletenessParams { NovelId = "n1", CharacterId = "c1" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("请使用 :cloud login 登录后重试", text);
        Assert.DoesNotContain("invalid, expired", text); // 原始 401 技术细节不得画进对话流
    }

    [Fact]
    public async Task ConnectionError_HidesRawDetail_AndRefersToLog()
    {
        var harness = new ThrowingHarness(new CloudApiException("result_timeout", "timed out"));
        var tool = new CheckCharacterCompletenessTool(BuildStore(), harness);

        var result = await tool.ExecuteAsync(
            "call-2",
            new CheckCharacterCompletenessParams { NovelId = "n1", CharacterId = "c1" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        var text = ToolDispatcher.ExtractText(result.Content);
        Assert.Contains("详情已写入日志", text);
        Assert.DoesNotContain("result_timeout", text);
        Assert.DoesNotContain("timed out", text);
    }

    private sealed class ThrowingHarness(CloudApiException exception) : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => Task.FromException<HarnessResult>(exception);

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class StubHarness(Func<HarnessRequest, HarnessResult> respond)
        : IFictionHarnessClient
    {
        public HarnessRequest? LastRequest { get; private set; }

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
