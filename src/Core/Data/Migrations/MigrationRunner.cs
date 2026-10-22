using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Core.Shared;
using Microsoft.Extensions.Logging;

namespace Core.Data.Migrations;

/// <summary>
/// 数据迁移运行器。启动时检测已应用的迁移记录，按序执行所有未执行的迁移。
/// 每次成功迁移后记录一条迁移记录到 version.json（EF Core 风格），
/// 而非仅更新一个版本号数字。
/// 每次执行会写入 migration.log 到 ~/.mythology/，记录每步耗时和结果。
/// </summary>
public class MigrationRunner(string mythologyDir, string novelsRoot, ILogger? logger = null)
{
    /// <summary>
    /// version.json 专用序列化选项：基于 JsonHelper 但开启缩进，方便人类阅读。
    /// </summary>
    private static readonly JsonSerializerOptions VersionJsonOpts = new(JsonHelper.JsonOptions)
    {
        WriteIndented = true,
    };

    private string VersionFilePath => Path.Combine(mythologyDir, "version.json");
    private string LogFilePath => Path.Combine(mythologyDir, "migration.log");

    /// <summary>运行所有待执行的迁移。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var migrations = DiscoverMigrations().OrderBy(m => m.Version).ToList();
        if (migrations.Count == 0)
            return;

        var history = ReadAppliedMigrations();
        // 只有**成功**的迁移才算已应用：失败记录必须留下重试机会
        // （否则修复后重启会被当作「已应用」跳过，数据永久停在半迁移状态）。
        // "converted" = 旧格式 version.json 里 v<=N 的迁移（按已应用处理，不重跑）。
        var appliedVersions = new HashSet<int>(
            history
                .AppliedMigrations.Where(m => m.Result is "success" or "converted")
                .Select(m => m.Version)
        );

        var pending = migrations.Where(m => !appliedVersions.Contains(m.Version)).ToList();
        if (pending.Count == 0)
        {
            var lastVersion =
                history.AppliedMigrations.Count > 0
                    ? history.AppliedMigrations.Max(m => m.Version)
                    : 0;
            logger?.LogInformation("所有迁移已应用 (v{Version})，无需迁移。", lastVersion);
            return;
        }

        var logFile = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        await using var _ = logFile.ConfigureAwait(false);

        var firstPendingVersion = pending.First().Version;
        await logFile.WriteLineAsync(
            $"=== 迁移开始 @ {DateTime.UtcNow:O}，待执行 {pending.Count} 个迁移 ==="
        );

        // 首次迁移前备份（没有任何迁移记录时）
        if (history.AppliedMigrations.Count == 0)
            await BackupAsync(ct);

        logger?.LogInformation(
            "待执行 {Count} 个迁移（从 {Name} 开始）。",
            pending.Count,
            pending.First().Name
        );

        foreach (var migration in pending)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            logger?.LogInformation(
                "执行迁移 {Name}: {Description} ...",
                migration.Name,
                migration.Description
            );
            await logFile.WriteLineAsync(
                $"[{DateTime.UtcNow:O}] {migration.Name}: {migration.Description}"
            );

            migration.Log = logFile;
            try
            {
                await migration.UpAsync(mythologyDir, novelsRoot, ct);
                sw.Stop();

                // 记录成功迁移
                var record = new MigrationRecord
                {
                    Version = migration.Version,
                    Name = migration.Name,
                    Description = migration.Description,
                    AppliedAt = DateTime.UtcNow,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = "success",
                };
                RecordAppliedMigration(history, record);

                logger?.LogInformation(
                    "迁移 {Name} 完成（耗时 {Duration:F1}s）。",
                    migration.Name,
                    sw.Elapsed.TotalSeconds
                );
                await logFile.WriteLineAsync($"  ✓ 完成 (耗时 {sw.Elapsed.TotalSeconds:F1}s)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger?.LogError(ex, "迁移 {Name} 失败: {Message}", migration.Name, ex.Message);
                await logFile.WriteLineAsync(
                    $"  ✗ 失败 (耗时 {sw.Elapsed.TotalSeconds:F1}s): {ex.Message}"
                );

                // 记录失败迁移
                var record = new MigrationRecord
                {
                    Version = migration.Version,
                    Name = migration.Name,
                    Description = migration.Description,
                    AppliedAt = DateTime.UtcNow,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = "failed",
                };
                RecordAppliedMigration(history, record);

                throw new InvalidOperationException(
                    $"迁移 {migration.Name} 失败，已停止后续迁移。修复后重新启动即可重试（失败记录不会阻止重跑）。"
                );
            }
        }

