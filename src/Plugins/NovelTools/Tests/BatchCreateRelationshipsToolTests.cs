using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateRelationshipsTool 批量创建关系工具测试。
/// </summary>
public class BatchCreateRelationshipsToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateRelationshipsTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateRelationshipsParams { Relationships = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个关系", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateRelationshipsParams { Relationships = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有关系。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllRelationships()
    {
        var tool = CreateTool();
        var args = new BatchCreateRelationshipsParams
        {
            NovelId = "novel-1",
            Relationships =
            [
                new()
                {
                    CharacterAId = "char-1",
                    CharacterBId = "char-2",
                    Type = "friend",
                },
                new()
                {
                    CharacterAId = "char-1",
                    CharacterBId = "char-3",
                    Type = "rival",
                },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateRelationshipsDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }

    [Fact]
    /// <summary>缺少角色 ID 应跳过无效项。</summary>
    public async Task ExecuteAsync_MissingCharacterId_SkipsInvalid()
    {
        var tool = CreateTool();
        var args = new BatchCreateRelationshipsParams
        {
            NovelId = "novel-1",
            Relationships =
            [
                new() { CharacterAId = "char-1", CharacterBId = "char-2" },
                new() { CharacterAId = "", CharacterBId = "char-3" },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateRelationshipsDetails)result.Details!;
        Assert.Equal(1, details.CreatedCount);
        Assert.Equal(1, details.FailedCount);
    }
}
