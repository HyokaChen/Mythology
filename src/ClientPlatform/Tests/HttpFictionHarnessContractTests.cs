using System.Net;
using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// HttpFictionHarnessClient 与私有后端 的接口契约对齐测试。
/// 逐字段验证客户端发送的请求体与 backend Contracts/AgentContracts.cs 的
/// CreateHarnessInvocationRequest / ChapterInput / AnalysisContextInput 结构一致
/// （System.Text.Json camelCase 序列化后的字段名）。
/// </summary>
public sealed class HttpFictionHarnessContractTests
{
    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        string? ApiKey,
        string? IdempotencyKey
    );

    private static async Task<List<CapturedRequest>> CaptureAsync(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        Func<HttpFictionHarnessClient, Task> act
    )
    {
        var captured = new List<CapturedRequest>();
        var handler = new ContractStubHandler(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            captured.Add(
                new CapturedRequest(
                    request.Method,
                    request.RequestUri!.AbsolutePath,
                    body,
                    request.Headers.TryGetValues("X-API-Key", out var keys)
                        ? string.Join(",", keys)
                        : null,
                    request.Headers.TryGetValues("Idempotency-Key", out var idem)
                        ? string.Join(",", idem)
                        : null
                )
            );
            return await respond(request);
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            () => "mk_live_secret-key",
            "http://cloud.example"
        );
        await act(client);
        return captured;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CompletedResult() =>
        Json(
            """{"invocation-id":"inv_1","agent-run-id":"run_1","capability":"fiction.plotline.check","status":"completed","contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","cloud-work-units":3,"result":{"status":"pass"},"warnings":[]}"""
        );

    private static HttpResponseMessage SseCompleted() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: completed\ndata: "
                    + """{"invocation-id":"inv_1","agent-run-id":"run_1","capability":"fiction.plotline.check","status":"completed","contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","cloud-work-units":3,"result":{"status":"pass"},"warnings":[]}"""
                    + "\n\n",
                Encoding.UTF8,
                "text/event-stream"
            ),
        };

    [Fact]
    public async Task CreateInvocation_Payload_MatchesBackendContractFieldNames()
    {
        CapturedRequest? run = null,
            invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                {
                    run = await CaptureBody(request);
                    return Json(
                        """{"run-id":"run_1","status":"queued","contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","created-at":"2026-08-09T00:00:00Z"}"""
                    );
                }
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json(
                        """{"invocation-id":"inv_1","agent-run-id":"run_1","status":"queued","estimated-units":3,"contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","created-at":"2026-08-09T00:00:00Z"}"""
                    );
                }
                return SseCompleted();
            },
            async client =>
            {
                var request = new HarnessRequest
                {
                    ContractVersion = "1.0",
                    Capability = "fiction.plotline.check",
                    Depth = "standard",
                    Locale = "zh-CN",
                    Chapters =
                    [
                        new ChapterInput("ch_1", 1, "第一章", "target", "正文内容"),
                        new ChapterInput("ch_2", 2, "第二章", "context", "更多内容"),
                    ],
                    Context = new AnalysisContextInput(
                        "previous",
                        [new ChapterInput("ch_0", 0, "序章", "context", "前文")],
                        ["林风"],
                        ["玄幻"],
                        [
                            new CloudCharacterInput(
                                "林风",
                                "protagonist",
                                "男",
                                "17",
                                "坚韧隐忍",
                                "山村少年",
                                "复仇",
                                "灵根",
                                "重情",
                                "握拳"
                            ),
                        ],
                        [new CloudPlotlineInput("主线", "main", "ch2", "ch1")],
                        [new CloudItemInput("青云剑")],
                        "青云之路",
                        CharacterVersion: "cv-abc",
                        PlotlineVersion: "pv-xyz",
                        Setup: new SetupCheckInput(NovelTitle: "青云之路"),
                        Timeline:
                        [
                            new CloudTimelineEventInput("tl_1", "主角出生", "山村少年降生", "ch_1"),
                        ]
                    ),
                    Options = new AnalysisOptionsInput(
                        IncludeEvidence: true,
                        IncludeFeatures: false,
                        RetainResult: false
                    ),
                };
                await client.ExecuteAsync(request, "idem-contract-1", CancellationToken.None);
            }
        );

        // ── POST /v1/agent/runs：contractVersion + goal ──
        Assert.NotNull(run);
        var runBody = JsonDocument.Parse(run!.Body!);
        Assert.Equal("1.0", runBody.RootElement.GetProperty("contract-version").GetString());
        Assert.True(runBody.RootElement.TryGetProperty("goal", out _));

        // ── POST /v1/agent/runs/{runId}/harness-invocations ──
        Assert.NotNull(invocation);
        var inv = JsonDocument.Parse(invocation!.Body!);
        var root = inv.RootElement;

        // backend CreateHarnessInvocationRequest 顶层字段（kebab-case）
        Assert.Equal("1.0", root.GetProperty("contract-version").GetString());
        Assert.Equal("fiction.plotline.check", root.GetProperty("capability").GetString());
        Assert.Equal("standard", root.GetProperty("depth").GetString());
        Assert.Equal("zh-CN", root.GetProperty("locale").GetString());

        // chapters[]: clientChapterRef/order/title/role/content（backend ChapterInput）
        var chapters = root.GetProperty("chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        var first = chapters[0];
        Assert.Equal("ch_1", first.GetProperty("client-chapter-ref").GetString());
        Assert.Equal(1, first.GetProperty("order").GetInt32());
        Assert.Equal("第一章", first.GetProperty("title").GetString());
        Assert.Equal("target", first.GetProperty("role").GetString());
        Assert.Equal("正文内容", first.GetProperty("content").GetString());

        // context: 完整 AnalysisContextInput（旧字段 + 544a06d 结构化字段）
        var context = root.GetProperty("context");
        Assert.Equal("previous", context.GetProperty("previous-chapter-mode").GetString());
        var prevChapters = context.GetProperty("previous-chapters");
        Assert.Equal(1, prevChapters.GetArrayLength());
        Assert.Equal("ch_0", prevChapters[0].GetProperty("client-chapter-ref").GetString());
        Assert.Equal("林风", context.GetProperty("character-hints")[0].GetString());
        Assert.Equal("玄幻", context.GetProperty("plot-hints")[0].GetString());

        // 结构化角色详情必须真实上传，不能只传角色名
        var characterDetails = context.GetProperty("character-details");
        Assert.Equal(1, characterDetails.GetArrayLength());
        var linFeng = characterDetails[0];
        Assert.Equal("林风", linFeng.GetProperty("name").GetString());
        Assert.Equal("protagonist", linFeng.GetProperty("role").GetString());
        Assert.Equal("男", linFeng.GetProperty("gender").GetString());
        Assert.Equal("17", linFeng.GetProperty("age").GetString());
        Assert.Equal("坚韧隐忍", linFeng.GetProperty("personality").GetString());
        Assert.Equal("山村少年", linFeng.GetProperty("background").GetString());
        Assert.Equal("复仇", linFeng.GetProperty("motivation").GetString());
        Assert.Equal("灵根", linFeng.GetProperty("abilities").GetString());
        Assert.Equal("重情", linFeng.GetProperty("weakness").GetString());
        Assert.Equal("握拳", linFeng.GetProperty("habits").GetString());

        // 结构化情节线 / 物品 / 标题
        var plotlines = context.GetProperty("plotlines");
        Assert.Equal(1, plotlines.GetArrayLength());
        Assert.Equal("主线", plotlines[0].GetProperty("name").GetString());
        Assert.Equal("main", plotlines[0].GetProperty("type").GetString());
        Assert.Equal("ch2", plotlines[0].GetProperty("resolved-at-chapter-id").GetString());
        Assert.Equal("ch1", plotlines[0].GetProperty("planned-reveal-chapter-id").GetString());

        Assert.Equal(
            ["青云剑"],
            context
                .GetProperty("items")
                .EnumerateArray()
                .Select(i => i.GetProperty("name").GetString())
                .ToList()
        );
        Assert.Equal("青云之路", context.GetProperty("novel-title").GetString());

        // 版本指纹必须上线：服务端 novel_meta 版本比对/去重依赖（曾算得出但被出站 DTO 丢弃）
        Assert.Equal("cv-abc", context.GetProperty("character-version").GetString());
        Assert.Equal("pv-xyz", context.GetProperty("plotline-version").GetString());

        // 设定图摘要必须上线：scene.plan / story.check 的计划符合度与连续性依赖（HARNESS-CLOUD §17.3）
        Assert.Equal(
            "青云之路",
            context.GetProperty("setup").GetProperty("novel-title").GetString()
        );

        // 时间线事件必须上线：PlotHoleDetector 时间线冲突检查的唯一数据源（2026-10-18 接线）
        var timeline = context.GetProperty("timeline");
        Assert.Equal(1, timeline.GetArrayLength());
        Assert.Equal("tl_1", timeline[0].GetProperty("id").GetString());
        Assert.Equal("主角出生", timeline[0].GetProperty("title").GetString());
        Assert.Equal("山村少年降生", timeline[0].GetProperty("description").GetString());
        Assert.Equal("ch_1", timeline[0].GetProperty("chapter-id").GetString());

        // options: includeEvidence/includeFeatures/retainResult（backend AnalysisOptionsInput）
        var options = root.GetProperty("options");
        Assert.True(options.GetProperty("include-evidence").GetBoolean());
        Assert.False(options.GetProperty("include-features").GetBoolean());
        Assert.False(options.GetProperty("retain-result").GetBoolean());

        // 幂等键头
        Assert.Equal("idem-contract-1", invocation.IdempotencyKey);
    }

    [Fact]
    public async Task CreateInvocation_SetupPayload_MatchesBackendKebabKeys()
    {
        CapturedRequest? invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                {
                    return Json(
                        """{"run-id":"run_1","status":"queued","contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","created-at":"2026-08-09T00:00:00Z"}"""
                    );
                }
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json(
                        """{"invocation-id":"inv_1","agent-run-id":"run_1","status":"queued","estimated-units":4,"contract-version":"1.0","engine-version":"2026.08.1","baseline-version":"zh-web-fiction.2026.08","created-at":"2026-08-09T00:00:00Z"}"""
                    );
                }
                return SseCompleted();
            },
            async client =>
            {
                var setup = new SetupCheckInput(
                    Characters:
                    [
                        new SetupCharacterInput(
                            "c1",
                            "林默",
                            Status: "active",
                            Role: "protagonist"
                        ),
                    ],
                    Relations: [new SetupRelationInput("c2", "c1", "parent-of")],
                    WorldRules: [],
                    Scenes:
                    [
                        new SetupSceneInput(
                            "s1",
                            "山门",
                            ChapterId: "ch_1",
                            ParticipantIds: ["c1"],
                            PlotlineIds: ["pl1"],
                            TimeDescription: "黄昏"
                        ),
                    ],
                    Plotlines:
                    [
                        new SetupPlotlineInput(
                            "pl1",
                            "家族秘密",
                            "main",
                            PlannedRevealChapterId: "ch_2"
                        ),
                    ],
                    ChapterAnchors:
                    [
                        new SetupChapterAnchorInput("ch_1", 1),
                        new SetupChapterAnchorInput("ch_2", 2),
                    ],
                    Status: "ongoing",
                    NovelTitle: "林氏家族"
                );
                var request = new HarnessRequest
                {
                    ContractVersion = "1.0",
                    Capability = "fiction.setup.check",
                    Depth = "standard",
                    Locale = "zh-CN",
                    Chapters = [],
                    Context = new AnalysisContextInput(NovelTitle: "林氏家族", Setup: setup),
                };
                await client.ExecuteAsync(request, "idem-setup-1", CancellationToken.None);
            }
        );

        Assert.NotNull(invocation);
        var root = JsonDocument.Parse(invocation!.Body!).RootElement;
        Assert.Equal("fiction.setup.check", root.GetProperty("capability").GetString());
        Assert.Equal(0, root.GetProperty("chapters").GetArrayLength());

        // context.setup 顶层 kebab 键（云端 CloudSetupInput 绑定）
        var setupJson = root.GetProperty("context").GetProperty("setup");
        Assert.Equal("林氏家族", setupJson.GetProperty("novel-title").GetString());
        Assert.Equal("ongoing", setupJson.GetProperty("status").GetString());
        Assert.Equal(0, setupJson.GetProperty("world-rules").GetArrayLength());

        // characters[0] 字段 kebab 键
        var character = setupJson.GetProperty("characters")[0];
        Assert.Equal("c1", character.GetProperty("id").GetString());
        Assert.Equal("protagonist", character.GetProperty("role").GetString());

        // chapter-anchors[0] 字段 kebab 键
        var anchor = setupJson.GetProperty("chapter-anchors")[0];
        Assert.Equal("ch_1", anchor.GetProperty("chapter-id").GetString());
        Assert.Equal(1, anchor.GetProperty("order").GetInt32());

        // plotlines[0].planned-reveal-chapter-id
        Assert.Equal(
            "ch_2",
            setupJson
                .GetProperty("plotlines")[0]
                .GetProperty("planned-reveal-chapter-id")
                .GetString()
        );
    }

    [Fact]
    public async Task AllRequests_CarryApiKeyHeader()
    {
        var captured = await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                    return Json("""{"run-id":"run_1","status":"queued"}""");
                if (request.Method == HttpMethod.Post)
                    return Json("""{"invocation-id":"inv_1","status":"queued"}""");
                return SseCompleted();
            },
            async client =>
            {
                await client.ExecuteAsync(
                    new HarnessRequest { Capability = "fiction.plotline.check" },
                    "idem-key-2",
                    CancellationToken.None
                );
            }
        );

        Assert.NotEmpty(captured);
        Assert.All(captured, c => Assert.Equal("mk_live_secret-key", c.ApiKey));
    }

    [Fact]
    public async Task DynamicApiKeyProvider_ReflectsKeyRotation()
    {
        var currentKey = "mk_live_first-key";
        var seen = new List<string?>();
        var handler = new ContractStubHandler(async request =>
        {
            seen.Add(
                request.Headers.TryGetValues("X-API-Key", out var keys)
                    ? string.Join(",", keys)
                    : null
            );
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1","status":"queued"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            return SseCompleted();
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            () => currentKey,
            "http://cloud.example"
        );

        await client.ExecuteAsync(
            new HarnessRequest { Capability = "fiction.plotline.check" },
            "idem-key-3",
            CancellationToken.None
        );
        // 模拟 :cloud logout → :cloud login 换 Key（无需重建客户端）
        currentKey = "mk_live_second-key";
        await client.ExecuteAsync(
            new HarnessRequest { Capability = "fiction.plotline.check" },
            "idem-key-4",
            CancellationToken.None
        );

        Assert.Contains("mk_live_first-key", seen);
        Assert.Contains("mk_live_second-key", seen);
        Assert.DoesNotContain(seen, k => k == null);
    }

    [Fact]
    public async Task NoApiKey_FailsLocallyWithoutHttpCall()
    {
        var handlerCalled = false;
        var handler = new ContractStubHandler(async request =>
        {
            handlerCalled = true;
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1","status":"queued"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            return SseCompleted();
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            () => null,
            "http://cloud.example"
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.ExecuteAsync(
                new HarnessRequest { Capability = "fiction.plotline.check" },
                "idem-key-5",
                CancellationToken.None
            )
        );

        // 无 Key 时本地快速失败：不发起 HTTP、错误码对齐服务端 401（unauthorized），
        // 避免凭据清除后的重复 401 往返与无谓告警。
        Assert.Equal("unauthorized", ex.Code);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task GetContextAsync_UsesContextRetrieveCapability()
    {
        CapturedRequest? invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                    return Json("""{"run-id":"run_1","status":"queued"}""");
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json("""{"invocation-id":"inv_1","status":"queued"}""");
                }
                return CompletedResult();
            },
            async client =>
            {
                await client.GetContextAsync(
                    new HarnessRequest { Capability = "fiction.plotline.check" },
                    "idem-ctx-1",
                    CancellationToken.None
                );
            }
        );

        Assert.NotNull(invocation);
        var root = JsonDocument.Parse(invocation!.Body!).RootElement;
        Assert.Equal("fiction.context.retrieve", root.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task CreateInvocation_AssociativePayload_CarriesSources()
    {
        CapturedRequest? invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                    return Json("""{"run-id":"run_1","status":"queued"}""");
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json("""{"invocation-id":"inv_1","status":"queued"}""");
                }
                return CompletedResult();
            },
            async client =>
            {
                await client.ExecuteAsync(
                    new HarnessRequest
                    {
                        Capability = "fiction.associative.link",
                        Depth = "standard",
                        Locale = "zh-CN",
                        NovelId = "novel-1",
                        SceneGoal = "雨夜重逢",
                        Sources =
                        [
                            new AssociativeSourceInput(
                                Kind: "Chapter",
                                Text: "雨夜重逢的正文",
                                Title: "第七章 雨夜",
                                Summary: "两人重逢",
                                PlotlinesText: "主线",
                                Conflict: "误会未解",
                                SourceEntityType: "Chapter",
                                SourceEntityId: "ch_7",
                                SourceChapterId: "ch_7",
                                SourceChapterOrder: 7
                            ),
                        ],
                    },
                    "idem-assoc-1",
                    CancellationToken.None
                );
            }
        );

        // backend CreateHarnessInvocationRequest.Sources（联想记忆索引的输入单元）
        Assert.NotNull(invocation);
        var root = JsonDocument.Parse(invocation!.Body!).RootElement;
        var sources = root.GetProperty("sources");
        Assert.Equal(1, sources.GetArrayLength());
        var source = sources[0];
        Assert.Equal("Chapter", source.GetProperty("kind").GetString());
        Assert.Equal("雨夜重逢的正文", source.GetProperty("text").GetString());
        Assert.Equal("第七章 雨夜", source.GetProperty("title").GetString());
        Assert.Equal("两人重逢", source.GetProperty("summary").GetString());
        Assert.Equal("主线", source.GetProperty("plotlines-text").GetString());
        Assert.Equal("误会未解", source.GetProperty("conflict").GetString());
        Assert.Equal("Chapter", source.GetProperty("source-entity-type").GetString());
        Assert.Equal("ch_7", source.GetProperty("source-entity-id").GetString());
        Assert.Equal("ch_7", source.GetProperty("source-chapter-id").GetString());
        Assert.Equal(7, source.GetProperty("source-chapter-order").GetInt32());
    }

    [Fact]
    public async Task CreateInvocation_SemanticExtractionPayload_CarriesInteractions()
    {
        CapturedRequest? invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                    return Json("""{"run-id":"run_1","status":"queued"}""");
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json("""{"invocation-id":"inv_1","status":"queued"}""");
                }
                return CompletedResult();
            },
            async client =>
            {
                await client.ExecuteAsync(
                    new HarnessRequest
                    {
                        Capability = "fiction.story.check",
                        Depth = "standard",
                        Locale = "zh-CN",
                        Chapters = [new ChapterInput("ch_1", 1, "第一章", "target", "正文内容")],
                        SemanticExtraction = new SemanticExtractionInput([
                            new CharacterInteractionInput("林云", "苏瑶", "冲突", 2),
                        ]),
                    },
                    "idem-semantic-1",
                    CancellationToken.None
                );
            }
        );

        // backend CreateHarnessInvocationRequest.SemanticExtraction（网络层 N1/N2 交互图输入）
        Assert.NotNull(invocation);
        var root = JsonDocument.Parse(invocation!.Body!).RootElement;
        var interactions = root.GetProperty("semantic-extraction").GetProperty("interactions");
        Assert.Equal(1, interactions.GetArrayLength());
        var interaction = interactions[0];
        Assert.Equal("林云", interaction.GetProperty("from").GetString());
        Assert.Equal("苏瑶", interaction.GetProperty("to").GetString());
        Assert.Equal("冲突", interaction.GetProperty("type").GetString());
        Assert.Equal(2, interaction.GetProperty("paragraph-index").GetInt32());
    }

    [Fact]
    public async Task CreateInvocation_WritingProfilePayload_CarriesGenreAndTone()
    {
        CapturedRequest? invocation = null;
        await CaptureAsync(
            async request =>
            {
                if (
                    request.Method == HttpMethod.Post
                    && request.RequestUri!.AbsolutePath.EndsWith("/runs")
                )
                    return Json("""{"run-id":"run_1","status":"queued"}""");
                if (request.Method == HttpMethod.Post)
                {
                    invocation = await CaptureBody(request);
                    return Json("""{"invocation-id":"inv_1","status":"queued"}""");
                }
                return CompletedResult();
            },
            async client =>
            {
                await client.ExecuteAsync(
                    new HarnessRequest
                    {
                        Capability = "fiction.story.check",
                        Chapters = [new ChapterInput("ch_1", 1, "第一章", "target", "正文内容")],
                        Profile = new WritingProfileInput(Genre: "玄幻", Tone: "literary"),
                    },
                    "idem-profile-1",
                    CancellationToken.None
                );
            }
        );

        // backend CreateHarnessInvocationRequest.Profile（题材 + 增强档位驱动云端评分阈值）
        Assert.NotNull(invocation);
        var root = JsonDocument.Parse(invocation!.Body!).RootElement;
        var profile = root.GetProperty("profile");
        Assert.Equal("玄幻", profile.GetProperty("genre").GetString());
        Assert.Equal("literary", profile.GetProperty("tone").GetString());
    }

    private static async Task<CapturedRequest> CaptureBody(HttpRequestMessage request)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
        return new CapturedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            body,
            request.Headers.TryGetValues("X-API-Key", out var keys) ? string.Join(",", keys) : null,
            request.Headers.TryGetValues("Idempotency-Key", out var idem)
                ? string.Join(",", idem)
                : null
        );
    }

    private sealed class ContractStubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respondAsync
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => respondAsync(request);
    }
}
