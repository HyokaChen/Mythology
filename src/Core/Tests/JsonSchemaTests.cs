using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// JsonSchemaHelper 测试。
/// 验证从 C# 类型自动生成 JSON Schema 的功能。
/// </summary>
public class JsonSchemaHelperTests
{
    [Fact]
    /// <summary>简单记录应生成有效的 JSON Schema。</summary>
    public void GenerateSchema_SimpleRecord_ProducesValidSchema()
    {
        // AgentTool<TParams,TDetails> exposes Parameters which calls GenerateSchema
        var tool = new TestTool();
        Assert.NotNull(tool.Parameters);

        var schema = tool.Parameters.Value;
        Assert.True(schema.TryGetProperty("properties", out _));
        Assert.True(schema.TryGetProperty("type", out var typeProp));
        Assert.Equal("object", typeProp.GetString());
    }

    [Fact]
    /// <summary>必填字段应在 required 数组中列出。</summary>
    public void GenerateSchema_RequiredFields_AreListed()
    {
        var tool = new TestTool();
        var schema = tool.Parameters!.Value;

        Assert.True(schema.TryGetProperty("required", out var required));
        var requiredArray = required.EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("name", requiredArray);
        Assert.Contains("age", requiredArray);
    }

    [Fact]
    /// <summary>Description 特性应被注入到 Schema 描述中。</summary>
    public void GenerateSchema_DescriptionAttribute_IsInjected()
    {
        var tool = new TestTool();
        var schema = tool.Parameters!.Value;

        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("name", out var nameProp));
        Assert.True(nameProp.TryGetProperty("description", out var desc));
        Assert.Equal("用户名", desc.GetString());
    }

    // ── 测试类型 ──

    public sealed record TestParams
    {
        [System.ComponentModel.Description("用户名")]
        public string Name { get; init; } = "";

        [System.ComponentModel.Description("年龄")]
        public int Age { get; init; }

        public string? Optional { get; init; }

        public string[] Tags { get; init; } = [];
    }

    public sealed record TestDetails
    {
        public bool Success { get; init; }
    }

    private sealed class TestTool : AgentTool<TestParams, TestDetails>
    {
        public override string Name => "test_tool";
        public override string Label => "测试工具";
        public override string Description => "用于测试 Schema 生成";

        public override Task<AgentToolResult<TestDetails>> ExecuteAsync(
            string toolCallId,
            TestParams args,
            CancellationToken ct,
            IProgress<AgentToolResult<TestDetails>>? onUpdate = null
        )
        {
            return Task.FromResult(
                new AgentToolResult<TestDetails> { Details = new TestDetails { Success = true } }
            );
        }
    }
}
