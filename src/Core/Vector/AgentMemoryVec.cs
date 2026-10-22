using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Core.Vector;

/// <summary>
/// ANN 两阶段（B 选项）— sqlite-vec vec0 虚拟表 agent_memory_vec 同步维护。
///
/// 设计：ANN 只做候选加速器，不做语义替换。RecallAsync 行数 ≥ AnnMinRows 时
/// 先由 vec0 MATCH 取 top-M=max(500,10k) 候选，再用 corpus 归一化矩阵精确点积重评。
/// BM25 与规则通道恒全量，不受 ANN 影响（显式规则遗漏率=0 红线不破）。
///
/// vec0 约定：辅助列不能作 DELETE 条件，按 rowid 删；写入 DELETE+INSERT（不用 INSERT OR REPLACE）。
/// rowid 由 memory_id 经 SHA-256 截断派生（稳定业务键，禁运行时哈希）。
/// 本表是可重建派生数据：加载失败/数据落后时调用方回退全量精确扫描，BackfillAsync 可重建。
/// </summary>
internal static class AgentMemoryVec
{
    /// <summary>
    /// 维度漂移防护 + 建表。vec0 表创建后维度固定不可 ALTER；
    /// 已有表维度与当前配置不一致时 DROP 重建（镜像 SqliteRetrievalStore 模式）。
    /// </summary>
    public static async Task EnsureTableAsync(
        SqliteConnection conn,
        int dimension,
        CancellationToken ct = default
    )
    {
        await DropTableIfDimensionMismatchAsync(conn, dimension, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $@"CREATE VIRTUAL TABLE IF NOT EXISTS agent_memory_vec USING vec0(
                embedding float[{dimension}],
                memory_id text
              );";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>写入同步：DELETE+INSERT（vec0 不支持 INSERT OR REPLACE）。失败由调用方决定策略。</summary>
    public static async Task SyncUpsertAsync(
        SqliteConnection conn,
        string memoryId,
        float[] embedding,
        CancellationToken ct = default
    )
    {
        var rid = DeriveRowId(memoryId);
        var vectorStr = FormatVector(embedding);

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM agent_memory_vec WHERE rowid = $rid";
        del.Parameters.AddWithValue("$rid", rid);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = conn.CreateCommand();
        ins.CommandText =
            "INSERT INTO agent_memory_vec(rowid, embedding, memory_id) VALUES ($rid, $vec, $mid)";
        ins.Parameters.AddWithValue("$rid", rid);
        ins.Parameters.AddWithValue("$vec", vectorStr);
        ins.Parameters.AddWithValue("$mid", memoryId);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public static async Task SyncDeleteAsync(
        SqliteConnection conn,
        string memoryId,
        CancellationToken ct = default
    )
    {
        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM agent_memory_vec WHERE rowid = $rid";
        del.Parameters.AddWithValue("$rid", DeriveRowId(memoryId));
        await del.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ANN 候选查询：top-m 记忆 id。
    /// 失败返回 null（调用方回退全量精确路径，正确性无损）。
    /// </summary>
    public static async Task<List<(string MemoryId, float Distance)>?> SearchAsync(
        SqliteConnection conn,
        float[] qvec,
        int m,
        CancellationToken ct = default
    )
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT memory_id, distance FROM agent_memory_vec "
                + "WHERE embedding MATCH $q AND k = $m";
            cmd.Parameters.AddWithValue("$q", FormatVector(qvec));
            cmd.Parameters.AddWithValue("$m", m);

            var results = new List<(string, float)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add((reader.GetString(0), reader.GetFloat(1)));
            return results;
        }
        catch (SqliteException)
        {
            return null; // MATCH 语法不支持 / 表损坏等 → 回退全量
        }
    }

    /// <summary>
    /// 一次性回填存量 agent_memories 的 embedding 到 vec 表（幂等：SyncUpsert 覆盖写）。
    /// 损坏 BLOB（长度 %4 != 0 或 != dim*4）跳过——禁止静默造零向量（VEC-LOCAL §2.6 红线）。
    /// </summary>
    /// <returns>成功回填条数。</returns>
    public static async Task<int> BackfillAsync(
        SqliteConnection conn,
        int dimension,
        CancellationToken ct = default
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, embedding FROM agent_memories WHERE embedding IS NOT NULL";

        var rows = new List<(string Id, byte[] Blob)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), (byte[])reader.GetValue(1)));
        }

        var count = 0;
        foreach (var (id, blob) in rows)
        {
            if (blob.Length % 4 != 0 || blob.Length != dimension * 4)
                continue; // 损坏/维度不符 BLOB：跳过，不造零向量
            var vec = new float[dimension];
            Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
            await SyncUpsertAsync(conn, id, vec, ct);
            count++;
        }
        return count;
    }

    /// <summary>从 memory_id 推导稳定 vec0 rowid（SHA-256 截断，非运行时哈希）。</summary>
    private static long DeriveRowId(string memoryId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(memoryId));
        return Math.Abs(BitConverter.ToInt64(bytes, 0));
    }

    private static string FormatVector(float[] v)
    {
        var sb = new StringBuilder(v.Length * 12 + 2);
        sb.Append('[');
        for (var i = 0; i < v.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(v[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>sqlite_master 读建表 SQL，向量维度与当前配置不符则 DROP。</summary>
    private static async Task DropTableIfDimensionMismatchAsync(
        SqliteConnection conn,
        int dimension,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agent_memory_vec'";
        var sql = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(sql))
            return;

        // 建表 SQL 形如 ...float[{n}]...；提取 n 比较
        const string marker = "float[";
        var start = sql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return;
        start += marker.Length;
        var end = sql.IndexOf(']', start);
        if (end <= start)
            return;
        if (
            !int.TryParse(
                sql[start..end],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var existingDim
            )
        )
            return;

        if (existingDim != dimension)
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS agent_memory_vec";
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}
