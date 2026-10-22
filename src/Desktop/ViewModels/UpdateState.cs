using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Desktop.ViewModels;

/// <summary>更新流程当前所处阶段。</summary>
public enum UpdatePhase
{
    /// <summary>无更新 / 尚未检查。</summary>
    Idle,

    /// <summary>检查进行中（手动触发）。</summary>
    Checking,

    /// <summary>已发现更新，静默下载中。</summary>
    Downloading,

    /// <summary>安装包已下载并校验通过，可安装。</summary>
    Ready,

    /// <summary>检查或下载失败（手动「检查更新」即可重试）。</summary>
    Failed,
}

/// <summary>
/// 自动更新共享状态（DI 单例）。弹窗（挂 MainWindow）与设置页「关于」Tab 都绑定本对象；
/// 「是否有任务在跑」由 MainWindowViewModel 注入探针，「真正退出应用」由 App 注入委托。
/// 检测/下载在后台线程；所有属性写统一经 _uiPost 切 UI 线程
/// （测试注入同步执行版本，不依赖同步上下文捕获时机）。
/// </summary>
public sealed class UpdateState : ViewModelBase
{
    private readonly IUpdateCheckService _checkService;
    private readonly ILogger<UpdateState>? _logger;

    /// <summary>UI 线程投递（默认 Dispatcher.Post；测试注入同步版本钉死顺序语义）。</summary>
    private readonly Action<Action> _uiPost;

    /// <summary>进行中下载的取消源（链接调用方 CancellationToken；结束即释放）。</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>已下载并校验通过的安装包本地路径。</summary>
    private string? _installerLocalPath;

    /// <summary>单飞闸：手动检查与定时检查并发时，后到者直接跳过（防双下载写同一 .part）。</summary>
    private int _checkRunning;

    /// <summary>本轮检查已被「稍后提醒」：不再自动弹窗，等下一次检查周期（下次启动/次日 03:00/手动检查）。</summary>
    private bool _promptDismissed;

    /// <summary>本轮检查已确认存在可用更新（含后续下载/校验失败的情况）—— Failed 态也据此放行弹窗，绝不静默漏掉升级。</summary>
    private volatile bool _updateDetected;

    /// <summary>安装器已启动（状态行固定显示「即将退出」文案，避免刷新覆盖）。</summary>
    private bool _installerLaunched;

    /// <summary>「是否有任务在跑」探针，由 MainWindowViewModel 构造时注入；null 视为不忙。</summary>
    public Func<bool>? IsBusyProbe { get; set; }

    /// <summary>真正退出应用的委托，由 App 启动时注入（desktop.Shutdown）。</summary>
    public Action? RequestAppExit { get; set; }

    public UpdatePhase Phase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string LatestVersion
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string CurrentVersion
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string ReleaseNotes
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public DateTimeOffset? PublishedAt
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string InstallerSizeText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public double DownloadProgress
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string ErrorText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>弹窗可见性（非模态 overlay，Esc/稍后提醒关闭，主界面仍可操作）。</summary>
    public bool IsPromptVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>设置页「关于」按钮文案（随 Phase/进度刷新）。</summary>
    public string PrimaryActionText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>设置页「关于」状态行文案（随 Phase 刷新）。</summary>
    public string StatusText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>有可用更新（下载中或已就绪）—— 顶栏/设置页红点信号。</summary>
    public bool HasPendingUpdate => Phase is UpdatePhase.Downloading or UpdatePhase.Ready;

    public bool IsDownloading => Phase == UpdatePhase.Downloading;

    /// <summary>弹窗「开始安装」按钮可见性。</summary>
    public bool IsInstallReady => Phase == UpdatePhase.Ready;

    /// <summary>弹窗「重试」按钮可见性（检查/下载失败）。</summary>
    public bool IsFailed => Phase == UpdatePhase.Failed;

