using Core.Agent;
using Core.Data;
using Core.Tools;
using Xunit;

namespace Core.Tests;

public sealed class TaskScopeGuardTests
{
    private static readonly TaskScopeState Scope = new()
    {
        Mode = "revise",
        Label = "第2卷《夏夜的萤火》（第21-40章）",
        VolumeIds = ["v2"],
        ChapterOrders =
        [
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            29,
            30,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
        ],
        Instruction = "修正优化整个第二卷",
    };

    private static readonly Chapter[] Chapters =
    [
        new()
        {
            Id = "c21",
            Title = "萤火21",
            Order = 21,
            VolumeId = "v2",
        },
        new()
        {
            Id = "c54",
            Title = "恶意的瞥见",
            Order = 54,
            VolumeId = "v3",
        },
    ];

    private static readonly Volume[] Volumes =
    [
        new()
        {
            Id = "v2",
            Name = "夏夜的萤火",
            Order = 2,
        },
        new()
        {
            Id = "v3",
            Name = "遗忘的纸鸢",
            Order = 3,
        },
    ];

    [Fact]
    public void BlocksOutOfScopeExistingChapter()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "write_chapter",
            """{"novel-id":"n1","chapter-id":"54"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.NotNull(reason);
        Assert.Contains("第54章", reason);
        Assert.Contains("第2卷《夏夜的萤火》", reason);
    }

    [Fact]
    public void AllowsInScopeExistingChapter()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "edit_text",
            """{"novel-id":"n1","chapter-id":"c21"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.Null(reason);
    }

    [Fact]
    public void BlocksOutOfScopeNewChapter()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "write_chapter",
            """{"novel-id":"n1","volume-id":"v3","order":54,"title":"新章"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.NotNull(reason);
        Assert.Contains("第54章", reason);
    }

    [Fact]
    public void AllowsInScopeNewChapter()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "write_chapter",
            """{"novel-id":"n1","volume-id":"v2","order":25,"title":"新章"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.Null(reason);
    }

    [Fact]
    public void ExplicitTurnTargetAllowsOneOffOutOfScopeEdit()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "rewrite_selection",
            """{"novel-id":"n1","chapter-id":"54"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: "c54"
        );

        Assert.Null(reason);
    }

    [Fact]
    public void BlocksOutOfScopeOutlineVolume()
    {
        var reason = TaskScopeGuard.Evaluate(
            Scope,
            "create_outline",
            """{"novel-id":"n1","volume-id":"v3","chapter-titles":["新章"]}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.NotNull(reason);
        Assert.Contains("第3卷", reason);
    }

    [Fact]
    public void NoScopeAllowsWrites()
    {
        var reason = TaskScopeGuard.Evaluate(
            null,
            "write_chapter",
            """{"novel-id":"n1","chapter-id":"54"}""",
            Chapters,
            Volumes,
            turnTargetChapterId: null
        );

        Assert.Null(reason);
    }
}
