using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定检查窗的新键展示（2026-10-18）：服务端 graph 摘要 + outline-metrics.volumes
/// 经 check_setup 工具结果驱动 VM，断言两个绑定属性；键缺席（旧服务端/quick）→ 隐藏。
///
/// 用测试替身工具而非真实 <c>CheckSetupTool</c>：Desktop 与 Plugins.NovelTools 之间
/// **没有编译时引用**（插件经 MSBuild 目标复制、运行时加载，见 Desktop.csproj），
/// 这里验证的是「CheckSetupDetails → 绑定属性」这一段映射（工具解析由 NovelTools 测试覆盖）。
/// </summary>
public sealed class SetUpPanelCheckGraphTests : IDisposable
{
    private readonly string _root;
    private readonly FileNovelStore _store;

    public SetUpPanelCheckGraphTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-check-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(tmp, "mythology"));
        _store = new FileNovelStore(_root, Path.Combine(tmp, "mythology"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>参数占位（VM 传 {"novel-id":…}；替身不消费参数）。</summary>
    public sealed class FakeSetupCheckParams { }

    /// <summary>固定返回预设 details 的 check_setup 替身。</summary>
    private sealed class FakeSetupCheckTool(CheckSetupDetails details)
        : AgentTool<FakeSetupCheckParams, CheckSetupDetails>
    {
        public override string Name => "check_setup";
        public override string Label => "设定检查";
        public override string Description => "测试替身（不起网络）";
        public override ToolCategory Category => ToolCategory.EntityRead;

        public override Task<AgentToolResult<CheckSetupDetails>> ExecuteAsync(
            string toolCallId,
            FakeSetupCheckParams args,
            CancellationToken ct,
            IProgress<AgentToolResult<CheckSetupDetails>>? onUpdate = null
        ) =>
            Task.FromResult(
                new AgentToolResult<CheckSetupDetails>
                {
                    Content = [new TextContent("（测试替身）")],
                    Details = details,
                }
            );
    }

    private SetUpPanelViewModel NewViewModel(CheckSetupDetails details)
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeSetupCheckTool(details), "Tests");
        return new SetUpPanelViewModel(_store, null, registry, null, () => "n1", null, a => a());
    }

    private static CheckSetupDetails GraphDetails() =>
        new()
        {
            Graph = new SetupGraphInfo("graph", 42, 66),
            Volumes =
            [
                new VolumeOutlineEntry
                {
                    VolumeId = "v1",
                    Name = "启程",
                    ChapterCount = 12,
                    BeatCount = 18,
                    TensionMean = 0.42,
                    TensionMin = 0.2,
                    TensionMax = 0.78,
                    ArcShape = "man-in-a-hole",
                    HookCoverage = 0.25,
                },
                new VolumeOutlineEntry
                {
                    VolumeId = "v2",
                    Name = "决战",
                    ChapterCount = 8,
                    FlatLine = true,
                },
            ],
        };

    [Fact]
    public async Task ExecuteCheckSetup_ShowsGraphAndVolumes()
    {
        var vm = NewViewModel(GraphDetails());

        await vm.ExecuteCheckSetupAsync();

        Assert.True(vm.HasGraphSummary);
        Assert.Contains("同步图", vm.GraphSummary);
        Assert.Contains("42", vm.GraphSummary);
        Assert.True(vm.HasVolumeSummary);
        Assert.Contains("启程", vm.VolumeSummary);
        Assert.Contains("决战", vm.VolumeSummary);
        Assert.Contains("平坦", vm.VolumeSummary); // flat-line 显式提示
    }

    [Fact]
    public async Task ExecuteCheckSetup_HidesGraphAndVolumes_WhenKeysAbsent()
    {
        var vm = NewViewModel(new CheckSetupDetails());

        await vm.ExecuteCheckSetupAsync();

        Assert.False(vm.HasGraphSummary);
        Assert.Equal("", vm.GraphSummary);
        Assert.False(vm.HasVolumeSummary);
        Assert.Equal("", vm.VolumeSummary);
    }
}
