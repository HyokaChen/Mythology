namespace ClientPlatform;

using System.IO;

/// <summary>
/// 极简 .env 加载器：读取仓库根目录 <c>.env</c> 文件，将键值写入进程环境变量（仅未设置时写入）。
/// 无任何 NuGet 依赖；手动实现避免额外部署负担。
/// </summary>
public static class DotEnv
{
    /// <summary>
    /// 尝试从仓库根目录加载 <c>.env</c> 文件。文件不存在静默跳过，格式错误仅记录警告。
    /// 建议在 <see cref="Program.Main(string[])"/> 最早处调用。
    /// </summary>
    public static void TryLoad()
    {
        // 搜索优先级：cwd（启动目录）→ 可执行文件目录 → 逐级上溯至找到 .env
        var candidates = new List<string?>
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, ".env"));

        var envPath = candidates.Distinct().FirstOrDefault(File.Exists);
        if (envPath is null)
            return;

        try
        {
            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;
                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('\"');
                if (string.IsNullOrEmpty(key))
                    continue;
                // 仅未设置时写入：环境变量优先级高于 .env，不妨碍用户显式 export
                if (Environment.GetEnvironmentVariable(key) is null or "")
                    Environment.SetEnvironmentVariable(key, value);
            }
        }
        catch (Exception ex)
        {
            // 非致命：静默跳过，避免 .env 读取失败阻断启动
            Console.Error.WriteLine($"[Warning] 读取 .env 失败: {ex.Message}");
        }
    }
}
