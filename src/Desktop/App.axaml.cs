using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Desktop.ViewModels;
using Desktop.Views;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SukiUI;
using SukiUI.Models;

namespace Desktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ── LiveMarkdown 扩展节点注册（Math / Mermaid / Svg，与 LKK 一致） ──
        MarkdownNode.Edit(builder =>
            builder
                .Register<MathInlineNode>()
                .Register<MathBlockNode>()
                .Register<MermaidBlockNode>()
        );
        MarkdownRenderer.ConfigurePipeline += x => x.UseMermaid();
        AsyncImageLoader.DefaultDecoders = [SvgImageDecoder.Shared, DefaultBitmapDecoder.Shared];
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── SukiUI 主题（与 LKK 一致：Light 基色 + 自定义品牌色卡） ──
        // 品牌色：Adaline（DESIGN.md）—— Olive Press（CTA 主色）+ Sage Leaf（强调色）
        SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Light);
        var brandTheme = new SukiColorTheme(
            "Mythology",
            Color.Parse("#2b390a"),
            Color.Parse("#4a6d47")
        );
        SukiTheme.GetInstance().AddColorTheme(brandTheme);
        SukiTheme.GetInstance().ChangeColorTheme(brandTheme);

        // ── SukiUI ContextMenu 阴影修复 ──
        // 源码中 BoxShadow="{DynamicResource SukiPopupShadow}" 是模板内本地值（优先级高于 Style），
        // 唯一途径是覆盖该资源；但 SukiTheme 的主题资源在其完全初始化后才合并，故延迟到
        // ApplicationIdle 时覆盖（确保晚于 SukiTheme 的资源合并）。
        var log = Program.Services?.GetRequiredService<ILogger<App>>();

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                Resources["SukiPopupShadow"] = new BoxShadows();
                Resources["SukiSmallPopupShadow"] = new BoxShadows();
                Resources["SukiBigPopupShadow"] = new BoxShadows();
                log?.LogInformation("SukiPopupShadow 覆盖完成");
            },
            Avalonia.Threading.DispatcherPriority.ApplicationIdle
        );

        // 全局未处理异常 → 落盘，便于排查 macOS 原生层问题
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "myth-desktop-crash.log");
                File.WriteAllText(path, e.ExceptionObject.ToString());
                log?.LogCritical(
                    e.ExceptionObject as Exception,
                    "未处理的 AppDomain 异常，已写入: {Path}",
                    path
                );
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "myth-desktop-crash.log");
                File.AppendAllText(path, "[UnobservedTask] " + e.Exception + "\n");
                log?.LogCritical(e.Exception, "未观察到的 Task 异常，已写入: {Path}", path);
            }
            catch { }
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var vm = Program.Services!.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = new MainWindow { DataContext = vm };

                // 自动更新：安装器启动后经此退出应用（desktop.Shutdown → Exit 事件 → vm.Shutdown 结算/中断）
                if (Program.Services!.GetService<UpdateState>() is { } updateState)
                    updateState.RequestAppExit = () => desktop.Shutdown();

                // 恢复持久化主题（必须在窗口创建后：DynamicResource 消费者已就绪）
                Program.Services!.GetRequiredService<Services.DesktopThemeService>().Restore();

                _ = vm.InitializeAsync();

                // 窗口关闭时：取消正在处理的 Agent 请求
                desktop.Exit += (_, _) => vm.Shutdown();
            }
            catch (Exception ex)
            {
                log?.LogCritical(ex, "主窗口初始化失败");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