    /// <summary>设置页主按钮可用性：检查/下载进行中禁用。</summary>
    public bool IsPrimaryActionEnabled =>
        Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading);

    /// <summary>有更新说明（ReleaseNotes 非空）—— 弹窗说明区可见性。</summary>
    public bool HasReleaseNotes => !string.IsNullOrEmpty(ReleaseNotes);

    /// <summary>有安装包大小信息—— 弹窗大小行可见性。</summary>
    public bool HasInstallerSize => !string.IsNullOrEmpty(InstallerSizeText);

    /// <summary>设置页主按钮：Ready 时安装，否则触发一次检查。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PrimaryActionCommand { get; }

    /// <summary>弹窗「开始安装」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> InstallNowCommand { get; }

    /// <summary>「稍后提醒」：只关弹窗，不停下载、不清状态。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RemindLaterCommand { get; }

    /// <summary>
    /// 构造函数（DI 单例）。
    /// </summary>
    /// <param name="checkService">更新检测/下载服务。</param>
    /// <param name="logger">日志器，可为 null。</param>
    /// <param name="uiPost">UI 线程投递（测试注入同步版本；本参数是唯一测试性注入）。</param>
    public UpdateState(
        IUpdateCheckService checkService,
        ILogger<UpdateState>? logger = null,
        Action<Action>? uiPost = null
    )
    {
        _checkService = checkService ?? throw new ArgumentNullException(nameof(checkService));
        _logger = logger;
        _uiPost = uiPost ?? (action => Dispatcher.UIThread.Post(action));

        InstallNowCommand = ReactiveCommand.CreateFromTask(InstallNowAsync);
        RemindLaterCommand = ReactiveCommand.Create(RemindLater);
        PrimaryActionCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Phase == UpdatePhase.Ready)
                await InstallNowAsync();
            else
                await RunCheckAsync(CancellationToken.None);
        });

        LatestVersion = string.Empty;
        // 未烙印产物（裸构建）没有可比基准，状态行显示为空
        CurrentVersion = UpdateVersion.Normalize(AppVersion.Full ?? string.Empty);
        ReleaseNotes = string.Empty;
        InstallerSizeText = string.Empty;
        ErrorText = string.Empty;
        PrimaryActionText = string.Empty;
        StatusText = string.Empty;

        // 派生只读属性随 Phase 变化通知（红点/按钮可见性/可用性）
        this.WhenAnyValue(x => x.Phase)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HasPendingUpdate));
                this.RaisePropertyChanged(nameof(IsDownloading));
                this.RaisePropertyChanged(nameof(IsInstallReady));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(IsPrimaryActionEnabled));
            });

        // 文案表：Phase 或下载进度变化时整表刷新
        this.WhenAnyValue(x => x.Phase, x => x.DownloadProgress).Subscribe(_ => RefreshTexts());

        // 派生可见性随内容变化通知
        this.WhenAnyValue(x => x.ReleaseNotes)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasReleaseNotes)));
        this.WhenAnyValue(x => x.InstallerSizeText)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasInstallerSize)));

        RefreshTexts();
    }

    /// <summary>定时/启动检测入口（由 UpdateCheckScheduler 调用）。</summary>
    public Task RunScheduledCheckAsync(CancellationToken cancellationToken = default) =>
        RunCheckAsync(cancellationToken);

    /// <summary>手动检查（设置页「检查更新」）。</summary>
    public Task CheckNowAsync() => RunCheckAsync(CancellationToken.None);

    /// <summary>
    /// 空闲则弹窗，否则只亮红点并记待提示（任务结束由 OnProcessingChanged 再调本方法补弹）。
    /// </summary>
    public void TryShowPrompt()
    {
        _uiPost(() =>
        {
            // 只有确有更新才弹：下载中/已就绪直接弹；「检测到更新但下载/校验失败」也弹
            // （弹窗状态行会显示失败原因）—— 否则用户重启后永远等不到任何提示，违反
            // 「不打断在跑的任务，但绝不漏掉升级」。纯检查失败（网络不可达、未发现更新）不弹。
            if (!HasPendingUpdate && !(Phase == UpdatePhase.Failed && _updateDetected))
                return;
            if (_promptDismissed)
                return; // 本轮已「稍后提醒」：等下一次检查周期，不立刻复弹
            if (IsBusyProbe?.Invoke() == true)
                return; // 有任务在跑：红点由 HasPendingUpdate 自动显示，任务结束补弹

            IsPromptVisible = true;
        });
    }

    /// <summary>
    /// 检查主流程：检测 → 发现更新即静默下载（校验 .sha256）→ 就绪/失败。
    /// 手动与定时共用；单飞闸防并发双跑。任何异常吞掉并置 Failed，绝不外抛。
    /// </summary>
    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _checkRunning, 1, 0) != 0)
            return;

        try
        {
            _promptDismissed = false;
            _uiPost(() =>
            {
                Phase = UpdatePhase.Checking;
                ErrorText = string.Empty;
                RefreshTexts();
            });

            var result = await _checkService.CheckAsync(cancellationToken).ConfigureAwait(false);

            if (!result.HasUpdate)
            {
                _updateDetected = false;
                _uiPost(() =>
                {
                    LatestVersion = result.LatestVersion;
                    Phase = UpdatePhase.Idle;
                    RefreshTexts();
                });
                return;
            }

            // 已确认存在可用更新：即便后续下载/校验失败（Failed）也要弹窗告知
            _updateDetected = true;

            _uiPost(() =>
            {
                LatestVersion = result.LatestVersion;
                CurrentVersion = UpdateVersion.Normalize(result.CurrentVersion);
                ReleaseNotes = result.ReleaseNotes ?? string.Empty;
                PublishedAt = result.PublishedAt?.ToLocalTime();
                InstallerSizeText =
                    result.InstallerSizeBytes > 0
                        ? FormatSize(result.InstallerSizeBytes)
                        : string.Empty;
                DownloadProgress = 0;
                Phase = UpdatePhase.Downloading;
                RefreshTexts();
            });

            // 检测到更新立即提示：弹窗内可见实时进度；忙则只亮红点，等任务结束补弹
            TryShowPrompt();

            try
            {
                _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var path = await DownloadAndVerifyAsync(result, _downloadCts.Token)
                    .ConfigureAwait(false);

                if (path is null)
                {
                    _uiPost(() =>
                    {
                        Phase = UpdatePhase.Failed;
                        RefreshTexts();
                    });
                }
                else
                {
                    _installerLocalPath = path;
                    _uiPost(() =>
                    {
                        Phase = UpdatePhase.Ready;
                        RefreshTexts();
                    });
                }

                // 忙时挂起的补弹（已被「稍后提醒」则不弹）
                TryShowPrompt();
            }
            catch (OperationCanceledException)
            {
                // 应用退出取消：不伪装成失败，Phase 保留
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出：静默结束
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[UpdateState] 更新检查流程异常");
            _uiPost(() =>
            {
                ErrorText = ex.Message;
                Phase = UpdatePhase.Failed;
                RefreshTexts();
            });
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            Volatile.Write(ref _checkRunning, 0);
        }
    }

    /// <summary>
    /// 静默下载并校验安装包。
    /// 校验失败 → 删除本地文件并返回 null（置 Failed）；发布未提供校验和 → 记 WRN 放行（宽容策略）。
    /// </summary>
    private async Task<string?> DownloadAndVerifyAsync(
        UpdateCheckResult result,
        CancellationToken cancellationToken
    )
    {
        var progress = new UiProgressAdapter(value => _uiPost(() => DownloadProgress = value));
        var path = await _checkService
            .DownloadAsync(
                result.InstallerDownloadUrl!,
                result.InstallerAssetName!,
                progress,
                result.InstallerSizeBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (path is null)
        {
            _uiPost(() => ErrorText = "安装包下载失败");
            return null;
        }

        if (
            string.IsNullOrEmpty(result.ChecksumAssetName)
            || string.IsNullOrEmpty(result.ChecksumDownloadUrl)
        )
        {
            _logger?.LogWarning("[UpdateState] 发布未提供校验和资产，跳过完整性校验");
            _uiPost(() => ErrorText = "发布未提供校验和，跳过完整性校验");
            return path;
        }

        var checksumText = await _checkService
            .DownloadTextAsync(result.ChecksumDownloadUrl, cancellationToken)
            .ConfigureAwait(false);
        var expected = ExtractSha256(checksumText);
        if (expected is null)
        {
            _logger?.LogWarning(
                "[UpdateState] 校验和文件无法解析，跳过校验: {Asset}",
                result.ChecksumAssetName
            );
            _uiPost(() => ErrorText = "发布未提供校验和，跳过完整性校验");
            return path;
        }

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("[UpdateState] 安装包校验失败，已删除下载文件: {Path}", path);
            TryDeleteFile(path);
            _installerLocalPath = null;
            _uiPost(() => ErrorText = "安装包校验失败，已删除下载文件");
            return null;
        }

        return path;
    }

    /// <summary>弹窗/设置页主操作「安装更新」：仅 Ready 且本地包在位时执行，否则空操作。</summary>
    private async Task InstallNowAsync()
    {
        var path = _installerLocalPath;
        if (Phase != UpdatePhase.Ready || string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            LaunchInstaller(path);
            _installerLaunched = true;
            _uiPost(RefreshTexts);
            // 给 ShellExecute/子进程返回留时间，再退出应用（Windows 上 Inno 会等旧进程释放文件）
            await Task.Delay(1500).ConfigureAwait(false);
            // 退出必须经 _uiPost 回 UI 线程：ConfigureAwait(false) 后这里在线程池线程上，
            // 而 RequestAppExit → desktop.Shutdown() 会关闭各窗口（窗口有线程亲和），
            // 后台线程直调会在关窗时抛 InvalidOperationException。
            _uiPost(() => RequestAppExit?.Invoke());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[UpdateState] 启动安装器失败");
            _uiPost(() =>
            {
                ErrorText = ex.Message;
                Phase = UpdatePhase.Failed;
                RefreshTexts();
            });
        }
    }

    /// <summary>按平台分流执行安装器（精确命令）。</summary>
    private static void LaunchInstaller(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        else if (OperatingSystem.IsLinux())
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                    ArgumentList = { path },
                }
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                    ArgumentList = { path },
                }
            );
        }
    }

    private void RemindLater()
    {
        // 只关弹窗：不取消下载、不清 Phase —— 设置页仍显示「安装更新」，下次检查周期再弹
        _uiPost(() =>
        {
            IsPromptVisible = false;
            _promptDismissed = true;
        });
    }

    /// <summary>按 Phase/进度组装按钮与状态行文案。</summary>
    private void RefreshTexts()
    {
        switch (Phase)
        {
            case UpdatePhase.Checking:
                PrimaryActionText = "检查中…";
                StatusText = "正在检查更新…";
                break;

            case UpdatePhase.Downloading:
                PrimaryActionText =
                    $"下载中 {DownloadProgress.ToString("F0", CultureInfo.InvariantCulture)}%";
                StatusText = $"发现新版本 v{LatestVersion}，正在后台下载…";
                break;

            case UpdatePhase.Ready:
                PrimaryActionText = "安装更新";
                StatusText = _installerLaunched
                    ? "安装器已启动，应用即将退出"
                    : $"新版本 v{LatestVersion} 已下载，点击安装";
                break;

            case UpdatePhase.Failed:
                PrimaryActionText = "检查更新";
                StatusText = $"更新失败：{ErrorText}";
                break;

            default:
                PrimaryActionText = "检查更新";
                if (!string.IsNullOrEmpty(LatestVersion))
                {
                    // 检查成功且无更新（服务端确实返回了最新 tag）
                    StatusText = $"已是最新版本（v{CurrentVersion}）";
                }
                else if (!string.IsNullOrEmpty(CurrentVersion))
                {
                    StatusText = $"当前版本 v{CurrentVersion}";
                }
                else
                {
                    StatusText = string.Empty;
                }

                break;
        }
    }

    /// <summary>解析 sha256sum 风格校验和文件（"&lt;hex&gt;  filename"）：取首个 64 位十六进制串。</summary>
    private static string? ExtractSha256(string? checksumText)
    {
        if (string.IsNullOrWhiteSpace(checksumText))
            return null;

        var token = checksumText
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : null;
    }

    /// <summary>安装包大小文案（GB/MB/KB 三档）。</summary>
    private static string FormatSize(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1073741824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1048576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes} B",
        };

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[UpdateState] 删除本地安装包失败");
        }
    }

    /// <summary>进度适配器：把任意线程的进度回调统一投递到 UI（Progress&lt;T&gt; 依赖同步上下文捕获时机，不用）。</summary>
    private sealed class UiProgressAdapter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
