using Core.Data;
using Plugins.NovelTools.Tools;
using Plugins.NovelTools.Tools.Formatters;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// NovelTools Formatter 测试：MarkdownFormatter、TxtFormatter。
/// </summary>
public class FormatterTests
{
    private static Novel CreateTestNovel() =>
        new()
        {
            Id = "test001",
            Title = "测试小说",
            Genre = "奇幻",
            Description = "这是一部测试小说的描述。",
            WritingStyle = "简洁",
        };

    private static List<Chapter> CreateTestChapters() =>
        [
            new Chapter
            {
                Id = "ch001",
                NovelId = "test001",
                Title = "序章",
                Order = 1,
                Content = "这是序章的内容。世界在混沌中诞生。",
                Status = ChapterStatus.Published,
            },
            new Chapter
            {
                Id = "ch002",
                NovelId = "test001",
                Title = "启程",
                Order = 2,
                Content = "主角踏上了旅程。",
                Status = ChapterStatus.Draft,
            },
        ];

    private static FormatterSettings CreateSettings() =>
        new()
        {
            Characters =
            [
                new Character
                {
                    Name = "林默",
                    Gender = "男",
                    Age = "25",
                    Role = CharacterRole.Protagonist,
                    Personality = "沉默寡言",
                    Habits = "擦拭剑刃",
                    Motivation = "复仇",
                    Weakness = "过于信任",
                    Status = CharacterStatus.Active,
                },
                new Character
                {
                    Name = "青云",
                    Gender = "女",
                    Age = "?",
                    Role = CharacterRole.Supporting,
                    Personality = "活泼开朗",
                    Habits = "收集药草",
                    Motivation = "守护家族",
                    Status = CharacterStatus.Active,
                },
            ],
            Locations =
            [
                new Location
                {
                    Name = "青云峰",
                    Type = LocationType.Wilderness,
                    Atmosphere = "仙气缭绕",
                    Description = "高耸入云的仙山",
                },
            ],
            Factions =
            [
                new Faction
                {
                    Name = "青云宗",
                    Type = FactionType.Sect,
                    Leader = "青云真人",
                    Goal = "维护秩序",
                    Description = "第一修仙门派",
                },
            ],
            IncludeAppendix = true,
            IncludeMetadata = true,
        };

    // ══════════════════════════════════════════════════════
    //  MarkdownFormatter
    // ══════════════════════════════════════════════════════

    [Fact]
    public void MarkdownFormatter_GeneratesTitle()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.StartsWith("# 测试小说", result);
    }

    [Fact]
    public void MarkdownFormatter_IncludesChapters()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("序章", result);
        Assert.Contains("启程", result);
        Assert.Contains("这是序章的内容", result);
    }

    [Fact]
    public void MarkdownFormatter_IncludesMetadata()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("奇幻", result);
        Assert.Contains("简洁", result);
    }

    [Fact]
    public void MarkdownFormatter_IncludesAppendix()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("## 附录", result);
        Assert.Contains("### 角色", result);
        Assert.Contains("林默", result);
        Assert.Contains("青云", result);
    }

    [Fact]
    public void MarkdownFormatter_AppendixHasCharacterTable()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("| 姓名 | 性别 | 年龄 | 定位 |", result);
        Assert.Contains("| 林默 | 男 | 25 | 主角 |", result);
    }

    [Fact]
    public void MarkdownFormatter_AppendixHasLocationTable()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("### 地点", result);
        Assert.Contains("青云峰", result);
    }

    [Fact]
    public void MarkdownFormatter_AppendixHasFactionTable()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("### 组织", result);
        Assert.Contains("青云宗", result);
        Assert.Contains("青云真人", result);
    }

    [Fact]
    public void MarkdownFormatter_NoAppendix_WhenDisabled()
    {
        var settings = CreateSettings() with { IncludeAppendix = false };
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), settings);

        Assert.DoesNotContain("## 附录", result);
    }

    [Fact]
    public void MarkdownFormatter_NoMetadata_WhenDisabled()
    {
        var settings = CreateSettings() with { IncludeMetadata = false };
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), settings);

        Assert.DoesNotContain("> 类型:", result);
    }

    [Fact]
    public void MarkdownFormatter_EmptyChapters_NoException()
    {
        var fmt = new MarkdownFormatter();
        var result = fmt.Generate(CreateTestNovel(), [], CreateSettings());

        Assert.Contains("# 测试小说", result);
    }

    // ══════════════════════════════════════════════════════
    //  TxtFormatter
    // ══════════════════════════════════════════════════════

    [Fact]
    public void TxtFormatter_GeneratesTitleWithUnderline()
    {
        var fmt = new TxtFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.StartsWith("测试小说", result);
        Assert.Contains("====", result);
    }

    [Fact]
    public void TxtFormatter_IncludesChapters()
    {
        var fmt = new TxtFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("第1章 序章", result);
        Assert.Contains("第2章 启程", result);
        Assert.Contains("这是序章的内容", result);
    }

    [Fact]
    public void TxtFormatter_ChaptersInOrder()
    {
        var fmt = new TxtFormatter();
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c2",
                NovelId = "t",
                Title = "第二章",
                Order = 2,
                Content = "后",
            },
            new()
            {
                Id = "c1",
                NovelId = "t",
                Title = "第一章",
                Order = 1,
                Content = "先",
            },
        };
        var result = fmt.Generate(CreateTestNovel(), chapters, CreateSettings());

        var firstIdx = result.IndexOf("第一章");
        var secondIdx = result.IndexOf("第二章");
        Assert.True(firstIdx < secondIdx);
    }

    [Fact]
    public void TxtFormatter_MetadataShown_WhenEnabled()
    {
        var settings = CreateSettings() with { IncludeMetadata = true };
        var fmt = new TxtFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), settings);

        Assert.Contains("状态:", result);
    }

    [Fact]
    public void TxtFormatter_MetadataHidden_WhenDisabled()
    {
        var settings = CreateSettings() with { IncludeMetadata = false };
        var fmt = new TxtFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), settings);

        Assert.DoesNotContain("状态:", result);
    }

    // ══════════════════════════════════════════════════════
    //  JsonFormatter
    // ══════════════════════════════════════════════════════

    [Fact]
    public void JsonFormatter_ProducesValidJson()
    {
        var fmt = new JsonFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.StartsWith("{", result.Trim());
        Assert.Contains("\"title\"", result);
        Assert.Contains("测试小说", result);
    }

    [Fact]
    public void JsonFormatter_IncludesAllChapters()
    {
        var fmt = new JsonFormatter();
        var result = fmt.Generate(CreateTestNovel(), CreateTestChapters(), CreateSettings());

        Assert.Contains("序章", result);
        Assert.Contains("启程", result);
    }
}
