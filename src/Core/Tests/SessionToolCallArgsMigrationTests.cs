using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Data;
using Core.Data.Migrations;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 012：磁盘上的历史会话里，空/截断的 tool-call arguments 必须被持久化修复，
/// 否则下一轮 OpenAI 兼容请求会以 400 arguments must be valid JSON 拒绝整段历史。
/// </summary>
public sealed class SessionToolCallArgsMigrationTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _mythologyDir;
    private readonly string _sessionFile;

    public SessionToolCallArgsMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"session-012-{Guid.NewGuid():N}");
        _mythologyDir = Path.Combine(_root, ".mythology");
        var sessionDir = Path.Combine(
            _mythologyDir,
            "sessions",
            "--novels-n1--",
            "2026-01-01T00-00-00-000Z_sess1"
        );
        Directory.CreateDirectory(sessionDir);
        _sessionFile = Path.Combine(sessionDir, "000000.jsonl");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RepairsEmptyAndTruncatedArguments_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var toolCalls = JsonSerializer.Serialize(
            new[]
            {
                new ToolCallItemDto
                {
                    Id = "c1",
                    Name = "create_outline",
                    Arguments = "",
                },
                new ToolCallItemDto
                {
                    Id = "c2",
                    Name = "probe",
                    Arguments = """{"x":1}""",
                },
                new ToolCallItemDto
                {
                    Id = "c3",
                    Name = "probe",
                    Arguments = """{"x":1""",
                },
            },
            JsonlSerializer.Options
        );
        var line = new JsonObject
        {
            ["type"] = "message",
            ["id"] = "e1",
            ["order"] = 0,
            ["tool-calls"] = toolCalls,
        };
        await File.WriteAllTextAsync(
            _sessionFile,
            line.ToJsonString(JsonlSerializer.Options) + "\n",
            ct
        );

        var migration = new SessionToolCallArguments { Log = new StringWriter() };
        await migration.UpAsync(_mythologyDir, Path.Combine(_root, "novels"), ct);

        var items = ReadToolCalls(await File.ReadAllTextAsync(_sessionFile, ct));
        Assert.Equal("{}", items[0].Arguments);
        Assert.Equal("""{"x":1}""", items[1].Arguments);
        using var repaired = JsonDocument.Parse(items[2].Arguments!);
        Assert.Equal(JsonValueKind.Object, repaired.RootElement.ValueKind);

        // 幂等：第二次执行不再改写文件
        var before = await File.ReadAllTextAsync(_sessionFile, ct);
        await migration.UpAsync(_mythologyDir, Path.Combine(_root, "novels"), ct);
        Assert.Equal(before, await File.ReadAllTextAsync(_sessionFile, ct));
    }

    private static List<ToolCallItemDto> ReadToolCalls(string jsonl)
    {
        var obj = JsonNode.Parse(jsonl.Trim())!.AsObject();
        var toolCallsJson = (string)obj["tool-calls"]!;
        return ToolCallItemDto.DeserializeList(toolCallsJson) ?? [];
    }
}
