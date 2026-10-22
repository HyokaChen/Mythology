using Core.Cloud;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// HarnessContextBuilder 测试：本地 Novel 结构化上下文 → AnalysisContextInput 上传契约。
/// 验证角色详情/情节线/物品/标题完整映射，云端分析器不再只有名字级输入。
/// </summary>
public class HarnessContextBuilderTests
{
    [Fact]
    public void Build_PopulatesStructuredContext()
    {
        var novel = new Novel
        {
            Title = "青云之路",
            Characters =
            [
                new Character
                {
                    Name = "林云",
                    Role = CharacterRole.Protagonist,
                    Gender = "男",
                    Age = "17",
                    Personality = "坚韧隐忍",
                    Background = "山村少年",
                    Motivation = "复仇",
                    Abilities = "灵根",
                    Weakness = "重情",
                    Habits = "握拳",
                },
                new Character
                {
                    Name = "苏瑶",
                    Role = CharacterRole.Supporting,
                    Personality = "温柔",
                },
            ],
            Plotlines =
            [
                new Plotline
                {
                    Name = "主线",
                    Type = PlotlineType.Main,
                    ResolvedAtChapterId = "ch2",
                    PlannedRevealChapterId = "ch1",
                },
            ],
            Items = [new Item { Name = "青云剑" }],
        };

        var ctx = HarnessContextBuilder.Build(novel, "玄幻");

        // 标题与题材提示
        Assert.Equal("青云之路", ctx.NovelTitle);
        Assert.Contains("玄幻", ctx.PlotHints!);

        // 名字级 hints（兼容旧云端）
        Assert.Equal(["林云", "苏瑶"], ctx.CharacterHints);

        // 结构化角色详情
        Assert.NotNull(ctx.CharacterDetails);
        var linYun = Assert.Single(ctx.CharacterDetails!.Where(c => c.Name == "林云"));
        Assert.Equal("protagonist", linYun.Role);
        Assert.Equal("坚韧隐忍", linYun.Personality);
        Assert.Equal("山村少年", linYun.Background);
        Assert.Equal("复仇", linYun.Motivation);
        Assert.Equal("灵根", linYun.Abilities);
        Assert.Equal("重情", linYun.Weakness);
        Assert.Equal("握拳", linYun.Habits);

        // 结构化情节线
        Assert.NotNull(ctx.Plotlines);
        var main = Assert.Single(ctx.Plotlines!);
        Assert.Equal("主线", main.Name);
        Assert.Equal("main", main.Type);
        Assert.Equal("ch2", main.ResolvedAtChapterId);
        Assert.Equal("ch1", main.PlannedRevealChapterId);

        // 物品
        Assert.NotNull(ctx.Items);
        Assert.Equal(["青云剑"], ctx.Items!.Select(i => i.Name).ToList());
    }

    [Fact]
    public void Build_EmptyNovel_ProducesSafeEmptyContext()
    {
        var ctx = HarnessContextBuilder.Build(new Novel(), null);

        Assert.Null(ctx.NovelTitle);
        Assert.Empty(ctx.CharacterHints!);
        Assert.Empty(ctx.CharacterDetails!);
        Assert.Empty(ctx.Plotlines!);
        Assert.Empty(ctx.Items!);
        Assert.Empty(ctx.PlotHints!);
    }

    [Fact]
    public void Build_SkipsNamelessCharactersAndItems()
    {
        var novel = new Novel
        {
            Characters = [new Character { Name = "" }, new Character { Name = "林云" }],
            Items = [new Item { Name = "" }, new Item { Name = "青云剑" }],
        };

        var ctx = HarnessContextBuilder.Build(novel, null);

        Assert.Equal(["林云"], ctx.CharacterHints);
        Assert.Single(ctx.CharacterDetails!);
        Assert.Equal(["青云剑"], ctx.Items!.Select(i => i.Name).ToList());
    }
}

/// <summary>
/// HarnessContextBuilder 边界测试：版本哈希稳定性（顺序无关）、null 字段容错、
/// 题材 hint 传递、空角色/空情节线安全。
/// </summary>
public class HarnessContextBuilderEdgeTests
{
    private static Novel SampleNovel() =>
        new()
        {
            Title = "测试",
            Genre = "玄幻",
            Characters =
            [
                new Character
                {
                    Name = "甲",
                    Role = CharacterRole.Protagonist,
                    Personality = "A",
                },
                new Character
                {
                    Name = "乙",
                    Role = CharacterRole.Supporting,
                    Personality = "B",
                },
            ],
            Plotlines =
            [
                new Plotline { Name = "线一", Type = PlotlineType.Main },
                new Plotline { Name = "线二", Type = PlotlineType.Foreshadowing },
            ],
        };

