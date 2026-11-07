using System.IO;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 新机器/新小说的基本不变量：Novel 默认 id 是 12 位 hex，SaveNovel 写入 novel.yaml；
/// 会话目录按该 id 编码（不是标题），所以改书名后会话仍能找到。
/// TUI（NovelProjectDialog）与 Desktop（ProjectPickerWindow）都用 `new Novel` + SaveNovel，
/// 二者自此走同一条路径。
/// </summary>
public sealed class NewNovelSessionKeyTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _novelsRoot;
    private readonly string _mythologyDir;

    public NewNovelSessionKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"new-novel-{Guid.NewGuid():N}");
        _novelsRoot = Path.Combine(_root, "novels");
        _mythologyDir = Path.Combine(_root, ".mythology");
        Directory.CreateDirectory(_novelsRoot);
        Directory.CreateDirectory(_mythologyDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NewNovel_SessionsLiveUnderNovelIdDir_AndSurviveTitleRename()
    {
        var ct = TestContext.Current.CancellationToken;
        var novels = new FileNovelStore(_novelsRoot, _mythologyDir);

        // 与 TUI/Desktop 新建表单一致：不显式设 Id，走 Novel 默认 12 位 hex
        var novel = new Novel { Title = "新书", Genre = "玄幻" };
        await novels.SaveNovel(novel, ct);

        Assert.Equal(12, novel.Id.Length);
        var yaml = await File.ReadAllTextAsync(Path.Combine(_novelsRoot, "新书", "novel.yaml"), ct);
        Assert.Contains($"id: {novel.Id}", yaml);

        var sessions = new FileSessionStore(_novelsRoot, _mythologyDir);
        await sessions.SaveSessionMessages(
            "sessA",
            novel.Id,
            [new LlmTornado.Chat.ChatMessage(LlmTornado.Code.ChatMessageRoles.User, "你好")],
            ct: ct
        );

        var idDir = Path.Combine(_mythologyDir, "sessions", $"--novels-{novel.Id}--");
        var sessionDir = Assert.Single(Directory.GetDirectories(idDir));
        Assert.True(File.Exists(Path.Combine(sessionDir, "MANIFEST.json")));
        Assert.False(Directory.Exists(Path.Combine(_mythologyDir, "sessions", "--novels-新书--")));

        // 重启读盘：id 不变
        var reopened = await new FileNovelStore(_novelsRoot, _mythologyDir).GetNovel(novel.Id, ct);
        Assert.NotNull(reopened);
        Assert.Equal(novel.Id, reopened!.Id);

        // 改名不影响会话归属（目录只认 id）
        reopened.Title = "改名后的书";
        await novels.SaveNovel(reopened, ct);
        var listed = Assert.Single(await sessions.GetSessions(novel.Id, ct));
        Assert.Equal("sessA", listed.Id);
        Assert.False(
            Directory.Exists(Path.Combine(_mythologyDir, "sessions", "--novels-改名后的书--"))
        );
    }
}
