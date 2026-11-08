using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.Services;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 会话边界与 Todo 面板的数据隔离：
/// 新会话必须清空；切到没有快照的会话必须为空；切到有快照的会话必须精确恢复。
/// </summary>
public sealed class SessionTodoLifecycleTests : IDisposable
{
    private const string NovelId = "nv-todo";

    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-todo-{Guid.NewGuid():N}"
    );
    private readonly string _todoRoot;
    private readonly FileSessionStore _store;
    private readonly TodoStore _todos = new();
    private readonly Agent _agent;
    private readonly SessionEngine _engine;

    public SessionTodoLifecycleTests()
    {
        var root = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        _todoRoot = Path.Combine(_base, "todos");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(storeDir);

        _store = new FileSessionStore(storeDir, root);
        _agent = new Agent(new ToolRegistry(), null) { CurrentNovelId = NovelId };
        _engine = new SessionEngine(
            new DesktopAgentBridge(_agent),
            _store,
            new FileNovelStore(root, storeDir),
            _todos,
            new ContextTokenCalibrator(),
            new ConfigStore(storeDir),
            NullLogger.Instance,
            todoRoot: _todoRoot
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void NewSession_ClearsTodos()
    {
        _todos.Create("旧任务");

        _engine.NewSession();

        Assert.Empty(_todos.List());
    }

    [Fact]
    public async Task SwitchToSessionWithoutTodoFile_ClearsTodos()
    {
        _todos.Create("旧任务");
        var sessionId = await SeedSessionAsync();

        await _engine.SwitchToAsync(sessionId, NovelId);

        Assert.Empty(_todos.List());
    }

    [Fact]
    public async Task SwitchToSessionWithTodoFile_RestoresSnapshot()
    {
        var sessionId = await SeedSessionAsync();
        _todos.Create("目标任务");
        _todos.SaveTo(Path.Combine(_todoRoot, $"{sessionId}.json"));
        _todos.Reset();

        await _engine.SwitchToAsync(sessionId, NovelId);

        var item = Assert.Single(_todos.List());
        Assert.Equal("目标任务", item.Subject);
    }

    [Fact]
    public async Task SwitchToSessionWithCorruptTodoFile_ClearsTodos()
    {
        var sessionId = await SeedSessionAsync();
        Directory.CreateDirectory(_todoRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_todoRoot, $"{sessionId}.json"),
            "{ not valid json"
        );
        _todos.Create("旧任务");

        await _engine.SwitchToAsync(sessionId, NovelId);

        Assert.Empty(_todos.List());
    }

    [Fact]
    public async Task ReloadTodosForCurrentSession_RestoresCurrentSnapshot()
    {
        var sessionId = await SeedSessionAsync();
        _todos.Create("目标任务");
        _todos.SaveTo(Path.Combine(_todoRoot, $"{sessionId}.json"));
        await _engine.SwitchToAsync(sessionId, NovelId);
        _todos.Reset();

        _engine.ReloadTodosForCurrentSession();

        var item = Assert.Single(_todos.List());
        Assert.Equal("目标任务", item.Subject);
    }

    [Fact]
    public void ReloadTodosForCurrentSession_NoSnapshot_ClearsTodos()
    {
        _engine.NewSession();
        _todos.Create("旧任务");

        _engine.ReloadTodosForCurrentSession();

        Assert.Empty(_todos.List());
    }

    [Fact]
    public async Task SaveBeforeNewSession_PreservesSourceSnapshot()
    {
        var sourceSession = await SeedSessionAsync();
        await _engine.SwitchToAsync(sourceSession, NovelId);
        _todos.Create("源会话任务");
        _engine.SaveTodos();

        var newEngine = CreateEngine();
        newEngine.NewSession();

        Assert.Empty(_todos.List());
        var restored = new TodoStore();
        Assert.True(
            restored.ReplaceFromFileOrEmpty(Path.Combine(_todoRoot, $"{sourceSession}.json"))
        );
        Assert.Equal("源会话任务", Assert.Single(restored.List()).Subject);
    }

    [Fact]
    public async Task LoadingAnotherSession_DoesNotOverwriteSourceSnapshot()
    {
        var sourceSession = await SeedSessionAsync();
        await _engine.SwitchToAsync(sourceSession, NovelId);
        _todos.Create("源会话任务");
        _engine.SaveTodos();

        var targetSession = await SeedSessionAsync();
        var targetEngine = CreateEngine();
        await targetEngine.SwitchToAsync(targetSession, NovelId);

        Assert.Empty(_todos.List());
        var restored = new TodoStore();
        Assert.True(
            restored.ReplaceFromFileOrEmpty(Path.Combine(_todoRoot, $"{sourceSession}.json"))
        );
        Assert.Equal("源会话任务", Assert.Single(restored.List()).Subject);
    }

    private SessionEngine CreateEngine()
    {
        var root = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        var agent = new Agent(new ToolRegistry(), null) { CurrentNovelId = NovelId };
        return new SessionEngine(
            new DesktopAgentBridge(agent),
            _store,
            new FileNovelStore(root, storeDir),
            _todos,
            new ContextTokenCalibrator(),
            new ConfigStore(storeDir),
            NullLogger.Instance,
            todoRoot: _todoRoot
        );
    }

    private async Task<string> SeedSessionAsync()
    {
        var sessionId = $"s{Guid.NewGuid():N}"[..12];
        await _store.SaveSessionMessages(
            sessionId,
            NovelId,
            [new LlmChatMessage(ChatMessageRoles.User, "测试消息")]
        );
        return sessionId;
    }
}
