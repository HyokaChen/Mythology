using Core.Data;
using Tui.Controls;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// SystemOneSection（嵌入页页脚「本地判定 · 可选」区块）保存契约：
/// 默认配置下 BuildSystemOneConfig 不改变任何字段；未编辑的数值字段原样保留
/// （防止「保存把未编辑字段清零」回归）。
/// </summary>
public sealed class SystemOneSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "myth-systemone-" + Guid.NewGuid().ToString("N")
    );

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
    public void BuildSystemOneConfig_Defaults_Unchanged()
    {
        var service = new SettingsService(new ConfigStore(_dir));
        service.LoadAll();
        var section = new SystemOneSection(service);

        var config = section.BuildSystemOneConfig();

        Assert.Equal(service.SystemOne, config);
    }

    [Fact]
    public void BuildSystemOneConfig_PreservesUneditedValues()
    {
        var store = new ConfigStore(_dir);
        store
            .SaveAsync(
                new ConfigData
                {
                    SystemOne = new SystemOneConfig
                    {
                        Enabled = true,
                        Path = "/decisions",
                        ApiKey = "sk-tui",
                        TimeoutMs = 12345,
                        MaxStateChars = 2345,
                        Rerank = new SystemOneRerankConfig { Enabled = false },
                        Relations = new SystemOneRelationConfig { MinProbability = 0.8 },
                        Loop = new SystemOneLoopConfig
                        {
                            Enabled = true,
                            TextChars = 900,
                            StopCheck = new SystemOneStopCheckConfig
                            {
                                MinProbability = 0.9,
                                MaxPerRun = 2,
                            },
                            DriftCheck = new SystemOneDriftCheckConfig { Enabled = false },
                            StabilityCheck = new SystemOneStabilityCheckConfig { Enabled = true },
                        },
                    },
                }
            )
            .GetAwaiter()
            .GetResult();
        var service = new SettingsService(store);
        service.LoadAll();
        var section = new SystemOneSection(service);

        var config = section.BuildSystemOneConfig();

        Assert.True(config.Enabled);
        Assert.Equal("/decisions", config.Path);
        Assert.Equal("sk-tui", config.ApiKey);
        Assert.Equal(12345, config.TimeoutMs);
        Assert.Equal(2345, config.MaxStateChars);
        Assert.False(config.Rerank.Enabled);
        Assert.Equal(0.8, config.Relations.MinProbability, 3);
        // 循环判定：开关初值 = 有效值（master ∧ 子开关）→ 漂移复核已关 ⇒ 三开关非全开，
        // 但停止复核/稳定性复核仍开 ⇒ 主开关保持 true；未暴露的数值字段原样保留。
        Assert.True(config.Loop.Enabled);
        Assert.Equal(900, config.Loop.TextChars);
        Assert.Equal(0.9, config.Loop.StopCheck.MinProbability, 3);
        Assert.Equal(2, config.Loop.StopCheck.MaxPerRun);
        Assert.True(config.Loop.StopCheck.Enabled);
        Assert.False(config.Loop.DriftCheck.Enabled);
        Assert.True(config.Loop.StabilityCheck.Enabled);
    }

    /// <summary>
    /// 三开关全关（默认配置：master=false + 子开关 true）→ 保存只关主开关，子开关与数值字段原值保留。
    /// </summary>
    [Fact]
    public void BuildSystemOneConfig_LoopAllOff_OnlyWritesMasterOff()
    {
        var store = new ConfigStore(_dir);
        store
            .SaveAsync(
                new ConfigData
                {
                    SystemOne = new SystemOneConfig
                    {
                        Loop = new SystemOneLoopConfig { TextChars = 777 },
                    },
                }
            )
            .GetAwaiter()
            .GetResult();
        var service = new SettingsService(store);
        service.LoadAll();
        var section = new SystemOneSection(service);

        var config = section.BuildSystemOneConfig();

        Assert.False(config.Loop.Enabled);
        Assert.True(config.Loop.StopCheck.Enabled); // 子键原值保留（不做无意义写入）
        Assert.True(config.Loop.DriftCheck.Enabled);
        Assert.True(config.Loop.StabilityCheck.Enabled);
        Assert.Equal(777, config.Loop.TextChars);
    }
}
