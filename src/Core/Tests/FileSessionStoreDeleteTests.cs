using Core.Data;
using Core.Shared;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>
/// FileSessionStore.DeleteSessionAsync 回归测试：
/// 删除必须同时清磁盘分片目录与全部内存态（会话索引/分片管理器/解析缓存），
/// 否则缓存优先读取会让已删会话"复活"。
/// 与 FileNovelStoreSessionTests 共享 Collection 避免 AtomicFile 静态锁冲突。
/// </summary>
[Collection("FileNovelStore")]
public sealed class FileSessionStoreDeleteTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"mythology-delete-test-{Guid.NewGuid():N}"
    );
    private FileSessionStore _store = null!;

    private const string TestNovelId = "deletenovel";
    private const string SessionA = "deleteaaaaaa01";
    private const string SessionB = "deletebbbbbb02";

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _store = new FileSessionStore(
            novelsRoot: Path.Combine(_tempRoot, "novels"),
            mythologyDir: _tempRoot
        )
        {
            SkipConflictCheck = true,
        };
        return default;
    }

    public ValueTask DisposeAsync()
    {
        AtomicFile.ForgetPath(_tempRoot);
        if (Directory.Exists(_tempRoot))
        {
            foreach (var file in Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories))
                AtomicFile.ForgetPath(file);
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结果
            }
        }
        return default;
    }

    private static List<LlmChatMessage> Messages(string userText) =>
        [
            new LlmChatMessage(ChatMessageRoles.User, userText),
            new LlmChatMessage(ChatMessageRoles.Assistant, "收到。"),
        ];

    [Fact]
    public async Task Delete_RemovesFromDisk_Index_AndOtherSessionsSurvive()
    {
        await _store.SaveSessionMessages(SessionA, TestNovelId, Messages("A 的消息"));
        await _store.SaveSessionMessages(SessionB, TestNovelId, Messages("B 的消息"));

        var deleted = await _store.DeleteSessionAsync(SessionA);
        Assert.True(deleted);

        Assert.Null(await _store.GetSession(SessionA));
        var remaining = await _store.GetSessions(TestNovelId);
        Assert.DoesNotContain(remaining, s => s.Id == SessionA);
        Assert.Contains(remaining, s => s.Id == SessionB); // 同小说其他会话不受影响

        var novelDir = Path.Combine(_tempRoot, "sessions", $"--novels-{TestNovelId}--");
        if (Directory.Exists(novelDir))
            Assert.Empty(
                Directory
                    .GetDirectories(novelDir)
                    .Where(d => Path.GetFileName(d).EndsWith(SessionA, StringComparison.Ordinal))
                    .ToList()
            );
    }

    [Fact]
    public async Task Delete_UnknownSession_ReturnsFalse()
    {
        Assert.False(await _store.DeleteSessionAsync("nosuchsession"));
    }

    [Fact]
    public async Task Delete_Twice_ReturnsFalseOnSecondCall()
    {
        await _store.SaveSessionMessages(SessionA, TestNovelId, Messages("只删一次"));
        Assert.True(await _store.DeleteSessionAsync(SessionA));
        Assert.False(await _store.DeleteSessionAsync(SessionA));
    }

    [Fact]
    public async Task Delete_ThenRecreateWithSameId_WritesClean()
    {
        await _store.SaveSessionMessages(SessionA, TestNovelId, Messages("旧内容"));
        await _store.DeleteSessionAsync(SessionA);

        // 重建同名会话：索引/管理器/缓存已清，应作为全新会话写入且可读回
        await _store.SaveSessionMessages(SessionA, TestNovelId, Messages("新内容"));
        var msgs = await _store.GetActiveMessages(SessionA);
        Assert.Contains(msgs, m => m.Content == "新内容");
        Assert.DoesNotContain(msgs, m => m.Content == "旧内容");
    }
}
