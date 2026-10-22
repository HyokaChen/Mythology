using System.Text.Json;
using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移 002：给旧 compact 条目补充 blockId 与 compressedMessageIds，实现历史可恢复。
///
/// 背景：B2 引入 BlockId/CompressedMessageIds 字段后，新写入的 compact 条目自带这些字段；
/// 但迁移前创建的旧 compact 条目缺失它们，decompress 无法定位/恢复原文。
///
/// 推导原理（分片滚动是 append-only）：
/// - compact 滚动分片时，新分片 = [compact 行 + 保留消息行]，旧分片不删除，
///   被压缩消息的原文行永远保留在旧分片中。
/// - 对每个 compact 条目，其覆盖范围 = (上一个 compact 保留起点, 本 compact 保留起点)
///   之间的消息区间；首个 compact 从会话起点开始。
/// - CompressedMessageIds = 该区间内所有消息条目的 entryId；
///   BlockId = b{分片 seq}（与 FileSessionStore.CompactSession 的分配规则一致，天然唯一）。
///
/// 幂等：已有 blockId 的 compact 条目跳过（不修改）。
/// 验证：迁移后二次扫描全部分片，确认所有 compact 条目都有 blockId；有残留则抛异常
/// 阻止 MigrationRunner 写入 version.json（防\"假装成功\"，与 001_SessionSegments 同模式）。
/// </summary>
public sealed class CompactBlocks : IDataMigration
{
    public int Version => 2;
    public string Name => "002_CompactBlocks";
    public string Description =>
        "给旧 compact 条目补充 blockId 与 compressedMessageIds（历史可恢复）";
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

            foreach (var sessionDir in Directory.GetDirectories(novelDir))
            {
                ct.ThrowIfCancellationRequested();
                var dirBase = Path.GetFileName(sessionDir);
                if (dirBase.StartsWith("audit_"))
                    continue;

                var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var (migrated, skipped) = await MigrateSessionAsync(sessionDir, ct);
                    totalMigrated += migrated;
                    totalSkipped += skipped;
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    Log?.WriteLine($"    ✗ {dirBase} 迁移失败: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  迁移完成: {totalMigrated} 个 compact 已补充, {totalSkipped} 个已跳过, {totalErrors} 个失败"
        );

        // ═══════════════════════════════════════════════════════════════
        //  迁移后验证：二次扫描所有分片，确认所有 compact 条目都有 blockId。
        //  防止\"假装成功\"——有残留则抛异常，阻止 version.json 写入。
        // ═══════════════════════════════════════════════════════════════
        if (totalMigrated > 0 || totalErrors > 0)
        {
            var remaining = await CountMissingBlockIdAsync(sessionsDir, ct);
            if (remaining > 0)
            {
                throw new InvalidOperationException(
                    $"迁移后仍有 {remaining} 个 compact 条目缺少 blockId，迁移未完全生效。"
                );
            }
        }
    }

    /// <summary>
    /// 迁移单个会话目录：读全部分片 → 推导 compact 覆盖区间 → 重写含 compact 的分片。
    /// </summary>
    /// <returns>(迁移的 compact 数, 跳过的 compact 数)。</returns>
    private async Task<(int Migrated, int Skipped)> MigrateSessionAsync(
        string sessionDir,
        CancellationToken ct
    )
    {
        var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
        SessionManifest? manifest = null;
        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            manifest = JsonSerializer.Deserialize<SessionManifest>(manifestJson, JsonOpts);
        }
        catch
        {
            // MANIFEST 解析失败：无分片信息，跳过（视为无分片格式）
        }

        if (manifest?.Segments is null || manifest.Segments.Count == 0)
            return (0, 0);

        // 1. 按 Seq 升序读全部分片，收集消息 + compact 条目
        //    每条目：entryId, order, type, (message | compact), 所在分片 seq
        var messages = new List<(string EntryId, int Order)>();
        var compacts =
            new List<(int SegmentSeq, string EntryId, int Order, string? FirstKeptEntryId)>();

