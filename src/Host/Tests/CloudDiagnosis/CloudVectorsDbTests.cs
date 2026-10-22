using CloudDiagnosis;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>R-1：vectors.db 连接工厂——WAL + busy_timeout 与 Core 侧约定一致（防回归）。</summary>
public sealed class CloudVectorsDbTests
{
    [Fact]
    public void Open_SetsWalJournalMode_AndBusyTimeout()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cloud_vectors_{Guid.NewGuid():N}.db");
        try
        {
            using var conn = CloudVectorsDb.Open(dbPath);

            using var journal = conn.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode";
            Assert.Equal("wal", journal.ExecuteScalar() as string);

            using var busy = conn.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout";
            Assert.Equal(30000L, Convert.ToInt64(busy.ExecuteScalar()));

            // 幂等：重复打开（多组件共库）不抛错且约定保持
            using var second = CloudVectorsDb.Open(dbPath);
            Assert.Equal("wal", journal.ExecuteScalar() as string);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(dbPath + suffix))
                    File.Delete(dbPath + suffix);
            }
        }
    }
}