        var lastApplied = history
            .AppliedMigrations.Where(m => m.Result == "success")
            .OrderByDescending(m => m.Version)
            .FirstOrDefault();
        var lastVer = lastApplied?.Version ?? 0;
        logger?.LogInformation("所有迁移完成，最新已应用版本 v{Version}。", lastVer);
        await logFile.WriteLineAsync(
            $"=== 迁移完成 @ {DateTime.UtcNow:O}，最新版本 v{lastVer} ==={Environment.NewLine}"
        );
    }

    /// <summary>从程序集扫描所有 IDataMigration 实现。</summary>
    private List<IDataMigration> DiscoverMigrations()
    {
        return Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(t =>
                t is { IsAbstract: false, IsInterface: false }
                && typeof(IDataMigration).IsAssignableFrom(t)
            )
            .Select(t => (IDataMigration)Activator.CreateInstance(t)!)
            .ToList();
    }

    /// <summary>读取已应用的迁移记录列表。</summary>
    private MigrationHistory ReadAppliedMigrations()
    {
        if (!File.Exists(VersionFilePath))
            return new MigrationHistory();

        try
        {
            var json = File.ReadAllText(VersionFilePath);
            var doc = JsonDocument.Parse(json);

            // 账本格式：{"applied-migrations": [...]}（全仓统一 kebab-case）
            if (doc.RootElement.TryGetProperty("applied-migrations", out _))
            {
                return JsonSerializer.Deserialize<MigrationHistory>(json, VersionJsonOpts)
                    ?? new MigrationHistory();
            }

            // 旧格式兼容：{"version": N} → 转换为新格式
            // 读取所有已知迁移，假设所有 v <= N 的都已应用
            if (doc.RootElement.TryGetProperty("version", out var vEl))
            {
                var oldVersion = vEl.GetInt32();
                logger?.LogInformation(
                    "检测到旧格式 version.json (v{Version})，正在转换为新格式...",
                    oldVersion
                );

                var migrations = DiscoverMigrations().OrderBy(m => m.Version).ToList();
                var applied = migrations
                    .Where(m => m.Version <= oldVersion)
                    .Select(m => new MigrationRecord
                    {
                        Version = m.Version,
                        Name = m.Name,
                        Description = m.Description,
                        AppliedAt = DateTime.UtcNow,
                        DurationMs = 0,
                        Result = "converted",
                    })
                    .ToList();

                var history = new MigrationHistory { AppliedMigrations = applied };
                WriteHistory(history);
                return history;
            }

            return new MigrationHistory();
        }
        catch
        {
            return new MigrationHistory();
        }
    }

    /// <summary>追加一条迁移记录并写入 version.json（同版本旧记录被替换，重试不会堆积）。</summary>
    private void RecordAppliedMigration(MigrationHistory history, MigrationRecord record)
    {
        // 必须把记录累积回 history 再整体写入；
        // 否则每次覆盖写只保留最后一条（此前被 2 个迁移的测试侥幸掩盖）。
        history.AppliedMigrations.RemoveAll(m => m.Version == record.Version);
        history.AppliedMigrations.Add(record);
        WriteHistory(history);
    }

    /// <summary>写入 version.json。</summary>
    private void WriteHistory(MigrationHistory history)
    {
        Directory.CreateDirectory(mythologyDir);
        var json = JsonSerializer.Serialize(history, VersionJsonOpts);
        File.WriteAllText(VersionFilePath, json);
    }

    /// <summary>首次迁移前创建带时间戳的备份。</summary>
    private async Task BackupAsync(CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(mythologyDir, "backups", timestamp);
        Directory.CreateDirectory(backupDir);

        // 备份 sessions 目录
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        if (Directory.Exists(sessionsDir))
        {
            var target = Path.Combine(backupDir, "sessions");
            await CopyDirectoryAsync(sessionsDir, target, ct);
        }

        // 备份 config.yaml
        var configFile = Path.Combine(mythologyDir, "config.yaml");
        if (File.Exists(configFile))
            File.Copy(configFile, Path.Combine(backupDir, "config.yaml"));

        // 备份 vectors.db
        var dbFile = Path.Combine(mythologyDir, "vectors.db");
        if (File.Exists(dbFile))
            File.Copy(dbFile, Path.Combine(backupDir, "vectors.db"));

        logger?.LogInformation("迁移前备份已创建: {Path}", backupDir);
    }

    private static async Task CopyDirectoryAsync(
        string sourceDir,
        string destDir,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            await using var src = File.OpenRead(file);
            await using var dst = File.Create(destFile);
            await src.CopyToAsync(dst, ct);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(dir, Path.Combine(destDir, Path.GetFileName(dir)), ct);
        }
    }
}