    [Fact]
    public void CharacterVersionHash_OrderIndependent_StableForSameData()
    {
        // 角色顺序交换 → 版本哈希不变（按 Name 排序后序列化）
        var a = SampleNovel();
        var b = SampleNovel();
        b.Characters = [a.Characters[1], a.Characters[0]]; // 交换顺序

        var ctxA = HarnessContextBuilder.Build(a);
        var ctxB = HarnessContextBuilder.Build(b);

        Assert.NotNull(ctxA.CharacterVersion);
        Assert.Equal(ctxA.CharacterVersion, ctxB.CharacterVersion);
    }

    [Fact]
    public void PlotlineVersionHash_OrderIndependent_StableForSameData()
    {
        var a = SampleNovel();
        var b = SampleNovel();
        b.Plotlines = [a.Plotlines[1], a.Plotlines[0]];

        var ctxA = HarnessContextBuilder.Build(a);
        var ctxB = HarnessContextBuilder.Build(b);

        Assert.Equal(ctxA.PlotlineVersion, ctxB.PlotlineVersion);
    }

    [Fact]
    public void VersionHash_ChangesWhenDataChanges()
    {
        var a = SampleNovel();
        var b = SampleNovel();
        b.Characters[0].Personality = "C";

        var ctxA = HarnessContextBuilder.Build(a);
        var ctxB = HarnessContextBuilder.Build(b);

        Assert.NotEqual(ctxA.CharacterVersion, ctxB.CharacterVersion);
    }

    [Fact]
    public void NullNullableFields_DoNotThrow_AndMapToNull()
    {
        var novel = new Novel
        {
            Title = "空字段",
            Characters =
            [
                new Character
                {
                    Name = "林云",
                    Role = CharacterRole.Protagonist,
                    Gender = null,
                    Age = null,
                    Personality = null,
                    Background = null,
                    Motivation = null,
                    Abilities = null,
                    Weakness = null,
                    Habits = null,
                },
            ],
            Plotlines =
            [
                new Plotline
                {
                    Name = "线",
                    ResolvedAtChapterId = null,
                    PlannedRevealChapterId = null,
                },
            ],
        };

        var ctx = HarnessContextBuilder.Build(novel, null);

        var ch = Assert.Single(ctx.CharacterDetails!);
        // null 字段原样透传（契约字段为 string，云端按缺失处理）
        Assert.Null(ch.Gender);
        Assert.Null(ch.Personality);
        var pl = Assert.Single(ctx.Plotlines!);
        Assert.Null(pl.ResolvedAtChapterId);
        Assert.Null(pl.PlannedRevealChapterId);
    }

    [Fact]
    public void RoleEnum_SerializesLowercase()
    {
        var novel = new Novel
        {
            Characters =
            [
                new Character { Name = "反", Role = CharacterRole.Antagonist },
                new Character { Name = "龙", Role = CharacterRole.Minor },
            ],
        };

        var ctx = HarnessContextBuilder.Build(novel, null);

        var byName = ctx.CharacterDetails!.ToDictionary(c => c.Name);
        Assert.Equal("antagonist", byName["反"].Role);
        Assert.Equal("minor", byName["龙"].Role);
    }

    [Fact]
    public void GenreHint_GoesIntoPlotHints_AsFirstElement()
    {
        var ctx = HarnessContextBuilder.Build(new Novel { Title = "T" }, "悬疑");

        Assert.Equal(["悬疑"], ctx.PlotHints);
    }

    [Fact]
    public void TimelineEvents_MapToContext_AndNoneMeansNull()
    {
        var novel = new Novel
        {
            Title = "T",
            TimelineEvents =
            [
                new TimelineEvent
                {
                    Id = "tl_1",
                    Title = "主角出生",
                    Description = "山村少年降生",
                    ChapterId = "ch_1",
                },
                new TimelineEvent { Id = "tl_2" }, // 全书级事件（无章节引用）
            ],
        };

        var ctx = HarnessContextBuilder.Build(novel, null);

        Assert.Equal(2, ctx.Timeline!.Count);
        Assert.Equal("tl_1", ctx.Timeline[0].Id);
        Assert.Equal("主角出生", ctx.Timeline[0].Title);
        Assert.Equal("山村少年降生", ctx.Timeline[0].Description);
        Assert.Equal("ch_1", ctx.Timeline[0].ChapterId);
        Assert.Null(ctx.Timeline[1].ChapterId);

        // 无事件 → null（不在 wire 上新增占位空数组；服务端两种形态等价）
        Assert.Null(HarnessContextBuilder.Build(new Novel { Title = "T" }, null).Timeline);
    }
}
