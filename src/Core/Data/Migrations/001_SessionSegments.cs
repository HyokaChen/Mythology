using System.Text.Json;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移 001：将会话 JSONL 文件从单文件格式迁移为分片目录格式。
///
/// 改造前：sessions/--novels-X--/{sessionId}.jsonl
/// 改造后：sessions/--novels-X--/{timestamp}_{sessionId}/000000.jsonl + MANIFEST.json
///
/// 幂等：已使用新格式的会话目录跳过不做；目录已存在且有 MANIFEST.json 时跳过。
/// 验证：迁移后自动扫描残留旧格式文件，确保迁移真正生效，防止"假装成功"。
/// </summary>
public sealed class SessionSegments : IDataMigration
{
    public int Version => 1;
    public string Name => "001_SessionSegments";
    public string Description =>
        "将会话 JSONL 从单文件迁移为分片目录格式 (MANIFEST.json + 分片文件)";
    public TextWriter? Log { get; set; }

    // 迁移器输入是历史数据（camelCase + snake 枚举），故用只读遗留选项
    private static readonly JsonSerializerOptions JsonOpts = JsonlSerializer.LegacyReadOptions;

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            Log?.WriteLine("  sessions 目录不存在，跳过迁移。");
            return;
        }

        var totalMigrated = 0;
        var totalSkipped = 0;
        var totalErrors = 0;

        foreach (var novelDir in Directory.GetDirectories(sessionsDir))
        {
            ct.ThrowIfCancellationRequested();

            var dirName = Path.GetFileName(novelDir);
            if (dirName.StartsWith("audit_"))
                continue;

            Log?.WriteLine($"  扫描小说目录: {dirName}");

            // 扫描旧格式文件
            foreach (var jsonlPath in Directory.GetFiles(novelDir, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();

                var fileName = Path.GetFileNameWithoutExtension(jsonlPath);
                if (fileName.StartsWith("audit_"))
                    continue;

                // 使用旧文件的创建时间作为时间戳前缀
                var fi = new FileInfo(jsonlPath);
                var timestamp = fi.CreationTimeUtc.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
                var sessionDirName = $"{timestamp}_{fileName}";
                var newDir = Path.Combine(novelDir, sessionDirName);
                var manifestPath = Path.Combine(newDir, "MANIFEST.json");

                // 幂等跳过：目标目录和 MANIFEST 已存在（也检查旧的无时间戳目录）
                if (Directory.Exists(newDir) && File.Exists(manifestPath))
                {
                    totalSkipped++;
                    continue;
                }
                // 检查旧的无时间戳目录（兼容之前可能创建的）
                var legacyDir = Path.Combine(novelDir, fileName);
                if (
                    Directory.Exists(legacyDir)
                    && File.Exists(Path.Combine(legacyDir, "MANIFEST.json"))
                )
                {
                    totalSkipped++;
                    continue;
                }

                try
                {
                    await MigrateSessionFileAsync(jsonlPath, newDir, fileName, ct);
                    totalMigrated++;
                    Log?.WriteLine(
                        $"    ✓ {fileName}.jsonl → {sessionDirName}/ (分片格式，带时间戳)"
                    );
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    Log?.WriteLine($"    ✗ {fileName}.jsonl 迁移失败: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  迁移完成: {totalMigrated} 个会话已迁移, {totalSkipped} 个已跳过, {totalErrors} 个失败"
        );

        // ═══════════════════════════════════════════════════════════════
        //  迁移后验证：扫描 sessions 目录，确认没有旧格式 JSONL 文件残留
        //  防止迁移"假装成功"——如果迁移逻辑有 bug 导致漏掉文件，
        //  这里会抛出异常，阻止 MigrationRunner 写入 version.json。
        // ═══════════════════════════════════════════════════════════════
        if (totalMigrated > 0 || totalErrors > 0)
        {
            // 有实际迁移动作或失败，验证残留
            var remaining = FindRemainingFlatJsonlFiles(sessionsDir);
            if (remaining.Count > 0)
            {
                var files = string.Join(", ", remaining);
                throw new InvalidOperationException(
                    $"迁移后仍有 {remaining.Count} 个旧格式文件残留，迁移未完全生效。残留文件: {files}"
                );
            }
        }
        else if (Directory.Exists(sessionsDir))
        {
            // 没有迁移任何文件 → 检查是否有应该被迁移的文件
            var remaining = FindRemainingFlatJsonlFiles(sessionsDir);
            if (remaining.Count > 0)
            {
                var files = string.Join(", ", remaining);
                throw new InvalidOperationException(
                    $"发现 {remaining.Count} 个旧格式 JSONL 文件但未进行迁移，迁移无效。残留文件: {files}"
                );
            }

            // 也没有旧格式文件 → 检查是否已经有分片目录（数据已是最新格式）
            var hasNewFormat = false;
            foreach (var novelDir in Directory.GetDirectories(sessionsDir))
            {
                foreach (var subDir in Directory.GetDirectories(novelDir))
                {
                    if (File.Exists(Path.Combine(subDir, "MANIFEST.json")))
                    {
                        hasNewFormat = true;
                        break;
                    }
                }
                if (hasNewFormat)
                    break;
            }

            if (!hasNewFormat)
            {
                Log?.WriteLine(
                    "  ⚠ 警告: sessions 目录存在但未发现任何会话数据（旧格式 JSONL 或新格式分片目录均无）"
                );
            }
        }
        // 如果 sessions 目录不存在，视为新用户，迁移无事可做但合法
    }

    /// <summary>
    /// 扫描 sessions 目录，找到所有顶层旧格式 JSONL 文件（排除 audit_*）。
    /// </summary>
    private static List<string> FindRemainingFlatJsonlFiles(string sessionsDir)
    {
        var remaining = new List<string>();
        if (!Directory.Exists(sessionsDir))
            return remaining;

        foreach (var novelDir in Directory.GetDirectories(sessionsDir))
        {
            var dirName = Path.GetFileName(novelDir);
            if (dirName.StartsWith("audit_"))
                continue;

            foreach (var jsonlFile in Directory.GetFiles(novelDir, "*.jsonl"))
            {
                var fileName = Path.GetFileNameWithoutExtension(jsonlFile);
                if (fileName.StartsWith("audit_"))
                    continue;
                remaining.Add(jsonlFile);
            }
        }

        return remaining;
    }

    /// <summary>
    /// 将单个 JSONL 文件迁移为分片目录格式。
    /// </summary>
    private static async Task MigrateSessionFileAsync(
        string jsonlPath,
        string newDir,
        string sessionId,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(newDir);
        var targetPath = Path.Combine(newDir, "000000.jsonl");

        // 移动旧文件到新目录
        File.Move(jsonlPath, targetPath);

        // 提取元数据：title, messageCount, createdAt, updatedAt
        string? title = null;
        var createdAt = DateTime.MaxValue;
        var updatedAt = DateTime.MinValue;
        var messageCount = 0;
        var entryCount = 0;
        var maxOrder = -1;

        try
        {
            var text = await File.ReadAllTextAsync(targetPath, ct);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                entryCount++;

                try
                {
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                    if (probe?.Type == JsonlEntryType.Message)
                    {
                        messageCount++;
                        var msg = JsonSerializer.Deserialize<JsonlMessageEntry>(line, JsonOpts);
                        if (msg is not null)
                        {
                            if (msg.Order > maxOrder)
                                maxOrder = msg.Order;
                            if (title is null && msg.Role == "user")
                            {
                                var firstLine = msg.Content.Split('\n')[0].Trim();
                                title = firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
                            }
                            if (DateTime.TryParse(msg.Timestamp, out var dt))
                            {
                                if (dt < createdAt)
                                    createdAt = dt;
                                if (dt > updatedAt)
                                    updatedAt = dt;
                            }
                        }
                    }
                    else if (probe?.Type is not null)
                    {
                        // 非 message 条目：尝试从 timestamp 字段提取时间
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsEl))
                        {
                            var ts = tsEl.GetString();
                            if (ts is not null && DateTime.TryParse(ts, out var dt))
                            {
                                if (dt < createdAt)
                                    createdAt = dt;
                                if (dt > updatedAt)
                                    updatedAt = dt;
                            }
                        }
                    }
                }
                catch
                {
                    // 单行解析失败不影响整体
                }
            }
        }
        catch
        {
            // 解析失败时使用文件时间
        }

        if (createdAt == DateTime.MaxValue)
            createdAt = new FileInfo(targetPath).CreationTimeUtc;
        if (updatedAt == DateTime.MinValue)
            updatedAt = new FileInfo(targetPath).LastWriteTimeUtc;
        if (maxOrder < 0)
            maxOrder = 0;

        // 从文件名推断 novelId（通过 novelDir 命名规则）
        var novelDir = Path.GetDirectoryName(newDir)!;
        var novelId = InferNovelId(novelDir) ?? sessionId;

        var manifest = new SessionManifest
        {
            Version = 1,
            SessionId = sessionId,
            NovelId = novelId,
            Title = title ?? "",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Segments =
            [
                new ManifestSegmentRecord
                {
                    Seq = 0,
                    File = "000000.jsonl",
                    StartOrder = 0,
                    EndOrder = maxOrder,
                    MessageCount = messageCount,
                    EntryCount = entryCount,
                    FileSize = new FileInfo(targetPath).Length,
                    CompactEntryId = null,
                    HasCompact = false,
                    CreatedAt = createdAt,
                },
            ],
            ActiveSegment = 0,
            CompactCount = 0,
            TotalEntryCount = entryCount,
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);
        await File.WriteAllTextAsync(Path.Combine(newDir, "MANIFEST.json"), manifestJson, ct);
    }

    /// <summary>
    /// 从会话目录名推断 novelId。
    /// 目录名格式：--novels-{encodedTitle}--，需要从 novel.yaml 反向查找 novelId。
    /// 无法推断时返回 null（由 FileSessionStore 的 GetNovelIdFromSessionDir 逻辑处理）。
    /// </summary>
    private static string? InferNovelId(string novelDir)
    {
        var dirName = Path.GetFileName(novelDir);
        if (
            dirName.StartsWith("--novels-", StringComparison.Ordinal)
            && dirName.EndsWith("--", StringComparison.Ordinal)
        )
        {
            var encodedTitle = dirName[9..^2];
            return encodedTitle;
        }
        return dirName;
    }
}
