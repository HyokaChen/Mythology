using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Threading;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// @ 文件路径补全端到端：真实输入路径驱动 TriggerCompletion（update loop），
/// 断言弹层候选来自 CWD 文件系统（Core FilePathCompletion 共享口径）。
///
/// 回归锁（两端对齐后的语义契约）：
/// - 裸 @ → 列 CWD 条目（目录尾带 /）；
/// - 含空格目录 + 尾 / → 列子目录内容（B2：空格不断词）；
/// - @ 已不再回退实体提及（实体引用一律 #）——非路径词无匹配 → 弹层不开。
/// </summary>
[Collection(TerminalUiCollection.Name)]
public sealed class TuiAppAtFileCompletionIntegrationTests : TerminalUiFixture
{
    private TuiSlashTestHarness _harness = null!;
    private string _fixtureDir = "";
    private string? _prevCwd;

    protected override TerminalSize Size => new(140, 44);

    protected override Func<CancellationToken, Task> CreateAppRunner()
    {
        // 临时目录做 CWD：裸 @ 的列举基准（测试进程 CWD 是仓库根，会列出无关条目）
        _fixtureDir = Path.Combine(
            Path.GetTempPath(),
            $"tui-at-completion-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_fixtureDir);
        Directory.CreateDirectory(Path.Combine(_fixtureDir, "卷一 风起"));
        File.WriteAllText(Path.Combine(_fixtureDir, "序章.txt"), "内容");
        File.WriteAllText(Path.Combine(_fixtureDir, "卷一 风起", "细纲.md"), "内容");

        _harness = new TuiSlashTestHarness(
            novelId: TuiSlashTestHarness.NovelId,
            sessionId: TuiSlashTestHarness.SessionId
        );
        _prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_fixtureDir);
        return _harness.App.RunAsync;
    }

    protected override async ValueTask OnStartedAsync()
    {
        await WaitUntilAsync(
            () => DispatcherAttached() && _harness.CurrentSessionId is not null,
            "TerminalApp 启动并完成首帧（Dispatcher 绑定 + 会话恢复）",
            20000
        );
    }

    protected override async ValueTask OnStoppingAsync()
    {
        await _harness.DisposeAsync();
        if (_prevCwd is not null)
            Directory.SetCurrentDirectory(_prevCwd);
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task BareAt_ListsCwdEntries()
    {
        await Dispatcher.Current.InvokeAsync(() => _harness.SetInputText("@"));
        await WaitUntilAsync(() => _harness.PromptEditor.IsMyPopupOpen, "裸 @ 未开补全弹层");

        // 候选弹层打开且文本保持 @（候选内容来自 CWD 文件系统）
        var text = await TerminalUiFixture.OnUiAsync(() => _harness.PromptEditor.Text);
        Assert.Equal("@", text);
    }

    [Fact]
    public async Task DirectoryWithSpace_Slash_NavigatesNextLevel()
    {
        // B2 回归：含空格目录名 + 尾 / → 列子目录内容
        await Dispatcher.Current.InvokeAsync(() => _harness.SetInputText("@卷一 风起/"));
        await WaitUntilAsync(() => _harness.PromptEditor.IsMyPopupOpen, "含空格目录未开补全弹层");

        // Enter 应用第一个候选（细纲.md）→ 插入输入框
        Backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
        await WaitUntilAsync(
            async () =>
                await TerminalUiFixture.OnUiAsync(() =>
                    _harness.PromptEditor.Text.Contains("细纲.md", StringComparison.Ordinal)
                ),
            "文件候选未插入输入框"
        );
    }

    [Fact]
    public async Task NonPathText_NoFallbackToEntityMention()
    {
        // 语义契约：@实体提及回退已删——纯词无文件匹配 → 不开弹层（想引用实体请用 #）
        await Dispatcher.Current.InvokeAsync(() => _harness.SetInputText("@不存在的纯词xyz"));
        await WaitUntilAsync(
            async () =>
                await TerminalUiFixture.OnUiAsync(() => !_harness.PromptEditor.IsMyPopupOpen),
            "无文件匹配时弹层应关闭（不得回退实体提及）"
        );
    }
}
