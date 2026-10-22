using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateCharactersTool 批量创建角色工具测试。
/// </summary>
public class BatchCreateCharactersToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateCharactersTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateCharactersParams { Characters = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个角色", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateCharactersParams { Characters = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有角色。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllCharacters()
    {
        var tool = CreateTool();
        var args = new BatchCreateCharactersParams
        {
            NovelId = "novel-1",
            Characters =
            [
                new()
                {
                    Name = "Alice",
                    Gender = "女",
                    Age = "25",
                    Role = "protagonist",
                    Personality = "勇敢",
                    Appearance = "黑发",
                },
                new()
                {
                    Name = "Bob",
                    Gender = "男",
                    Age = "中年",
                    Role = "antagonist",
                    Personality = "聪明",
                    Appearance = "金发",
                },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateCharactersDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
        Assert.Contains("Alice", ToolDispatcher.ExtractText(result.Content));
        Assert.Contains("Bob", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>空名称应跳过并计入失败。</summary>
    public async Task ExecuteAsync_EmptyName_SkipsAndCountsFailed()
    {
        var tool = CreateTool();
        var args = new BatchCreateCharactersParams
        {
            NovelId = "novel-1",
            Characters = [new() { Name = "Alice" }, new() { Name = "" }, new() { Name = "Bob" }],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateCharactersDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(1, details.FailedCount); // empty name is skipped
        Assert.Contains("Alice", ToolDispatcher.ExtractText(result.Content));
        Assert.Contains("Bob", ToolDispatcher.ExtractText(result.Content));
    }
}
