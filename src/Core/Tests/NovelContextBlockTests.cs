using Core.Data;
using Core.Prompts;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 「当前小说环境」注入块（三层大纲的模型侧入口）：总纲截断摘要、卷纲概览（有界）、
/// 章节进度（有界）。长篇（上千章 / 上百卷）注入块大小必须恒定，否则每轮都在烧上下文。
/// </summary>
public sealed class NovelContextBlockTests
{
    private static Novel BuildNovel(string outline = "", string description = "") =>
        new()
        {
            Id = "n1",
            Title = "长篇",
            Genre = "玄幻",
            WritingStyle = "简洁",
            Description = description,
            Outline = outline,
        };

    [Fact]
    public void Build_IncludesOutlineAndVolumeOverview()
    {
        var novel = BuildNovel(outline: "三幕：入城 → 夺权 → 反目");
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜",
                Order = 1,
                VolumeId = "v1",
            },
            new()
            {
                Id = "c2",
                NovelId = "n1",
                Title = "进城",
                Order = 2,
                VolumeId = "v1",
            },
            new()
            {
                Id = "c3",
                NovelId = "n1",
                Title = "夺宝",
                Order = 3,
            },
        };
        var volumes = new List<Volume>
        {
            new()
            {
                Id = "v1",
                NovelId = "n1",
                Name = "风起",
                Order = 1,
                Summary = "入城夺宝，结识旧友",
            },
        };

        var block = NovelContextBlock.Build(
            novel,
            chapters,
            volumes,
            new DateTime(2026, 1, 2, 3, 4, 5)
        );

        Assert.Contains("总纲: 三幕：入城 → 夺权 → 反目", block);
        Assert.Contains("分卷: 1 卷 · 3 章", block);
        Assert.Contains("📦 第1卷 风起（2章）— 入城夺宝，结识旧友", block);
        Assert.Contains("3. 夺宝 (Draft) ← 当前进度", block); // 最后一条标当前进度
        Assert.Contains("get_outline", block); // 细纲不在注入块，指路按需查
    }

    [Fact]
    public void Build_TruncatesOutlineAndDescription()
    {
        var novel = BuildNovel(
            outline: new string('纲', NovelContextBlock.OutlineMaxChars + 50),
            description: new string('简', NovelContextBlock.DescriptionMaxChars + 50)
        );

        var block = NovelContextBlock.Build(novel, [], [], DateTime.UnixEpoch);

        var outlineLine = block
            .Split('\n')
            .First(l => l.StartsWith("总纲: ", StringComparison.Ordinal));
        var descriptionLine = block
            .Split('\n')
            .First(l => l.StartsWith("简介: ", StringComparison.Ordinal));
        Assert.Equal("总纲: ".Length + NovelContextBlock.OutlineMaxChars + 1, outlineLine.Length); // 前缀 + 截断 + 省略号
        Assert.EndsWith("…", outlineLine, StringComparison.Ordinal);
        Assert.Equal(
            "简介: ".Length + NovelContextBlock.DescriptionMaxChars + 1,
            descriptionLine.Length
        );
    }

    [Fact]
    public void Build_LongNovel_KeepsBlockBounded()
    {
        // 1200 章 / 60 卷：注入块只随上限增长，不随作品规模增长
        var chapters = Enumerable
            .Range(1, 1200)
            .Select(i => new Chapter
            {
                Id = $"c{i}",
                NovelId = "n1",
                Title = $"第{i}节",
                Order = i,
                VolumeId = $"v{(i - 1) / 20 + 1}",
            })
            .ToList();
        var volumes = Enumerable
            .Range(1, 60)
            .Select(i => new Volume
            {
                Id = $"v{i}",
                NovelId = "n1",
                Name = $"卷{i}",
                Order = i,
                Summary = new string('卷', 200),
            })
            .ToList();

        var block = NovelContextBlock.Build(BuildNovel(), chapters, volumes, DateTime.UnixEpoch);

        Assert.Contains("分卷: 60 卷 · 1200 章", block);
        Assert.Contains("… 共 60 卷（其余卷纲用 get_outline 查看）", block);
        Assert.Contains("... 共 1200 章", block);
        Assert.True(block.Length < 4096, $"注入块过大: {block.Length} 字符");
        // 卷行数受上限约束（卷名各一行 + 1 行汇总）
        Assert.Equal(
            NovelContextBlock.MaxVolumeLines + 1,
            block
                .Split('\n')
                .Count(l =>
                    l.StartsWith("  📦 ", StringComparison.Ordinal)
                    || l.StartsWith("  … 共 60 卷", StringComparison.Ordinal)
                )
        );
    }

    [Fact]
    public void Build_EmptyOutlineAndVolumes_UsesPlaceholders()
    {
        var block = NovelContextBlock.Build(BuildNovel(), [], [], DateTime.UnixEpoch);

        Assert.Contains("总纲: (未填写)", block);
        Assert.Contains("分卷: 0 卷 · 0 章", block);
        Assert.Contains("暂无章节", block);
    }
}
