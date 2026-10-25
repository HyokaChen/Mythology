using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Data.Migrations;
using Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class SessionInteractionFormatTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionFile;

    public SessionInteractionFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"interaction-migration-{Guid.NewGuid():N}");
        var sessionDir = Path.Combine(_tempDir, "sessions", "--novels-novel--", "--session--");
        Directory.CreateDirectory(sessionDir);
        _sessionFile = Path.Combine(sessionDir, "000000.jsonl");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { }
        return default;
    }

    [Fact]
    public async Task UpAsync_ConvertsAskUserAndVariantPrompts_Idempotently()
    {
        var assistantToolCall = new JsonObject
        {
            ["id"] = "call_1",
            ["name"] = "ask_user",
            ["arguments"] = new JsonObject
            {
                ["questions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["header"] = "方向",
                        ["question"] = "选择要保留的线索",
                        ["options"] = new JsonArray("身世", "师门", "旧案"),
                        ["multiSelect"] = true,
                    },
                },
            }.ToJsonString(),
        };

        var lines = new[]
        {
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m1",
                ["role"] = "assistant",
                ["content"] = "",
                ["tool-calls"] = new JsonArray(assistantToolCall).ToJsonString(),
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m2",
                ["role"] = "tool",
                ["content"] = "用户回复:\n  [方向] 选择要保留的线索\n    → 身世, 旧案",
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m3",
                ["role"] = "user",
                ["content"] = "**用户回复:**\n方向: 选择要保留的线索\n  → 身世, 旧案",
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m4",
                ["role"] = "user",
                ["content"] =
                    "用户从 explore 工具的结果中选择了变体「悲剧走向」（ID: var_003），并要求基于此版本继续修改。\n\n"
                    + "===== 探索类型 =====\n\n"
                    + "outline\n\n"
                    + "===== 应用目标 =====\n\n"
                    + "目标章节: 旧钟 (序号 12)\n\n"
                    + "===== 选中的变体内容 =====\n\n"
                    + "旧钟在雪夜响起。\n他回头。\n\n"
                    + "===== 用户修改要求 =====\n\n"
                    + "把结尾改得更克制，不要保留 <variant> 标签",
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m5",
                ["role"] = "user",
                ["content"] =
                    "用户从探索结果中选择了变体「主角方案」——一个角色方案。\n\n"
                    + "主角名叫旧钟。\n\n"
                    + "请根据以上内容，使用相应的工具（如 batch_create_characters）创建结构化实体。"
                    + "如果内容为大纲/风格描述，直接将其作为参考存入上下文，不需要创建实体。",
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m6",
                ["role"] = "user",
                ["content"] = "普通作者输入",
            }.ToJsonString(),
            "not json",
        };
        // 未变更的合法 JSON 行保留原排版（迁移只重写实际改动的行）。
        lines[5] =
            "{ \"type\": \"message\", \"id\": \"m6\", \"role\": \"user\", \"content\": \"普通作者输入\" }";

        await AtomicFile.WriteAllTextAsync(
            _sessionFile,
            string.Join("\n", lines) + "\n",
            TestContext.Current.CancellationToken
        );

        var migration = new SessionInteractionFormat();
        await migration.UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );

        var contents = ReadContents();
        Assert.Equal("User answers:\n方向: [身世, 旧案]", contents["m2"]);
        Assert.Equal("User answers:\n方向: [身世, 旧案]", contents["m3"]);
        Assert.Contains(
            "<variant id=\"var_003\" label=\"悲剧走向\" type=\"outline\" target=\"第12章「旧钟」\">",
            contents["m4"]
        );
        Assert.Contains("旧钟在雪夜响起。\n他回头。\n</variant>", contents["m4"]);
        Assert.Contains("===== 用户修改要求 =====", contents["m4"]);
        Assert.Contains("不要保留 <variant> 标签", contents["m4"]);
        Assert.Contains("<variant label=\"主角方案\" type=\"character\">", contents["m5"]);
        Assert.Contains("主角名叫旧钟。\n</variant>", contents["m5"]);
        Assert.Equal("普通作者输入", contents["m6"]);
        Assert.Contains(
            "not json",
            await File.ReadAllTextAsync(_sessionFile, TestContext.Current.CancellationToken)
        );

        var afterFirst = await File.ReadAllTextAsync(
            _sessionFile,
            TestContext.Current.CancellationToken
        );
        Assert.EndsWith("not json\n", afterFirst);
        Assert.Equal(lines.Length, afterFirst.Split('\n').Length - 1);
        Assert.Contains("\"content\": \"普通作者输入\"", afterFirst);
        await migration.UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            afterFirst,
            await File.ReadAllTextAsync(_sessionFile, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task UpAsync_MapsMultipleGroupsUsingAssistantMetadata()
    {
        var assistantToolCall = new JsonObject
        {
            ["id"] = "call_1",
            ["name"] = "ask_user",
            ["arguments"] = new JsonObject
            {
                ["questions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["header"] = "方向",
                        ["question"] = "选择要保留的线索",
                        ["multiSelect"] = true,
                    },
                    new JsonObject
                    {
                        ["header"] = "视角",
                        ["question"] = "用第几人称？",
                        ["multiSelect"] = false,
                    },
                },
            }.ToJsonString(),
        };
        var lines = new[]
        {
            new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = "",
                ["tool-calls"] = new JsonArray(assistantToolCall).ToJsonString(),
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m1",
                ["role"] = "tool",
                ["content"] =
                    "用户回复:\n"
                    + "  [方向] 选择要保留的线索\n"
                    + "    → 身世, 旧案\n"
                    + "  [视角] 用第几人称？\n"
                    + "    → 第三人称",
            }.ToJsonString(),
        };
        await AtomicFile.WriteAllTextAsync(
            _sessionFile,
            string.Join("\n", lines) + "\n",
            TestContext.Current.CancellationToken
        );

        await new SessionInteractionFormat().UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("User answers:\n方向: [身世, 旧案]\n视角: 第三人称", ReadContents()["m1"]);
    }

    [Fact]
    public async Task UpAsync_PreservesCrLfWithoutTrailingNewline()
    {
        var line1 = new JsonObject
        {
            ["type"] = "message",
            ["id"] = "m1",
            ["role"] = "user",
            ["content"] = "**用户回复:**\n方向: 选择要保留的线索\n  → 身世",
        }.ToJsonString();
        var line2 = new JsonObject
        {
            ["type"] = "message",
            ["id"] = "m2",
            ["role"] = "user",
            ["content"] = "普通作者输入",
        }.ToJsonString();
        await AtomicFile.WriteAllTextAsync(
            _sessionFile,
            line1 + "\r\n" + line2,
            TestContext.Current.CancellationToken
        );

        await new SessionInteractionFormat().UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );

        var after = await File.ReadAllTextAsync(
            _sessionFile,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("\r\n", after);
        Assert.DoesNotContain("\n\n", after);
        Assert.False(after.EndsWith('\n'));
        Assert.Equal("User answers:\n方向: 身世", ReadContents()["m1"]);
    }

    [Fact]
    public async Task UpAsync_AssistantMetadataWithoutMultiSelect_InfersFromAnswers()
    {
        var assistantToolCall = new JsonObject
        {
            ["id"] = "call_1",
            ["name"] = "ask_user",
            ["arguments"] = new JsonObject
            {
                ["questions"] = new JsonArray
                {
                    new JsonObject { ["header"] = "方向", ["question"] = "选择要保留的线索" },
                },
            }.ToJsonString(),
        };
        var lines = new[]
        {
            new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = "",
                ["tool-calls"] = new JsonArray(assistantToolCall).ToJsonString(),
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m1",
                ["role"] = "tool",
                ["content"] = "用户回复:\n  [方向] 选择要保留的线索\n    → 身世, 旧案",
            }.ToJsonString(),
        };
        await AtomicFile.WriteAllTextAsync(
            _sessionFile,
            string.Join("\n", lines) + "\n",
            TestContext.Current.CancellationToken
        );

        await new SessionInteractionFormat().UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("User answers:\n方向: [身世, 旧案]", ReadContents()["m1"]);
    }

    [Fact]
    public async Task UpAsync_WithoutToolMetadata_InfersMultiSelectFromAnswers()
    {
        var lines = new[]
        {
            new JsonObject
            {
                ["type"] = "message",
                ["id"] = "m1",
                ["role"] = "tool",
                ["content"] = "用户回复:\n  [方向] 选择要保留的线索\n    → 身世, 旧案",
            }.ToJsonString(),
        };
        await AtomicFile.WriteAllTextAsync(
            _sessionFile,
            string.Join("\n", lines) + "\n",
            TestContext.Current.CancellationToken
        );

        await new SessionInteractionFormat().UpAsync(
            _tempDir,
            Path.Combine(_tempDir, "novels"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("User answers:\n方向: [身世, 旧案]", ReadContents()["m1"]);
    }

    private Dictionary<string, string> ReadContents()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(_sessionFile))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                continue;
            var node = JsonNode.Parse(line)!.AsObject();
            var id = node["id"]?.GetValue<string>();
            var content = node["content"]?.GetValue<string>();
            if (id is not null && content is not null)
                result[id] = content;
        }
        return result;
    }
}
