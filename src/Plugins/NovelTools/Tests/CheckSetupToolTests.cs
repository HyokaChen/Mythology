using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// check_setup 工具测试（CHANGE_10 · fiction.setup.check）：
/// 请求必须携带 Capability=fiction.setup.check、Chapters 空、Context.Setup 非空且章节引用为 ch_N 规范形式；
/// 深度被服务端拒绝时降级 quick；云端不可用/设定为空时返回可读错误。
/// </summary>
public sealed class CheckSetupToolTests
{
    private static readonly string SetupCheckJson = """
        {
          "capability": "fiction.setup.check",
          "status": "pass",
          "l0-only": false,
          "findings": [
            {
              "id": "L0.dangling.relation.target.ghost",
              "verdict": "conflict",
              "layer": "L0",
              "description": "关系 c1→ghost 的终点引用不存在的实体",
              "suggestion": "修复引用"
            }
          ],
          "outline-metrics": {
            "arc-shape": "灰姑娘",
            "arc-distance": 0.18,
            "peak-gap-chapter": 12,
            "convergence": "converging",
            "warnings": [ "支线B 休眠 9 章" ]
          }
        }
        """;

    private static async Task<FakeNovelStore> SeedStore()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(
            new Novel
            {
                Id = "n1",
                Title = "林氏家族",
                Genre = "玄幻",
                CompletionStatus = "ongoing",
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch-guid-0001",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch-guid-0002",
                NovelId = "n1",
                Title = "第二章",
                Order = 2,
            }
        );
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林默",
                Age = "25",
                Role = CharacterRole.Protagonist,
                Status = CharacterStatus.Active,
                FactionId = "f1",
            }
        );
        await store.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = "n1",
                Name = "林家",
            }
        );
        // v2 一等实体：地点/道具/组织阵营（端到端契约覆盖）
        await store.SaveLocation(
            new Location
            {
                Id = "loc1",
                NovelId = "n1",
                Name = "林府",
            }
        );
        await store.SaveItem(
            new Item
            {
                Id = "it1",
                NovelId = "n1",
                Name = "家传玉佩",
                OwnerId = "c1",
            }
        );
        await store.SaveFactionRelationship(
            new FactionRelationship
            {
                Id = "fr1",
                NovelId = "n1",
                FactionAId = "f1",
                FactionBId = "f2",
                Type = FactionRelationType.Enemy,
            }
        );
        await store.SavePlotline(
            new Plotline
            {
                Id = "pl1",
                NovelId = "n1",
                Name = "家族秘密",
                Type = PlotlineType.Main,
                PlannedRevealChapterId = "ch-guid-0002",
            }
        );
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "s1",
                NovelId = "n1",
                Title = "山门",
                ChapterId = "ch-guid-0001",
                ParticipantIds = ["c1"],
                PlotlineIds = ["pl1"],
            }
        );
        return store;
    }

    private static HarnessResult OkResult() =>
        new()
        {
            Status = "pass",
            Capability = "fiction.setup.check",
            Result = JsonDocument.Parse(SetupCheckJson).RootElement.Clone(),
        };

    [Fact]
    public async Task ExecuteAsync_SendsSetupCheckContract()
    {
        var store = await SeedStore();
        var harness = new StubHarness(_ => OkResult());
        var tool = new CheckSetupTool(store, harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.NotNull(harness.LastRequest);
        Console.WriteLine(
            $"DEBUG: Locations={harness.LastRequest.Context?.Setup?.Locations?.Count}, Items={harness.LastRequest.Context?.Setup?.Items?.Count}"
        );
        Assert.Equal("fiction.setup.check", harness.LastRequest!.Capability);
        Assert.Equal("standard", harness.LastRequest.Depth);
        Assert.Empty(harness.LastRequest.Chapters);
        Assert.NotNull(harness.LastRequest.Context);
        Assert.NotNull(harness.LastRequest.Context!.Setup);
        Assert.Equal("ongoing", harness.LastRequest.Context.Setup!.Status);
        Assert.Equal("林氏家族", harness.LastRequest.Context.Setup.NovelTitle);
        // 章节引用规范化为 ch_{Order}
        Assert.Equal(
            ["ch_1", "ch_2"],
            harness
                .LastRequest.Context.Setup.ChapterAnchors!.Select(a => a.ChapterId)
                .OrderBy(x => x)
                .ToArray()
        );
        Assert.Equal(
            "ch_2",
            harness.LastRequest.Context.Setup.Plotlines![0].PlannedRevealChapterId
        );
        Assert.Equal("protagonist", harness.LastRequest.Context.Setup.Characters![0].Role);
        // 组织实体化后角色侧上报 FactionId（名称在组织实体上）；悬空判定由云端 L0 负责
        Assert.Equal("f1", harness.LastRequest.Context.Setup.Characters![0].Organizations![0]);
        // v2 一等实体：locations/items/organizations 随请求上报
        var setup = harness.LastRequest.Context.Setup;
        Assert.Equal("loc1", Assert.Single(setup.Locations!).Id);
        var item = Assert.Single(setup.Items!);
        Assert.NotNull(item.Ownership);
        Assert.Equal("c1", Assert.Single(item.Ownership!).OwnerId);
        var org = Assert.Single(setup.Organizations!);
        Assert.Equal("f1", org.Id);
        Assert.Equal("ch_1", harness.LastRequest.Context.Setup.Scenes![0].ChapterId);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFindings()
    {
        var store = await SeedStore();
        var tool = new CheckSetupTool(store, new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = ExtractText(result.Content);
        Assert.Contains("L0.dangling.relation.target.ghost", text);
        Assert.Contains("关系 c1→ghost 的终点引用不存在的实体", text);
        Assert.Contains("灰姑娘", text);
    }

    [Fact]
    public async Task ExecuteAsync_DepthRejected_FallsBackToQuick()
    {
        var store = await SeedStore();
        var requests = new List<HarnessRequest>();
        var harness = new StubHarness(request =>
        {
            requests.Add(request);
            if (request.Depth == "standard")
            {
                throw new CloudApiException(
                    "invalid_analysis_input",
                    "Depth 'standard' is not supported by 'fiction.setup.check' (supported: quick)."
                );
            }
            return OkResult();
        });
        var tool = new CheckSetupTool(store, harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal(2, requests.Count);
        Assert.Equal("standard", requests[0].Depth);
        Assert.Equal("quick", requests[1].Depth);
    }

    [Fact]
    public async Task ExecuteAsync_NoHarness_ReturnsReadableError()
    {
        var store = await SeedStore();
        var tool = new CheckSetupTool(store, cloudHarness: null);

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("云端设定检查不可用", ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_EmptySetup_ReturnsLocalError()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(new Novel { Id = "n2", Title = "空小说" });
        var tool = new CheckSetupTool(store, new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n2" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("设定为空", ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownNovel_ReturnsError()
    {
        var tool = new CheckSetupTool(new FakeNovelStore(), new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "no-such" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("未找到小说", ExtractText(result.Content));
    }

    // ── Phase 4 新增键消费（2026-10-18）：graph 摘要 + outline-metrics.volumes ──

    private static readonly string SetupCheckGraphJson = """
        {
          "capability": "fiction.setup.check",
          "status": "pass",
          "l0-only": false,
          "findings": [],
          "graph": { "source": "graph", "node-count": 42, "edge-count": 66 },
          "outline-metrics": {
            "arc-shape": "灰姑娘",
            "volumes": [
              { "volume-id": "v1", "name": "启程", "chapter-count": 12, "beat-count": 18,
                "tension-mean": 0.42, "tension-max": 0.78, "tension-min": 0.2,
                "arc-shape": "man-in-a-hole", "arc-distance": 0.31, "hook-coverage": 0.25,
                "flat-line": false, "peak-count": 3 },
              { "volume-id": "v2", "name": "决战", "chapter-count": 8, "beat-count": 0,
                "tension-mean": 0, "tension-max": 0, "tension-min": 0,
                "arc-shape": "unknown", "arc-distance": 0, "hook-coverage": 0,
                "flat-line": true, "peak-count": 0 }
            ]
          }
        }
        """;

    [Fact]
    public async Task ExecuteAsync_ParsesGraphAndVolumes_AndRendersThem()
    {
        var store = await SeedStore();
        var tool = new CheckSetupTool(
            store,
            new StubHarness(_ => new HarnessResult
            {
                Status = "pass",
                Capability = "fiction.setup.check",
                Result = JsonDocument.Parse(SetupCheckGraphJson).RootElement.Clone(),
            })
        );

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var details = Assert.IsType<CheckSetupDetails>(result.Details);
        Assert.NotNull(details.Graph);
        Assert.Equal("graph", details.Graph!.Source);
        Assert.Equal(42, details.Graph.NodeCount);
        Assert.Contains("同步图", details.Graph.Describe());

        Assert.Equal(2, details.Volumes.Count);
        var first = details.Volumes[0];
        Assert.Equal("启程", first.Name);
        Assert.Equal(12, first.ChapterCount);
        Assert.Equal(18, first.BeatCount);
        Assert.Contains("man-in-a-hole", first.Describe());
        Assert.Contains("峰值覆盖", first.Describe());
        Assert.Contains("平坦", details.Volumes[1].Describe()); // flat-line=true → 显式提示

        // 报告（LLM 可见文本）必须含两段：设定图来源 + 分卷指标
        var text = ExtractText(result.Content);
        Assert.Contains("**设定图**", text);
        Assert.Contains("同步图", text);
        Assert.Contains("**分卷大纲**", text);
        Assert.Contains("启程", text);
        Assert.Contains("决战", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutGraphKeys_HidesThem()
    {
        // 旧服务端 / quick：两键缺席 → 解析为 null/空表，报告不出现（零破坏）
        var store = await SeedStore();
        var tool = new CheckSetupTool(store, new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new CheckSetupParams { NovelId = "n1" },
            CancellationToken.None
        );

        var details = Assert.IsType<CheckSetupDetails>(result.Details);
        Assert.Null(details.Graph);
        Assert.Empty(details.Volumes);
        var text = ExtractText(result.Content);
        Assert.DoesNotContain("**设定图**", text);
        Assert.DoesNotContain("**分卷大纲**", text);
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

    private static string ExtractText(IReadOnlyList<Core.Shared.ContentBlock> content) =>
        string.Join("\n", content.OfType<Core.Shared.TextContent>().Select(t => t.Text));
}
