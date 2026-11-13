using System.Reflection;
using Core.Agent;
using Core.Data;
using Core.Tools;
using LlmTornado.Code;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentModel = Core.Agent.Agent;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Tests;

/// <summary>5W1H compact 检查点必须来自小说结构，而不是文件 read/write 操作。</summary>
public sealed class CompactNovelStateSnapshotTests : IDisposable
{
    private const string NovelId = "n1";
    private readonly string _base = Path.Combine(
        Path.GetTempPath(),
        $"myth-compact-snapshot-{Guid.NewGuid():N}"
    );
    private readonly FileNovelStore _novelStore;
    private readonly CompactionEngine _engine;

    public CompactNovelStateSnapshotTests()
    {
        var novelsRoot = Path.Combine(_base, "novels");
        var storeDir = Path.Combine(_base, "store");
        Directory.CreateDirectory(novelsRoot);
        Directory.CreateDirectory(storeDir);

        _novelStore = new FileNovelStore(novelsRoot, storeDir);
        var agent = new AgentModel(new ToolRegistry(), null) { CurrentNovelId = NovelId };
        _engine = new CompactionEngine(
            agent,
            new FileSessionStore(storeDir, novelsRoot),
            _novelStore,
            NullLogger.Instance
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task BuildNovelStateSnapshot_Includes5W1H_AndNext()
    {
        await SeedNovelAsync();

        var messages = new List<LlmChatMessage>
        {
            new(ChatMessageRoles.User, "继续写第一章的对峙"),
            new(ChatMessageRoles.Assistant, "好的，我会接着写。"),
        };
        var method = typeof(CompactionEngine).GetMethod(
            "BuildNovelStateSnapshotAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var task =
            (Task<string>)method.Invoke(_engine, [NovelId, messages, CancellationToken.None])!;
        var snapshot = await task;

        Assert.Contains("## Where / 写到哪里", snapshot);
        Assert.Contains("## Who / 谁出场", snapshot);
        Assert.Contains("## What / 发生了什么", snapshot);
        Assert.Contains("## Why / 为什么", snapshot);
        Assert.Contains("## When / 时间", snapshot);
        Assert.Contains("## How / 怎么写", snapshot);
        Assert.Contains("## Next / 下一步", snapshot);

        Assert.Contains("第一卷", snapshot);
        Assert.Contains("第1章《开端》", snapshot);
        Assert.Contains("黄昏", snapshot);
        Assert.Contains("教室", snapshot);
        Assert.Contains("林默", snapshot);
        Assert.Contains("身世之谜", snapshot);
        Assert.Contains("下一章细纲：林默发现线索", snapshot);
        Assert.Contains("用户目标：继续写第一章的对峙", snapshot);
        Assert.Contains("场景冲突/目标：对峙 / 试探", snapshot);
        Assert.Contains("推进下一章", snapshot);

        Assert.DoesNotContain("read(", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("write(", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("## 文件操作", snapshot, StringComparison.Ordinal);
    }

    private async Task SeedNovelAsync()
    {
        var novel = new Novel
        {
            Id = NovelId,
            NovelId = NovelId,
            Title = "测试小说",
            Genre = "玄幻",
            WritingStyle = "冷峻",
        };
        await _novelStore.SaveNovel(novel);

        var volume = new Volume
        {
            Id = "v1",
            NovelId = NovelId,
            Name = "第一卷",
            Order = 1,
        };
        await _novelStore.SaveVolume(volume);

        await _novelStore.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = NovelId,
                Title = "开端",
                Order = 1,
                VolumeId = volume.Id,
                Status = ChapterStatus.Draft,
                Summary = "林默在教室试探对手。",
                CharacterIds = ["ch1", "ch2"],
                PlotlineIds = ["p1"],
                Notes = "节奏要快",
                Content = new string('文', 200),
            }
        );
        await _novelStore.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = NovelId,
                Title = "线索",
                Order = 2,
                VolumeId = volume.Id,
                Status = ChapterStatus.Draft,
                Summary = "林默发现线索。",
            }
        );

        await _novelStore.SaveLocation(
            new Location
            {
                Id = "loc1",
                NovelId = NovelId,
                Name = "旧校舍",
                Status = LocationStatus.Active,
            }
        );
        await _novelStore.SaveFaction(
            new Faction
            {
                Id = "f1",
                NovelId = NovelId,
                Name = "调查组",
                Status = FactionStatus.Active,
            }
        );
        await _novelStore.SaveItem(
            new Item
            {
                Id = "it1",
                NovelId = NovelId,
                Name = "铜钥匙",
                Status = ItemStatus.Active,
            }
        );
        await _novelStore.SaveCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = NovelId,
                Name = "林默",
                Motivation = "查清身世",
                FactionId = "f1",
            }
        );
        await _novelStore.SaveCharacter(
            new Character
            {
                Id = "ch2",
                NovelId = NovelId,
                Name = "苏晚",
                Motivation = "保护林默",
            }
        );
        await _novelStore.SavePlotline(
            new Plotline
            {
                Id = "p1",
                NovelId = NovelId,
                Name = "身世之谜",
                Status = PlotlineStatus.Active,
                Tension = PlotlineTension.High,
                Turnpoints = "第1章：发现铜钥匙",
            }
        );
        await _novelStore.SaveRelationship(
            new Relationship
            {
                Id = "r1",
                NovelId = NovelId,
                CharacterAId = "ch1",
                CharacterBId = "ch2",
                Type = RelationshipType.Friend,
                Strength = RelationshipStrength.Strong,
                Description = "互相试探",
            }
        );
        await _novelStore.SaveScene(
            new SceneDefinition
            {
                Id = "s1",
                NovelId = NovelId,
                ChapterId = "c1",
                Title = "教室对峙",
                Order = 1,
                LocationId = "loc1",
                TimeDescription = "黄昏",
                ParticipantIds = ["ch1", "ch2"],
                PlotlineIds = ["p1"],
                ItemIds = ["it1"],
                Conflict = "对峙",
                Goal = "试探",
            }
        );
        await _novelStore.SaveTimelineEvent(
            new TimelineEvent
            {
                Id = "t1",
                NovelId = NovelId,
                ChapterId = "c1",
                Title = "铜钥匙出现",
                Description = "林默在课桌里发现铜钥匙。",
            }
        );
    }
}
