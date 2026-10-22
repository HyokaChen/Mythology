using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// scan_hallucination 工具测试（fiction.hallucination.scan）：
/// 请求必须携带 Capability=fiction.hallucination.scan、章节引用规范化为 ch_{Order}（云端
/// ChapterRefResolver 只认该形式）、Context.Setup 含 E5a 出场数据；超 100 章取最近窗口；
/// 深度被服务端拒绝时降级 quick；云端不可用/无正文时返回可读错误。
/// </summary>
public sealed class ScanHallucinationToolTests
{
    private static readonly string ScanJson = """
        {
          "capability": "fiction.hallucination.scan",
          "status": "completed",
          "scan": {
            "chapters-scanned": 2,
            "attributes-checked": 2,
            "entities-covered": 1,
            "summary": { "conflicts": 1, "undetermined": 0, "high-risk": 1 },
            "r-drift-score": 0.4564,
            "risk-level": "high",
            "attribute-groups": [
              {
                "entity-id": "c1",
                "attribute": "status",
                "r-drift": 0.4564,
                "observation-count": 3,
                "distinct-values": [ "deceased", "alive" ],
                "dominant-value": "deceased"
              }
            ]
          },
          "findings": [
            {
              "id": "HS.conflict.status.c1",
              "verdict": "conflict",
              "layer": "l1",
              "description": "角色「林昭」设定状态为 active，但正文主导断言为已死亡（2/3）",
              "evidence": "设定=active, 正文主导值=deceased (2/3)",
              "suggestion": "确认以哪一侧为准：修正正文或更新角色状态",
              "involved-element-ids": [ "c1" ]
            }
          ],
          "metadata": { "total-compute-units": 6, "scanned-observations": 4 }
        }
        """;

    private static HarnessResult OkResult(int chaptersScanned = 2) =>
        new()
        {
            Status = "completed",
            Capability = "fiction.hallucination.scan",
            Result = JsonDocument
                .Parse(
                    ScanJson.Replace(
                        "\"chapters-scanned\": 2",
                        $"\"chapters-scanned\": {chaptersScanned}"
                    )
                )
                .RootElement.Clone(),
        };

    private static async Task<FakeNovelStore> SeedStore(int chapterCount = 2)
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
        for (var i = 1; i <= chapterCount; i++)
        {
            await store.SaveChapter(
                new Chapter
                {
                    Id = $"ch-guid-{i:D4}",
                    NovelId = "n1",
                    Title = $"第{i}章",
                    Order = i,
                    Content = i == 1 ? "林昭死了。" : "林昭复活。",
                }
            );
        }
        await store.SaveCharacter(
            new Character
            {
                Id = "c1",
                NovelId = "n1",
                Name = "林昭",
                Role = CharacterRole.Protagonist,
                Status = CharacterStatus.Active,
            }
        );
        await store.SaveLocation(
            new Location
            {
                Id = "loc1",
                NovelId = "n1",
                Name = "青云山",
            }
        );
        // E5a 通道①：出场记录 → 云端出场模式约束数据面
        await store.SaveChapterAppearances(
            "n1",
            "ch-guid-0001",
            [
                new AppearanceRecord
                {
                    Id = "a1",
                    NovelId = "n1",
                    EntityType = EntityType.Character,
                    EntityId = "c1",
                    ChapterId = "ch-guid-0001",
                    Mode = AppearanceMode.Present,
                },
            ]
        );
        return store;
    }

    [Fact]
    public async Task ExecuteAsync_SendsScanContract()
    {
        var store = await SeedStore();
        var harness = new StubHarness(_ => OkResult());
        var tool = new ScanHallucinationTool(store, harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var request = harness.LastRequest;
        Assert.NotNull(request);
        Assert.Equal("fiction.hallucination.scan", request!.Capability);
        Assert.Equal("standard", request.Depth);
        Assert.Equal("n1", request.NovelId);
        Assert.Equal(2, request.Chapters.Count);
        // 关键：章节引用规范化为 ch_{Order}（GUID 会让云端引用解析失败 → 约束/时窗全部失效）
        Assert.Equal(["ch_1", "ch_2"], request.Chapters.Select(c => c.ClientChapterRef).ToArray());
        Assert.All(request.Chapters, c => Assert.Equal("main", c.Role));
        Assert.NotNull(request.Context);
        Assert.NotNull(request.Context!.Setup);
        Assert.Equal("林氏家族", request.Context.Setup!.NovelTitle);
        Assert.Equal(
            ["ch_1", "ch_2"],
            request.Context.Setup.ChapterAnchors!.Select(a => a.ChapterId).ToArray()
        );
        // E5a 出场数据随请求上报（云端出场模式约束依赖它）
        Assert.NotNull(request.Context.Setup.EntityAppearances);
        Assert.Equal("c1", Assert.Single(request.Context.Setup.EntityAppearances!).EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFindings()
    {
        var store = await SeedStore();
        var tool = new ScanHallucinationTool(store, new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var text = ExtractText(result.Content);
        Assert.Contains("HS.conflict.status.c1", text);
        Assert.Contains("已死亡", text);
        Assert.Contains("漂移分 0.456", text);

        var details = Assert.IsType<HallucinationScanDetails>(result.Details);
        Assert.Equal("high", details.RiskLevel);
        Assert.Equal(1, details.Conflicts);
        Assert.Equal(0.4564, details.RDriftScore, 4);
        Assert.Single(details.Findings);
        Assert.Equal("conflict", details.Findings[0].Verdict);
        Assert.Single(details.AttributeGroups);
        Assert.Equal("deceased", details.AttributeGroups[0].DominantValue);
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
                    "Depth 'standard' is not available for the free plan (available: quick)."
                );
            }
            return OkResult();
        });
        var tool = new ScanHallucinationTool(store, harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal(2, requests.Count);
        Assert.Equal("standard", requests[0].Depth);
        Assert.Equal("quick", requests[1].Depth);
    }

    [Fact]
    public async Task ExecuteAsync_OverChapterLimit_KeepsLatest100()
    {
        var store = await SeedStore(chapterCount: 105);
        var harness = new StubHarness(_ => OkResult(chaptersScanned: 100));
        var tool = new ScanHallucinationTool(store, harness);

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapters = harness.LastRequest!.Chapters;
        Assert.Equal(100, chapters.Count); // 云端门禁 1..100 章
        Assert.Equal("ch_6", chapters[0].ClientChapterRef); // 取最近 100 章（舍弃最早的 5 章）
        Assert.Equal("ch_105", chapters[^1].ClientChapterRef);
        Assert.Contains("仅取最近 100 章", ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_NoHarness_ReturnsReadableError()
    {
        var store = await SeedStore();
        var tool = new ScanHallucinationTool(store, cloudHarness: null);

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n1" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("云端幻觉扫描不可用", ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_NoChaptersWithContent_ReturnsError()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n2", Title = "空小说" });
        await store.SaveChapter(
            new Chapter
            {
                Id = "empty-1",
                NovelId = "n2",
                Title = "空章",
                Order = 1,
                Content = "",
            }
        );
        var tool = new ScanHallucinationTool(store, new StubHarness(_ => OkResult()));

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "n2" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("没有可扫描的正文章节", ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownNovel_ReturnsError()
    {
        var tool = new ScanHallucinationTool(
            new FakeNovelStore(),
            new StubHarness(_ => OkResult())
        );

        var result = await tool.ExecuteAsync(
            "call-1",
            new ScanHallucinationParams { NovelId = "no-such" },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("未找到小说", ExtractText(result.Content));
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
