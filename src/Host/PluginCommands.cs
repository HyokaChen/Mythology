using System.Diagnostics;
using System.Text.Json;
using Core.Plugins;

namespace Host;

/// <summary>
/// 插件管理命令：提供插件的编译、安装、列出、移除和更新等功能。
/// </summary>
public static class PluginCommands
{
    // 用户主目录路径
    private static readonly string HomeDir = Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile
    );

    // 全局插件安装目录（用户级）
    private static readonly string GlobalPluginsDir = Path.Combine(
        HomeDir,
        ".mythology",
        "plugins"
    );

    // 本地插件安装目录（项目级）
    private static readonly string LocalPluginsDir = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".mythology",
        "plugins"
    );

    /// <summary>
    /// 编译插件命令处理。
    /// 支持 --install 安装到全局、--install-local 安装到本地、-c 指定配置、--verbose 显示详细输出。
    /// </summary>
    /// <param name="args">build 子命令的参数列表</param>
    /// <returns>退出码：0 成功，非零失败</returns>
    public static async Task<int> HandleBuildAsync(string[] args)
    {
        // 编译参数默认值
        var path = Directory.GetCurrentDirectory();
        var install = false;
        var installLocal = false;
        var configuration = "Release";
        var verbose = false;

        // 解析命令行参数
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install":
                    install = true;
                    break;
                case "--install-local":
                    installLocal = true;
                    install = true;
                    break;
                case "-c" when i + 1 < args.Length:
                    configuration = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    if (!args[i].StartsWith('-'))
                        path = Path.GetFullPath(args[i]);
                    break;
            }
        }

        // 查找项目文件
        var csproj = FindCsproj(path);
        if (csproj is null)
        {
            await Console.Error.WriteLineAsync($"错误: 在 {path} 下未找到 .csproj 文件");
            return 1;
        }

        Console.WriteLine($"编译插件: {Path.GetFileName(csproj)} ({configuration})");

        // 配置 dotnet build 进程
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "build", csproj, "-c", configuration },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // 非详细模式下静默编译
        if (!verbose)
            psi.ArgumentList.Add("--verbosity quiet");

        var process = Process.Start(psi);
        if (process is null)
        {
            await Console.Error.WriteLineAsync("错误: 无法启动 dotnet build");
            return 1;
        }

        // 读取编译输出
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // 检查编译结果
        if (process.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync("编译失败:");
            if (!string.IsNullOrEmpty(stderr))
                await Console.Error.WriteLineAsync(stderr);
            if (!string.IsNullOrEmpty(stdout))
                await Console.Error.WriteLineAsync(stdout);
            return process.ExitCode;
        }

        if (verbose && !string.IsNullOrEmpty(stdout))
            Console.WriteLine(stdout);

        Console.WriteLine("编译成功");

        // 如果不安装，直接返回成功
        if (!install)
            return 0;

        // 确定编译输出目录
        var outputDir = Path.Combine(
            Path.GetDirectoryName(csproj)!,
            "bin",
            configuration,
            "net10.0"
        );
        if (!Directory.Exists(outputDir))
        {
            await Console.Error.WriteLineAsync($"错误: 输出目录不存在: {outputDir}");
            return 1;
        }

        // 读取插件清单文件
        var manifestSrc = Path.Combine(outputDir, "plugin.json");
        PluginManifest? manifest = null;
        if (File.Exists(manifestSrc))
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestSrc);
                manifest = JsonSerializer.Deserialize<PluginManifest>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[HandleBuildAsync] HandleBuildAsync: json deserialize failed: {ex.Message}"
                );
            }
        }

        // 确定插件 ID
        var pluginId =
            manifest?.Id
            ?? Path.GetFileNameWithoutExtension(csproj).Replace("Plugins.", "").ToLowerInvariant();

        // 确定安装目标目录
        var targetBase = installLocal ? LocalPluginsDir : GlobalPluginsDir;
        var targetDir = Path.Combine(targetBase, pluginId);
        Directory.CreateDirectory(targetDir);

        // 复制 DLL 文件到插件目录
        foreach (var file in Directory.GetFiles(outputDir, "*.dll"))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            Console.WriteLine($"  复制: {Path.GetFileName(file)} → {dest}");
        }

        // 复制插件清单文件
        if (File.Exists(manifestSrc))
        {
            File.Copy(manifestSrc, Path.Combine(targetDir, "plugin.json"), overwrite: true);
            Console.WriteLine($"  复制: plugin.json → {targetDir}");
        }

        Console.WriteLine($"安装完成 → {(installLocal ? "项目级" : "全局")}: {targetDir}");
        return 0;
    }

    /// <summary>
    /// 插件命令总入口，根据子命令分发到对应的处理方法。
    /// </summary>
    /// <param name="args">plugin 子命令的参数列表（第一个元素为子命令名）</param>
    /// <returns>退出码：0 成功，非零失败</returns>
    public static async Task<int> HandlePluginAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintPluginHelp();
            return 1;
        }

        var subCommand = args[0].ToLowerInvariant();
        var rest = args[1..];

        // 根据子命令分发处理
        return subCommand switch
        {
            "list" => HandlePluginList(rest),
            "install" => await HandlePluginInstall(rest),
            "remove" => HandlePluginRemove(rest),
            "update" => await HandlePluginUpdate(rest),
            _ => HandlePluginUnknown(subCommand),
        };
    }

    /// <summary>
    /// 列出已安装插件。
    /// 使用 PluginLoader.ScanPluginDirs 获取实际解析的信任级别（DLL 签名可升级 External → Verified）。
    /// 支持 --available 参数（暂未实现）。
    /// </summary>
    /// <param name="args">list 子命令参数，支持 --available</param>
    /// <returns>退出码：0 成功</returns>
    private static int HandlePluginList(string[] args)
    {
        var showAvailable = args.Contains("--available");

        // 显示云端可用插件（暂未实现）
        if (showAvailable)
        {
            Console.WriteLine("云端可用插件 — 功能尚未实现");
            Console.WriteLine("提示: 使用 'mythology plugin list' 查看本地已安装插件");
            return 0;
        }

        // 使用 PluginLoader.ScanPluginDirs 获取实际解析的信任级别
        var scanned = PluginLoader.ScanPluginDirs(GetPluginDirs());

        if (scanned.Count == 0)
        {
            Console.WriteLine("  (无已安装插件)");
            return 0;
        }

        foreach (var (manifest, _, trust) in scanned)
            Console.WriteLine(
                $"  {manifest.Name} v{manifest.Version} [{trust}] {manifest.Description}"
            );

        return 0;
    }

    /// <summary>
    /// 安装插件（目前仅创建目录，云端 API 未实现）。
    /// 支持 @version 指定版本，--local 安装到项目级。
    /// </summary>
    /// <param name="args">install 子命令参数，第一个为插件名（可含 @version），可选 --local</param>
    /// <returns>退出码：0 成功，1 参数缺失</returns>
    private static async Task<int> HandlePluginInstall(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("用法: mythology plugin install <name> [--local]");
            return 1;
        }

        var name = args[0];
        var isLocal = args.Contains("--local");
        var version = "latest";

        // 解析 @version 语法
        var atIdx = name.IndexOf('@');
        if (atIdx > 0)
        {
            version = name[(atIdx + 1)..];
            name = name[..atIdx];
        }

        // 创建插件目录
        var targetDir = isLocal
            ? Path.Combine(LocalPluginsDir, name)
            : Path.Combine(GlobalPluginsDir, name);
        Directory.CreateDirectory(targetDir);

        Console.WriteLine(
            $"安装插件 '{name}' (v{version}) → {(isLocal ? "项目级" : "全局")}: {targetDir}"
        );
        Console.WriteLine("云端 API 未实现，请手动将 DLL + plugin.json 复制到插件目录");
        return 0;
    }

    /// <summary>
    /// 移除已安装插件。
    /// 支持按目录名、插件 ID 或插件名称匹配。
    /// 如果 DLL 被锁定（应用运行中），提示用户关闭应用后重试。
    /// </summary>
    /// <param name="args">remove 子命令参数，第一个为插件名，可选 --local</param>
    /// <returns>退出码：0 成功，1 未找到或参数缺失</returns>
    private static int HandlePluginRemove(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: mythology plugin remove <name> [--local]");
            return 1;
        }

        var name = args[0];
        var isLocal = args.Contains("--local");

        // 确定搜索范围：默认搜索全局+本地，--local 只搜索本地
        var searchBases = isLocal
            ? new[] { LocalPluginsDir }
            : new[] { GlobalPluginsDir, LocalPluginsDir };

        var removed = false;
        foreach (var baseDir in searchBases)
        {
            if (!Directory.Exists(baseDir))
                continue;

            foreach (var subDir in Directory.GetDirectories(baseDir))
            {
                var manifest = PluginManifest.LoadFromDirectory(subDir);
                var dirName = Path.GetFileName(subDir);

                // 按目录名、ID 或名称匹配
                var match =
                    dirName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (
                        manifest is not null
                        && manifest.Id.Equals(name, StringComparison.OrdinalIgnoreCase)
                    )
                    || (
                        manifest is not null
                        && manifest.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    );

                if (!match)
                    continue;

                try
                {
                    Directory.Delete(subDir, recursive: true);
                    Console.WriteLine($"已移除: {subDir}");
                    removed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"无法删除: {subDir}");
                    Console.Error.WriteLine("  插件 DLL 可能被占用。请关闭 Mythology 后重试。");
                    removed = true; // 找到了但无法删除，不算"未找到"
                }
                catch (IOException)
                {
                    Console.Error.WriteLine($"无法删除: {subDir}");
                    Console.Error.WriteLine("  插件 DLL 可能被占用。请关闭 Mythology 后重试。");
                    removed = true;
                }
            }
        }

        if (!removed)
        {
            Console.Error.WriteLine($"未找到插件: {name}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 更新插件（云端 API 未实现）。
    /// </summary>
    /// <param name="args">update 子命令参数，第一个为插件名</param>
    /// <returns>退出码：1（功能未实现）</returns>
    private static async Task<int> HandlePluginUpdate(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("用法: mythology plugin update <name>");
            return 1;
        }

        var name = args[0];
        await Console.Error.WriteLineAsync($"更新插件 '{name}' — 云端 API 尚未实现");
        await Console.Error.WriteLineAsync(
            "提示: 使用 'mythology build --install' 从源码重新编译并安装"
        );
        return 1;
    }

    /// <summary>
    /// 处理未知的插件子命令。
    /// </summary>
    /// <param name="subCommand">未识别的子命令名</param>
    /// <returns>退出码：1</returns>
    private static int HandlePluginUnknown(string subCommand)
    {
        Console.Error.WriteLine($"未知子命令: plugin {subCommand}");
        PrintPluginHelp();
        return 1;
    }

    /// <summary>
    /// 打印插件命令帮助信息。
    /// </summary>
    private static void PrintPluginHelp()
    {
        Console.WriteLine("用法: mythology plugin <command> [options]");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  list [--available]             列出已安装/可用插件");
        Console.WriteLine("  install <name> [--local]       安装插件");
        Console.WriteLine("  remove <name> [--local]        移除插件（默认搜索全局+本地）");
        Console.WriteLine("  update <name>                  更新插件");
    }

    /// <summary>
    /// 获取所有插件目录（Builtin、环境变量、本地、全局）。
    /// </summary>
    /// <returns>目录路径与信任级别的列表</returns>
    private static List<(string Dir, PluginTrustLevel Trust)> GetPluginDirs()
    {
        var dirs = new List<(string, PluginTrustLevel)>();

        // Builtin: 主程序同目录
        var builtinDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(builtinDir))
            dirs.Add((builtinDir, PluginTrustLevel.Builtin));

        // External: 用户安装目录（DLL 签名可升级为 Verified）
        dirs.Add((GlobalPluginsDir, PluginTrustLevel.External));

        // External: 项目级
        dirs.Add((LocalPluginsDir, PluginTrustLevel.External));

        // External: 环境变量
        var envDir = Environment.GetEnvironmentVariable("NOVEL_PLUGINS_DIR");
        if (envDir is not null)
            dirs.Add((envDir, PluginTrustLevel.External));

        return dirs;
    }

    /// <summary>
    /// 在指定目录中查找第一个 .csproj 文件。
    /// </summary>
    /// <param name="dir">搜索目录</param>
    /// <returns>找到的 .csproj 路径，未找到返回 null</returns>
    private static string? FindCsproj(string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        var csprojs = Directory.GetFiles(dir, "*.csproj");
        return csprojs.Length > 0 ? csprojs[0] : null;
    }
}
