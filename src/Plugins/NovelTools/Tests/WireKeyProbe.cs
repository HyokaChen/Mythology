using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 跨仓 wire 键探针（CHANGE_10 v2/v3 对接回归锁）：
/// 客户端 SetupCheckInput 经 ClientJsonDefaults.Cloud（kebab）序列化后，
/// 字段键必须与云端 CloudSetupInput 的 STJ 绑定一致（MythologyCloud 仓库）。
/// </summary>
public class WireKeyProbe
{
    [Fact]
    public void SetupCheckInput_SerializesV2V3Fields_WithKebabKeys()
    {
        var setup = new SetupCheckInput(
            Characters:
            [
                new SetupCharacterInput(
                    "c1",
                    "林默",
                    Role: "protagonist",
                    Organizations: ["f1"],
                    Personality: "沉毅",
                    Motivation: "复仇",
                    Background: "遗孤",
                    Habits: "摩挲玉佩"
                ),
            ],
            Locations: [new SetupLocationInput("loc1", "林府", Region: "东荒")],
            Organizations: [new SetupOrganizationInput("f1", "林家", MemberIds: ["c1"])],
            Items:
            [
                new SetupItemInput(
                    "it1",
                    "玉佩",
                    Ownership: [new OwnershipInput("c1")],
                    LocationId: "loc1"
                ),
            ],
            ChapterAnchors: [new SetupChapterAnchorInput("ch_1", 1, WordCount: 1200)]
        );

        var json = JsonSerializer.Serialize(setup, ClientJsonDefaults.Cloud);

        // v2/v3 关键键名（与云端 CloudSetupInput kebab 绑定一致）
        Assert.Contains("\"personality\"", json);
        Assert.Contains("\"motivation\"", json);
        Assert.Contains("\"background\"", json);
        Assert.Contains("\"habits\"", json);
        Assert.Contains("\"locations\"", json);
        Assert.Contains("\"organizations\"", json);
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"member-ids\"", json);
        Assert.Contains("\"ownership\"", json);
        Assert.Contains("\"owner-id\"", json);
        Assert.Contains("\"first-appearance-chapter-id\"", json);
        Assert.Contains("\"word-count\"", json);
    }

    /// <summary>
    /// 云端反序列化验证（跨仓 E2E 的可复现形态）：
    /// 用 Cloud.Api 的 STJ 配置（kebab + 大小写不敏感）读取同一 JSON，
    /// 全部 v2/v3 字段可绑定 —— 与 MythologyCloud CloudSetupInput 契约一致。
    /// 云端类型在此仓不可引用，这里以"键名存在性 + 形状"作代理断言；
    /// 真实类型绑定由云端仓 SetupCheckCapabilityTests + 本 JSON 手工回放覆盖。
    /// </summary>
    [Fact]
    public void SetupCheckJson_RoundTrips_AllV2V3FieldsPresent()
    {
        var setup = new SetupCheckInput(
            Characters:
            [
                new SetupCharacterInput(
                    "c1",
                    "林默",
                    Role: "protagonist",
                    Personality: "沉毅",
                    Habits: "摩挲玉佩"
                ),
            ],
            Locations: [new SetupLocationInput("loc1", "林府", Region: "东荒")],
            Organizations: [new SetupOrganizationInput("f1", "林家", MemberIds: ["c1"])],
            Items: [new SetupItemInput("it1", "玉佩", Ownership: [new OwnershipInput("c1")])],
            ChapterAnchors: [new SetupChapterAnchorInput("ch_1", 1, WordCount: 1200)]
        );

        // 模拟网络往返：serialize → deserialize（同 ClientJsonDefaults.Cloud）
        var json = JsonSerializer.Serialize(setup, ClientJsonDefaults.Cloud);
        var round = JsonSerializer.Deserialize<SetupCheckInput>(json, ClientJsonDefaults.Cloud);

        Assert.NotNull(round);
        Assert.Equal("沉毅", round!.Characters![0].Personality);
        Assert.Equal("摩挲玉佩", round.Characters![0].Habits);
        Assert.Equal("林府", round.Locations![0].Name);
        Assert.Equal("东荒", round.Locations![0].Region);
        Assert.Equal("c1", Assert.Single(round.Organizations![0].MemberIds!));
        Assert.Equal("c1", round.Items![0].Ownership![0].OwnerId);
        Assert.Equal(1200, round.ChapterAnchors![0].WordCount);
    }
}
