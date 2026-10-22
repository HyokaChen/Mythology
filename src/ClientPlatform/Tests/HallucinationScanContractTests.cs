using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 幻觉扫描（fiction.hallucination.scan）→ 真实后端跨仓库契约测试
/// （CloudBackendFixture 拉起 Mythology.Cloud.Api 子进程）。
///
/// 钉死 stub 测试覆盖不到的真实语义：客户端 DTO → kebab wire → 云端扫描器
/// （正文断言提取/聚类熵/grounding）→ findings 解析 全链路；
/// 并验证章节引用 ch_{Order} 规范化在真实后端被正确解析（引用归一失效时此断言必红）。
/// </summary>
[Collection("live-backend")]
public sealed class HallucinationScanContractTests(CloudBackendFixture fixture)
{
    private const string SkipReason =
        "未找到私有后端（设置 MYTHOLOGY_CLOUD_BACKEND 或保持兄弟目录布局），跳过跨仓库契约测试";

    [Fact]
    public async Task Scan_AgainstLiveBackend_ReturnsDriftFindings()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new HttpFictionHarnessClient(
            new HttpClient(),
            "mk_test_dev-local_contract",
            fixture.BaseUrl!
        );

        var request = new HarnessRequest
        {
            Capability = "fiction.hallucination.scan",
            Depth = "standard",
            Locale = "zh-CN",
            Chapters =
            [
                new ChapterInput("ch_1", 1, "第一章", "main", "林昭死了。"),
                new ChapterInput("ch_2", 2, "第二章", "main", "林昭复活，现身于青云山之巅。"),
                new ChapterInput("ch_3", 3, "第三章", "main", "林昭陨落。"),
            ],
            Context = new AnalysisContextInput
            {
                NovelTitle = "契约测试",
                // 注意：云端 CloudSetupInput 的 characters/relations/world-rules/scenes/plotlines/
                // chapter-anchors 是非空构造参数（ASP.NET 隐式 [Required]，传 null → 400）。
                // 真实工具经 SetupDigestBuilder 恒传空数组，测试镜像同一形状。
                Setup = new SetupCheckInput(
                    Characters: [new SetupCharacterInput("c1", "林昭", Status: "active")],
                    Relations: [],
                    WorldRules: [],
                    Scenes: [],
                    Plotlines: [],
                    ChapterAnchors:
                    [
                        new SetupChapterAnchorInput("ch_1", 1),
                        new SetupChapterAnchorInput("ch_2", 2),
                        new SetupChapterAnchorInput("ch_3", 3),
                    ],
                    Status: "ongoing",
                    NovelTitle: "契约测试",
                    Locations: [new SetupLocationInput("loc1", "青云山")]
                ),
            },
        };

        var result = await client.ExecuteAsync(
            request,
            $"hallucination-contract-{Guid.NewGuid():N}",
            ct
        );

        Assert.Equal("fiction.hallucination.scan", result.Capability);
        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Result);

        var element = (JsonElement)result.Result!;
        var scan = element.GetProperty("scan");
        // 3 章 [死了, 复活, 陨落] → deceased 2:1 严格多数 → status 组 R_drift≈0.4564 → high
        Assert.True(scan.GetProperty("r-drift-score").GetDouble() > 0.4, element.GetRawText());
        Assert.Equal("high", scan.GetProperty("risk-level").GetString());
        Assert.Equal(3, scan.GetProperty("chapters-scanned").GetInt32());

        var findings = element.GetProperty("findings").EnumerateArray().ToList();
        Assert.Contains(
            findings,
            f =>
                f.GetProperty("id").GetString() == "HS.conflict.status.c1"
                && f.GetProperty("verdict").GetString() == "conflict"
        );
        // location 组存在（ch_2「现身于青云山之巅」窗口取值成功——证明 ch_{Order} 引用被云端解析）
        var groups = scan.GetProperty("attribute-groups").EnumerateArray().ToList();
        Assert.Contains(groups, g => g.GetProperty("attribute").GetString() == "location");
    }
}
