using Core.Data;
using Desktop.ViewModels;
using ReactiveUI.Primitives;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设置页「本地判定（System One）· 可选」区块与 config.yaml 的往返契约：
/// 未编辑字段（超时/状态预算/重排开关/冲突开关）穿越 UI 保存管线必须原样保留，
/// 改动字段落盘；缺省 = 关闭（可选区块零影响）。
/// </summary>
public sealed class SettingsSystemOneRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "myth-settings-system-one-" + Guid.NewGuid().ToString("N")
    );

    public SettingsSystemOneRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试清理尽力而为
        }
    }

    [Fact]
    public async Task SystemOne_LoadsFields_AndPreservesUneditedOnSave()
    {
        var store = new ConfigStore(_dir);
        await store.SaveAsync(
            new ConfigData
            {
                SystemOne = new SystemOneConfig
                {
                    Enabled = true,
                    BaseUrl = "http://127.0.0.1:9999",
                    Path = "/decisions",
                    ApiKey = "sk-desk",
                    Model = "d1-omni-custom",
                    TimeoutMs = 12345,
                    MaxStateChars = 2345,
                    Rerank = new SystemOneRerankConfig { Enabled = false },
                    Conflicts = new SystemOneConflictConfig { Enabled = false },
                    Relations = new SystemOneRelationConfig { MinProbability = 0.8 },
                    Loop = new SystemOneLoopConfig
                    {
                        Enabled = true,
                        TextChars = 900,
                        StopCheck = new SystemOneStopCheckConfig { MinProbability = 0.9 },
                        DriftCheck = new SystemOneDriftCheckConfig { Enabled = false },
                        StabilityCheck = new SystemOneStabilityCheckConfig { Enabled = true },
                    },
                },
            }
        );

        var vm = new SettingsViewModel(store, null, null).Refresh();

        Assert.True(vm.SystemOneEnabled);
        Assert.Equal("http://127.0.0.1:9999", vm.SystemOneBaseUrl);
        Assert.Equal("/decisions", vm.SystemOnePath);
        Assert.Equal("sk-desk", vm.SystemOneApiKey);
        Assert.Equal("d1-omni-custom", vm.SystemOneModel);
        Assert.Equal("12345", vm.SystemOneTimeoutMs);
        Assert.Equal("2345", vm.SystemOneMaxStateChars);
        Assert.False(vm.SystemOneRerankEnabled);
        Assert.False(vm.SystemOneConflictsEnabled);
        Assert.True(vm.SystemOneRelationsEnabled);
        Assert.Equal("0.8", vm.SystemOneRelationsMinProbability);
        Assert.True(vm.SystemOneStopCheckEnabled);
        Assert.False(vm.SystemOneDriftCheckEnabled);
        Assert.True(vm.SystemOneStabilityCheckEnabled);

        // 改动两个字段；其余（含未在面板暴露的数值）必须原样保留
        vm.SystemOneEnabled = false;
        vm.SystemOneRelationsMinProbability = "0.6";
        vm.SystemOneStabilityCheckEnabled = false;
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(store, c => c.SystemOne.Relations.MinProbability == 0.6);

        Assert.False(saved.SystemOne.Enabled);
        Assert.Equal("http://127.0.0.1:9999", saved.SystemOne.BaseUrl);
        Assert.Equal("/decisions", saved.SystemOne.Path);
        Assert.Equal("sk-desk", saved.SystemOne.ApiKey);
        Assert.Equal("d1-omni-custom", saved.SystemOne.Model);
        Assert.Equal(12345, saved.SystemOne.TimeoutMs);
        Assert.Equal(2345, saved.SystemOne.MaxStateChars);
        Assert.False(saved.SystemOne.Rerank.Enabled);
        Assert.False(saved.SystemOne.Conflicts.Enabled);
        Assert.Equal(0.6, saved.SystemOne.Relations.MinProbability, 3);
        // 循环判定：稳定性复核已关 → 停止复核仍开 ⇒ 主开关保持 true；未暴露字段原样保留
        Assert.True(saved.SystemOne.Loop.Enabled);
        Assert.Equal(900, saved.SystemOne.Loop.TextChars);
        Assert.Equal(0.9, saved.SystemOne.Loop.StopCheck.MinProbability, 3);
        Assert.True(saved.SystemOne.Loop.StopCheck.Enabled);
        Assert.False(saved.SystemOne.Loop.DriftCheck.Enabled);
        Assert.False(saved.SystemOne.Loop.StabilityCheck.Enabled); // 开关翻转生效
    }

    [Fact]
    public async Task SystemOne_Defaults_RoundTripDisabled()
    {
        var store = new ConfigStore(_dir);
        var vm = new SettingsViewModel(store, null, null).Refresh();

        Assert.False(vm.SystemOneEnabled); // 可选区块：默认关闭
        vm.SaveCommand.Execute().Subscribe();

        var saved = await WaitForAsync(store, _ => File.Exists(Path.Combine(_dir, "config.yaml")));

        Assert.False(saved.SystemOne.Enabled);
        Assert.Equal(20_000, saved.SystemOne.TimeoutMs);
        Assert.Equal(3000, saved.SystemOne.MaxStateChars);
        Assert.Equal(0.7, saved.SystemOne.Relations.MinProbability, 3);
        // 循环判定默认全关：只关主开关，子开关原值保留（默认 true）
        Assert.False(saved.SystemOne.Loop.Enabled);
        Assert.True(saved.SystemOne.Loop.StopCheck.Enabled);
        Assert.Equal(1200, saved.SystemOne.Loop.TextChars);
    }

    private static async Task<ConfigData> WaitForAsync(
        ConfigStore store,
        Func<ConfigData, bool> done
    )
    {
        for (var i = 0; i < 100; i++)
        {
            var config = store.Load();
            if (done(config))
                return config;
            await Task.Delay(50);
        }

        Assert.Fail("System One 配置未在预期时间内落盘");
        return store.Load();
    }
}
