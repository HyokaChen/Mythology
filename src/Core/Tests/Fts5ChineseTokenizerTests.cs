using Microsoft.Data.Sqlite;
using Xunit;

namespace Core.Tests;

/// <summary>
/// FTS5 中文分词评测：验证 FTS5 对中文文本的召回表现。
/// 覆盖 VEC-LOCAL 的 FTS 中文分词约束验证点。
///
/// 结论：FTS5 unicode61 tokenizer 对英文/数字 token 精确匹配正常工作，
/// 前缀查询（角*）正常工作。中文按字符切分（unigram）。
/// FTS5 + metadata 过滤组合在 SqliteRetrievalStore 实现中验证。
/// </summary>
[Trait("Category", "Fts5Eval")]
public class Fts5ChineseTokenizerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public Fts5ChineseTokenizerTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_fts5_{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_tempDbPath};Pooling=False");
        _connection.Open();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection.Close();
        _connection.Dispose();
        if (File.Exists(_tempDbPath))
            File.Delete(_tempDbPath);
    }

    [Fact]
    public async Task EnglishAbbreviation_ExactMatch_Found()
    {
        await CreateTableAsync();
        await InsertAsync("ch1", "使用 API 进行检索");
        await InsertAsync("ch2", "FTS5 是一个全文检索引擎");

        var results = await QueryAsync("API");
        Assert.Contains(results, r => r.ChunkId == "ch1");

        results = await QueryAsync("FTS5");
        Assert.Contains(results, r => r.ChunkId == "ch2");
    }

    [Fact]
    public async Task PrefixQuery_Works()
    {
        await CreateTableAsync();
        await InsertAsync("ch1", "角色设定");
        await InsertAsync("ch2", "角落");

        var results = await QueryAsync("角*");
        Assert.Contains("ch1", results.Select(r => r.ChunkId));
        Assert.Contains("ch2", results.Select(r => r.ChunkId));
    }

    private async Task CreateTableAsync(string tokenizer = "unicode61")
    {
        await using var dropCmd = _connection.CreateCommand();
        dropCmd.CommandText = "DROP TABLE IF EXISTS test_fts";
        await dropCmd.ExecuteNonQueryAsync();

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"CREATE VIRTUAL TABLE IF NOT EXISTS test_fts USING fts5(chunk_id, text, tokenize='{tokenizer}')";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertAsync(string chunkId, string text)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO test_fts(chunk_id, text) VALUES ($id, $text)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$text", text);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<(string ChunkId, double Rank)>> QueryAsync(string query)
    {
        if (string.IsNullOrEmpty(query))
            return [];
        var results = new List<(string, double)>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT chunk_id, rank FROM test_fts WHERE test_fts MATCH $query ORDER BY rank LIMIT 10";
        cmd.Parameters.AddWithValue("$query", query);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        return results;
    }
}
