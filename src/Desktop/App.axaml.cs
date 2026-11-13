using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Desktop.Styles;
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

        // 小说正文只需要基础 Markdown（段落/标题/列表/表格/代码块/图片）。
        // Math / Mermaid / Svg 扩展会引入不可估高的异步/图形控件，桌面端不注册。
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── 全局窗口入场 / 退场动效：所有 Window（含未来新增对话框）统一 ──
        // 入场：内容自己挂了 Motion.Enter 时交给它播（否则同一 Opacity 上会有两个时钟抢值）。
        // 退场：首次 Closing 取消 → 淡出下沉 → 真正 Close（27 个窗口不再硬切）；
        //       关机 / 退应用 / 随宿主关闭不播，见 AttachWindowMotion。
        // 位移优先走渲染线程 Translation，不参与布局，窗口尺寸/定位不受影响。
        Window.WindowOpenedEvent.AddClassHandler<Window>(
            static (window, args) =>
            {
                _ = args;
                Motion.AttachWindowMotion(window);
            }
        );

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

                _ = InitializeAfterRuntimeAsync(vm);

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

    /// <summary>
    /// 主窗口构造完成后立即把运行期装配投递到后台；await 让出 UI 线程，窗口得以显示
    /// 并开始泵事件，最后再加载小说/会话等 UI 状态。IsInitializing 遮罩阻止用户在
    /// Agent 工具注册完成前发送消息。
    /// </summary>
    private static async Task InitializeAfterRuntimeAsync(MainWindowViewModel vm)
    {
        try
        {
            await Program.StartRuntimeInitialization(Program.Services!);
        }
        catch (Exception ex)
        {
            // InitializeRuntime 内部已兜底；这里防御 Task.Run 调度失败等极端情况。
            Program.Services?.GetService<ILogger<App>>()?.LogCritical(ex, "运行时初始化任务失败");
        }

        await vm.InitializeAsync();
    }
}
