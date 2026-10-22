using System.Security.Cryptography;
using System.Text;
using Desktop.Services;
using Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// UpdateState 状态机测试（不依赖 Avalonia UI：uiPost 注入同步版本）：
/// 成功流 Phase 迁移、忙探针挂起/补弹、稍后提醒语义、失败重试、校验失败删文件、单飞闸。
/// </summary>
public sealed class UpdateStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"update-state-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言
        }
    }

    [Fact]
    public async Task FoundUpdate_DownloadsAndBecomesReady()
    {
        var (stub, _) = StubWithFile(Encoding.UTF8.GetBytes("installer-bytes"));
        var state = CreateState(stub);
        var phases = new List<UpdatePhase>();
        var enabledWhileDownloading = true;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UpdateState.Phase))
                return;
            phases.Add(state.Phase);
            if (state.Phase == UpdatePhase.Downloading)
                enabledWhileDownloading = state.IsPrimaryActionEnabled;
        };

        await state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { UpdatePhase.Checking, UpdatePhase.Downloading, UpdatePhase.Ready },
            phases
        );
        Assert.True(state.HasPendingUpdate);
        Assert.True(state.IsPromptVisible);
        Assert.False(enabledWhileDownloading); // 下载期间主按钮禁用
        Assert.Equal("安装更新", state.PrimaryActionText);
    }

    [Fact]
    public async Task BusyProbe_GatesPromptAndDefersUntilIdle()
    {
        var (stub, _) = StubWithFile(Encoding.UTF8.GetBytes("installer-bytes"));
        var state = CreateState(stub, busy: () => true);

        await state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);

        Assert.True(state.HasPendingUpdate); // 红点信号已亮
        Assert.False(state.IsPromptVisible); // 有任务在跑：只挂起、不弹

        state.IsBusyProbe = () => false;
        state.TryShowPrompt(); // 回合结束补弹
        Assert.True(state.IsPromptVisible);
    }

    [Fact]
    public async Task RemindLater_ClosesPromptWithoutTouchingPhase()
    {
        var (stub, _) = StubWithFile(Encoding.UTF8.GetBytes("installer-bytes"));
        var state = CreateState(stub);

        await state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);
        Assert.True(state.IsPromptVisible);

        state.RemindLaterCommand.Execute().Subscribe();

        Assert.False(state.IsPromptVisible);
        Assert.Equal(UpdatePhase.Ready, state.Phase); // 只关弹窗：不取消下载、不清状态

        state.TryShowPrompt(); // 本轮已 dismiss，不立刻复弹
        Assert.False(state.IsPromptVisible);

        await state.CheckNowAsync(); // 新一轮检查重置 dismiss，重跑成功后再次弹
        Assert.True(state.IsPromptVisible);
    }

    [Fact]
    public async Task DownloadFailure_ShowsRetryStateAndRetrySucceeds()
    {
        var bytes = Encoding.UTF8.GetBytes("installer-bytes");
        var installerPath = Path.Combine(_dir, "setup.exe");
        Directory.CreateDirectory(_dir);
        var stub = new StubCheckService { DownloadImpl = (_, _) => null };
        var state = CreateState(stub);

        await state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.True(state.IsFailed);
        Assert.True(state.IsPromptVisible); // 检测到更新但下载失败：也要弹（绝不静默漏掉）
        Assert.Equal("检查更新", state.PrimaryActionText);

        // 重试：stub 转成功 → 走主按钮命令再跑一次检查 + 下载
        stub.DownloadImpl = (_, _) =>
        {
            File.WriteAllBytes(installerPath, bytes);
            return installerPath;
        };
        stub.ChecksumText = Convert.ToHexString(SHA256.HashData(bytes)) + "  setup.exe\n";

        state.PrimaryActionCommand.Execute().Subscribe();
        await WaitUntilAsync(() => state.Phase == UpdatePhase.Ready);

        Assert.Equal(UpdatePhase.Ready, state.Phase);
    }

    [Fact]
    public async Task ChecksumMismatch_DeletesFileAndFails()
    {
        var bytes = Encoding.UTF8.GetBytes("installer-bytes");
        var (stub, installerPath) = StubWithFile(bytes);
        var correct = Convert.ToHexString(SHA256.HashData(bytes));
        stub.ChecksumText = (correct[0] == '0' ? "1" : "0") + correct[1..] + "  setup.exe\n";
        var state = CreateState(stub);

        await state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.False(File.Exists(installerPath)); // 校验失败 → 删除下载文件
        Assert.Contains("校验失败", state.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentChecks_RunOnce()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        var stub = new StubCheckService { CheckGate = gate };
        var state = CreateState(stub);

        var first = state.RunScheduledCheckAsync(TestContext.Current.CancellationToken);
        var second = state.CheckNowAsync(); // 单飞闸：后到者直接返回

        gate.SetResult(new UpdateCheckResult());
        await Task.WhenAll(first, second);

        Assert.Equal(1, stub.CheckCallCount);
    }

    private UpdateState CreateState(IUpdateCheckService service, Func<bool>? busy = null)
    {
        var state = new UpdateState(
            service,
            NullLogger<UpdateState>.Instance,
            uiPost: action => action()
        );
        if (busy is not null)
            state.IsBusyProbe = busy;
        return state;
    }

    private (StubCheckService Stub, string InstallerPath) StubWithFile(byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        var installerPath = Path.Combine(_dir, "setup.exe");
        var stub = new StubCheckService
        {
            DownloadImpl = (_, _) =>
            {
                File.WriteAllBytes(installerPath, bytes);
                return installerPath;
            },
            ChecksumText = Convert.ToHexString(SHA256.HashData(bytes)) + "  setup.exe\n",
        };
        return (stub, installerPath);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class StubCheckService : IUpdateCheckService
    {
        /// <summary>非 null 时 CheckAsync 挂起等待手动放行（单飞闸测试用）。</summary>
        public TaskCompletionSource<UpdateCheckResult>? CheckGate { get; init; }

        public int CheckCallCount { get; private set; }

        public Func<string, string?, string?>? DownloadImpl { get; set; }

        public string? ChecksumText { get; set; }

        public async Task<UpdateCheckResult> CheckAsync(
            CancellationToken cancellationToken = default
        )
        {
            CheckCallCount++;
            if (CheckGate is not null)
                return await CheckGate.Task;
            return FoundResult();
        }

        public Task<string?> DownloadAsync(
            string downloadUrl,
            string assetName,
            IProgress<double>? progress,
            long expectedSizeBytes,
            CancellationToken cancellationToken = default
        )
        {
            progress?.Report(50);
            progress?.Report(100);
            return Task.FromResult(DownloadImpl?.Invoke(downloadUrl, assetName));
        }

        public Task<string?> DownloadTextAsync(
            string downloadUrl,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ChecksumText);

        private static UpdateCheckResult FoundResult() =>
            new()
            {
                HasUpdate = true,
                CurrentVersion = "0.0.1+abc",
                LatestVersion = "0.0.3",
                TagName = "v0.0.3",
                PublishedAt = new DateTimeOffset(2026, 10, 12, 7, 33, 0, TimeSpan.Zero),
                ReleaseNotes = "notes",
                InstallerAssetName = "setup.exe",
                InstallerDownloadUrl = "https://example.invalid/setup.exe",
                InstallerSizeBytes = 1000,
                ChecksumAssetName = "setup.exe.sha256",
                ChecksumDownloadUrl = "https://example.invalid/setup.exe.sha256",
            };
    }
}
