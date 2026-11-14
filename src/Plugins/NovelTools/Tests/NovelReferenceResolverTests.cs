using Core.Data;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

public sealed class NovelReferenceResolverTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("第3章", 3)]
    [InlineData("第 3 章", 3)]
    [InlineData("第1场", 1)]
    [InlineData("第三章", 3)]
    [InlineData("第十二章", 12)]
    [InlineData("十二", 12)]
    [InlineData("ch12", 12)]
    [InlineData("chapter 12", 12)]
    [InlineData("第两百章", 200)]
    [InlineData("３", 3)]
    public void TryParseOrder_AcceptsNumberForms(string raw, int expected)
    {
        Assert.True(NovelReferenceResolver.TryParseOrder(raw, out var order));
        Assert.Equal(expected, order);
    }

    [Theory]
    [InlineData("序章")]
    [InlineData("第0章")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryParseOrder_RejectsNonOrder(string raw)
    {
        Assert.False(NovelReferenceResolver.TryParseOrder(raw, out _));
    }

    [Fact]
    public void FindChapter_SupportsIdOrderChineseAndTitle()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "ch1",
                Title = "风起",
                Order = 1,
            },
            new()
            {
                Id = "ch2",
                Title = "云涌",
                Order = 2,
            },
        };

        Assert.Equal("ch1", NovelReferenceResolver.FindChapter(chapters, "ch1")!.Id);
        Assert.Equal("ch2", NovelReferenceResolver.FindChapter(chapters, "第2章")!.Id);
        Assert.Equal("ch2", NovelReferenceResolver.FindChapter(chapters, "二")!.Id);
        Assert.Equal("ch1", NovelReferenceResolver.FindChapter(chapters, " 风起 ")!.Id);
    }

    [Fact]
    public void FindChapter_DuplicateOrder_ReturnsErrorInsteadOfGuessing()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "a",
                Title = "甲",
                Order = 1,
            },
            new()
            {
                Id = "b",
                Title = "乙",
                Order = 1,
            },
        };

        var lookup = NovelReferenceResolver.FindChapterLookup(chapters, "第1章");
        Assert.Null(lookup.Value);
        Assert.Contains("2 条", lookup.Error);
    }

    [Fact]
    public void FindVolume_SupportsIdOrderChineseAndName()
    {
        var volumes = new List<Volume>
        {
            new()
            {
                Id = "v1",
                Name = "起",
                Order = 1,
            },
            new()
            {
                Id = "v2",
                Name = "承",
                Order = 2,
            },
        };

        Assert.Equal("v1", NovelReferenceResolver.FindVolume(volumes, "v1")!.Id);
        Assert.Equal("v2", NovelReferenceResolver.FindVolume(volumes, "第二卷")!.Id);
        Assert.Equal("v2", NovelReferenceResolver.FindVolume(volumes, " 承 ")!.Id);
    }

    [Fact]
    public void LookupByIdOrName_DuplicateName_ReportsAmbiguity()
    {
        var characters = new List<Character>
        {
            new() { Id = "c1", Name = "小明" },
            new() { Id = "c2", Name = "小明" },
        };

        var lookup = NovelReferenceResolver.LookupByIdOrName(
            characters,
            "小明",
            c => c.Id,
            c => c.Name,
            "角色"
        );
        Assert.Null(lookup.Value);
        Assert.Contains("2 个", lookup.Error);
        Assert.Contains("c1", lookup.Error);
        Assert.Contains("c2", lookup.Error);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("not-a-role")]
    public void TryParseEnum_RejectsNumericAndUnknownValues(string raw)
    {
        Assert.False(NovelEnum.TryParse<CharacterRole>(raw, out _));
    }

    [Fact]
    public void TryParseEnum_AcceptsChineseLabels()
    {
        Assert.True(NovelEnum.TryParse<CharacterRole>("主角", out var protagonist));
        Assert.Equal(CharacterRole.Protagonist, protagonist);
        Assert.True(NovelEnum.TryParse<SceneType>("对话", out var sceneType));
        Assert.Equal(SceneType.Dialogue, sceneType);
        Assert.True(NovelEnum.TryParse<PlotlineTension>("中", out var tension));
        Assert.Equal(PlotlineTension.Medium, tension);
    }
}