        foreach (var seg in manifest.Segments.OrderBy(s => s.Seq))
        {
            ct.ThrowIfCancellationRequested();
            var segPath = Path.Combine(sessionDir, seg.File);
            if (!File.Exists(segPath))
                continue;

            var lines = await File.ReadAllLinesAsync(segPath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                    switch (probe?.Type)
                    {
                        case JsonlEntryType.Message:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlMessageEntry>(line, JsonOpts);
                            if (dto is not null)
                                messages.Add((dto.Id, dto.Order));
                            break;
                        }
                        case JsonlEntryType.Compact:
                        {
                            var dto = JsonSerializer.Deserialize<JsonlCompactEntry>(line, JsonOpts);
                            if (dto is not null)
                            {
                                compacts.Add((seg.Seq, dto.Id, dto.Order, dto.FirstKeptEntryId));
                            }
                            break;
                        }
                    }
                }
                catch
                {
                    // 单行解析失败不影响整体
                }
            }
        }

        if (compacts.Count == 0)
            return (0, 0);

        // 2. 推导每个 compact 的覆盖区间（按 Order 升序）
        //    id → order 映射（firstKeptEntryId 定位保留起点）
        var idToOrder = new Dictionary<string, int>(messages.Count, StringComparer.Ordinal);
        foreach (var (entryId, order) in messages)
            idToOrder[entryId] = order;

        var coverage = new List<(
            int SegmentSeq,
            string EntryId,
            string BlockId,
            List<string> MessageIds
        )>(compacts.Count);

        var prevKeptOrder = -1;
        foreach (var compact in compacts.OrderBy(c => c.Order))
        {
            var keptOrder = -1;
            if (compact.FirstKeptEntryId is not null)
                idToOrder.TryGetValue(compact.FirstKeptEntryId, out keptOrder);

            // 覆盖区间：(prevKeptOrder, keptOrder) 之间的消息
            var ids = messages
                .Where(m => m.Order > prevKeptOrder && m.Order < keptOrder)
                .Select(m => m.EntryId)
                .ToList();

            coverage.Add((compact.SegmentSeq, compact.EntryId, $"b{compact.SegmentSeq}", ids));

            if (keptOrder >= 0)
                prevKeptOrder = keptOrder;
        }

        // 3. 重写含 compact 的分片：compact 行补 blockId/compressedMessageIds，其余行原样
        var migrated = 0;
        var skipped = 0;
        var bySegment = coverage
            .GroupBy(c => c.SegmentSeq)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var seg in manifest.Segments.OrderBy(s => s.Seq))
        {
            if (!bySegment.TryGetValue(seg.Seq, out var segCompacts))
                continue;

            var segPath = Path.Combine(sessionDir, seg.File);
            var lines = await File.ReadAllLinesAsync(segPath, ct);
            var rewritten = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                    if (probe?.Type != JsonlEntryType.Compact)
                        continue;

                    var dto = JsonSerializer.Deserialize<JsonlCompactEntry>(line, JsonOpts);
                    if (dto is null)
                        continue;

                    // 幂等：已有 blockId 的 compact 跳过
                    if (!string.IsNullOrEmpty(dto.BlockId))
                    {
                        skipped++;
                        continue;
                    }

                    var match = segCompacts.FirstOrDefault(c => c.EntryId == dto.Id);
                    if (match.EntryId is null)
                        continue;

                    var updated = new JsonlCompactEntry
                    {
                        Type = dto.Type,
                        Id = dto.Id,
                        ParentId = dto.ParentId,
                        Timestamp = dto.Timestamp,
                        Summary = dto.Summary,
                        FirstKeptEntryId = dto.FirstKeptEntryId,
                        MessagesBefore = dto.MessagesBefore,
                        Order = dto.Order,
                        Reason = dto.Reason,
                        WillRetry = dto.WillRetry,
                        EstimatedTokensAfter = dto.EstimatedTokensAfter,
                        CompressedMessageIds = match.MessageIds,
                        BlockId = match.BlockId,
                    };
                    lines[i] = JsonSerializer.Serialize(updated, JsonOpts);
                    rewritten = true;
                    migrated++;
                }
                catch
                {
                    // 单行失败不影响其他行
                }
            }

            if (rewritten)
                await File.WriteAllLinesAsync(segPath, lines, ct);
        }

        return (migrated, skipped);
    }

    /// <summary>迁移后验证：扫描全部分片，统计缺少 blockId 的 compact 条目数。</summary>
    private static async Task<int> CountMissingBlockIdAsync(
        string sessionsDir,
        CancellationToken ct
    )
    {
        var missing = 0;
        foreach (var novelDir in Directory.GetDirectories(sessionsDir))
        {
            if (Path.GetFileName(novelDir).StartsWith("audit_"))
                continue;
            foreach (var sessionDir in Directory.GetDirectories(novelDir))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(sessionDir).StartsWith("audit_"))
                    continue;
                var manifestPath = Path.Combine(sessionDir, "MANIFEST.json");
                if (!File.Exists(manifestPath))
                    continue;
                foreach (var jsonlFile in Directory.GetFiles(sessionDir, "*.jsonl"))
                {
                    var lines = await File.ReadAllLinesAsync(jsonlFile, ct);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        try
                        {
                            var probe = JsonSerializer.Deserialize<JsonlTypeProbe>(line, JsonOpts);
                            if (probe?.Type != JsonlEntryType.Compact)
                                continue;
                            var dto = JsonSerializer.Deserialize<JsonlCompactEntry>(line, JsonOpts);
                            if (dto is not null && string.IsNullOrEmpty(dto.BlockId))
                                missing++;
                        }
                        catch
                        {
                            // 单行解析失败不计入
                        }
                    }
                }
            }
        }
        return missing;
    }
}
