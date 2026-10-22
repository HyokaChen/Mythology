using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Desktop.Models;
using Desktop.Services;
using QRCoder;

namespace Desktop.Controls;

/// <summary>
/// 独立云端登录窗口（云端登录设计 §8）：
/// OAuth（默认，失败自动回退设备流）→ 设备流（扫码/用户码）→ 手动 Key，一屏三态切换。
/// 只依赖 CloudAuthService，不持有任何存储/协议细节。
/// 登录成功后：无小说（offerProjectCreation）→ 展示"创建第一个项目"成功态；否则自动关闭。
/// </summary>
public partial class CloudLoginWindow : Window
{
    private readonly CloudAuthService _cloudAuth;
    private readonly bool _offerProjectCreation;

    /// <summary>
    /// 登录成功后用户选择"创建第一个项目"（云端登录设计 §6.4 onboarding 衔接）。
    /// 调用方（启动引导 / Header ☁️）据此直接打开项目引导。
    /// </summary>
    public bool ProjectCreationRequested { get; private set; }

    /// <param name="cloudAuth"></param>
    /// <param name="offerProjectCreation">无小说时：登录成功后展示"创建第一个项目"成功态而非直接关闭。</param>
    public CloudLoginWindow(CloudAuthService cloudAuth, bool offerProjectCreation = false)
    {
        _cloudAuth = cloudAuth;
        _offerProjectCreation = offerProjectCreation;

        InitializeComponent();

        // 按钮事件
        OAuthButton.Click += async (_, _) =>
            await _cloudAuth.LoginWithOAuthAsync(CancellationToken.None);
        DeviceButton.Click += async (_, _) =>
            await _cloudAuth.LoginWithDeviceFlowAsync(CancellationToken.None);
        ManualKeyButton.Click += async (_, _) =>
        {
            var key = ManualKeyBox.Text?.Trim() ?? "";
            if (key.Length > 0)
                await _cloudAuth.LoginWithManualKeyAsync(key, CancellationToken.None);
        };
        SkipButton.Click += (_, _) => Close();
        ConfirmProjectButton.Click += (_, _) =>
        {
            ProjectCreationRequested = true;
            Close();
        };
        LaterButton.Click += (_, _) => Close();

        // 事件订阅（窗口关闭时解除，避免单例服务上残留订阅）
        _cloudAuth.LoginUiChanged += OnLoginUiChanged;
        _cloudAuth.StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            _cloudAuth.LoginUiChanged -= OnLoginUiChanged;
            _cloudAuth.StateChanged -= OnStateChanged;
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void OnLoginUiChanged() =>
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = _cloudAuth.LoginStatus;
            DevicePanel.IsVisible = _cloudAuth.IsDeviceFlowVisible;
            if (!_cloudAuth.IsDeviceFlowVisible)
                return;
            UserCodeText.Text = _cloudAuth.DeviceUserCode;
            VerificationUriText.Text = _cloudAuth.DeviceVerificationUri;
            DeviceStatusText.Text = _cloudAuth.DeviceStatus;
            QrImage.Source = RenderQrCode(_cloudAuth.DeviceVerificationUri);
        });

    private void OnStateChanged(CloudAuthState state)
    {
        if (state != CloudAuthState.SignedIn)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_offerProjectCreation)
            {
                // 无小说：成功态引导创建第一个项目（onboarding 衔接），不直接关闭
                FormPanel.IsVisible = false;
                SuccessPanel.IsVisible = true;
                ConfirmProjectButton.Focus();
            }
            else
            {
                Close();
            }
        });
    }

    /// <summary>用 QRCoder 生成验证 URI 的二维码 PNG，转为 Avalonia Bitmap。</summary>
    private static Bitmap? RenderQrCode(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(8);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
