using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using LlmTornado;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 联想记忆触发钩子测试（不触网，stub <see cref="IFictionHarnessClient"/>）：
/// T1 写后 standard 档注入、T2 写前 quick 档注入、去重、非触发工具/空 harness/错误结果不注入。
/// </summary>
public sealed class AssociativeTriggerHookTests : IDisposable
{
    private const string NovelId = "n-hook-1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"assochook-{Guid.NewGuid():N}"
    );
    private readonly string _mythologyDir;
    private readonly string _novelsDir;

    public AssociativeTriggerHookTests()
    {
        _novelsDir = Path.Combine(_root, MythologyPaths.NovelsSubDir);
        Directory.CreateDirectory(_novelsDir);
        _mythologyDir = Path.Combine(_root, ".mythology");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        { /* 临时目录清理失败不影响测试结论 */
        }
    }

    private FileNovelStore MakeStore() => new(NovelWorkspace.Single(_novelsDir), _mythologyDir);

    private static Core.Agent.Agent MakeAgent()
    {
        var agent = new Core.Agent.Agent(
            new ToolRegistry(),
            new TornadoApi(LLmProviders.OpenAi, "test-key")
        );
        agent.CurrentNovelId = NovelId;
        return agent;
    }

    /// <summary>建书 + 建章，返回章 ID。</summary>
    private static async Task<string> SeedChapterAsync(FileNovelStore store, CancellationToken ct)
    {
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试书",
            Genre = "东方玄幻",
        };
        await store.SaveNovel(novel, ct);
        var chapter = new Chapter
        {
            Id = "ch-2",
            NovelId = NovelId,
            Order = 2,
            Title = "第二章",
            Content = "少年握紧了父亲留下的玉佩，彻夜未眠。",
        };
        await store.SaveChapter(chapter, ct);
        return chapter.Id;
    }

    private static ToolHookContext WriteContext(string chapterId) =>
        new()
        {
            ToolName = "write_chapter",
            Arguments = "{}",
            Result = new AgentToolResult<ChapterWriteStub>
            {
                Content = [new TextContent("ok")],
                Details = new ChapterWriteStub(chapterId, 16),
            },
        };

    [Fact]
    public async Task PostWrite_InjectsHits_WithStandardDepth()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var chapterId = await SeedChapterAsync(store, ct);
        var harness = new StubHarnessClient();
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, harness);

        await hook.AfterToolCallAsync(WriteContext(chapterId), ct);

        var queued = agent.InjectQueue.DequeueAllWithIds();
        var single = Assert.Single(queued);
        Assert.StartsWith(AssociativeTriggerHook.Prefix, single.Message.Content);
        Assert.Contains("父亲的玉佩可能重现", single.Message.Content);

        Assert.Equal("standard", harness.LastRequest?.Depth);
        Assert.Equal(NovelId, harness.LastRequest?.NovelId);
        Assert.NotEmpty(harness.LastRequest!.Sources!);
    }

    [Fact]
    public async Task PostWrite_SameChapter_DoesNotInjectTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var chapterId = await SeedChapterAsync(store, ct);
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, new StubHarnessClient());

        await hook.AfterToolCallAsync(WriteContext(chapterId), ct);
        await hook.AfterToolCallAsync(WriteContext(chapterId), ct);

        Assert.Single(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task PreWrite_InjectsRecall_WithQuickDepthAndArgumentsAsQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        const string scenePlan = """{"title":"雨夜对峙","conflict":"旧仇重逢"}""";
        var store = MakeStore();
        await SeedChapterAsync(store, ct);
        var harness = new StubHarnessClient();
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, harness);

        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "build_scene",
                Arguments = scenePlan,
                Result = new AgentToolResult { Content = [new TextContent("ok")] },
            },
            ct
        );

        var single = Assert.Single(agent.InjectQueue.DequeueAllWithIds());
        Assert.StartsWith(AssociativeTriggerHook.Prefix, single.Message.Content);
        Assert.Contains("落笔时可考虑呼应/回收", single.Message.Content);

        Assert.Equal("quick", harness.LastRequest?.Depth);
        Assert.Empty(harness.LastRequest!.Sources!);
        Assert.Equal(scenePlan, harness.LastRequest.SceneGoal);
    }

    [Fact]
    public async Task NonTriggerTool_DoesNotInject()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        await SeedChapterAsync(store, ct);
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, new StubHarnessClient());

        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "read_chapter",
                Arguments = "{}",
                Result = new AgentToolResult { Content = [new TextContent("ok")] },
            },
            ct
        );

        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task NullHarness_DoesNotInjectOrThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var chapterId = await SeedChapterAsync(store, ct);
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, harness: null);

        await hook.AfterToolCallAsync(WriteContext(chapterId), ct);

        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    [Fact]
    public async Task ErrorResult_DoesNotInject()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var chapterId = await SeedChapterAsync(store, ct);
        var agent = MakeAgent();
        var hook = new AssociativeTriggerHook(agent, store, new StubHarnessClient());

        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "write_chapter",
                Arguments = "{}",
                Result = new AgentToolResult<ChapterWriteStub>
                {
                    Content = [new TextContent("失败")],
                    IsError = true,
                    Details = new ChapterWriteStub(chapterId, 0),
                },
            },
            ct
        );

        Assert.Empty(agent.InjectQueue.DequeueAllWithIds());
    }

    /// <summary>IChapterWriteResult 测试桩（对齐 write_chapter 的真实返回形状）。</summary>
    private sealed record ChapterWriteStub(string Id, int WordCount) : IChapterWriteResult;

    /// <summary>stub harness：记录请求并返回固定一条命中。</summary>
    private sealed class StubHarnessClient : IFictionHarnessClient
    {
        public HarnessRequest? LastRequest { get; private set; }

        private static readonly JsonElement Payload = JsonDocument
            .Parse(
                """
                {
                  "status": "pass",
                  "indexed": 3,
                  "hits": [
                    {
                      "family": "horizon",
                      "channel": "legacy-anchor",
                      "concept": "父亲的玉佩可能重现",
                      "bridge": "玉佩 → 未来身份揭示",
                      "confidence": 0.8,
                      "cosine": 0.91,
                      "source-entity-type": "Chapter",
                      "source-entity-id": "ch-1",
                      "source-chapter-order": 1
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            return Task.FromResult(
                new HarnessResult
                {
                    Status = "completed",
                    Capability = request.Capability,
                    Result = Payload,
                }
            );
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
