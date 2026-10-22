using Core.Agent;
using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 上下文管理配置（YAML 段 context_management）：默认值、往返、模式归一
/// （拼错不得静默切到 continuation）、与运行期选项的映射。
/// </summary>
public sealed class ContextManagementConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ContextManagementConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mythology-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
    }

    [Fact]
    public void Defaults_AreCompactMode_WithReminderAndNoVector()
    {
        var config = new ConfigStore(_tempDir).Load();

        Assert.Equal("compact", config.ContextManagement.Mode);
        Assert.Equal(0, config.ContextManagement.ReminderPercent); // 0 = 按模型窗口推导
        Assert.False(config.ContextManagement.VectorRetrieval);
        Assert.Equal(ContextMode.Compact, config.ContextManagement.ToOptions().Mode);
    }

    [Fact]
    public async Task RoundTrip_PreservesContinuationSettings()
    {
        var store = new ConfigStore(_tempDir);
        var config = store.Load();
        config.ContextManagement.Mode = "continuation";
        config.ContextManagement.ReminderPercent = 55;
        config.ContextManagement.VectorRetrieval = true;
        await store.SaveAsync(config);

        var reloaded = new ConfigStore(_tempDir).Load();

        Assert.Equal("continuation", reloaded.ContextManagement.Mode);
        Assert.Equal(55, reloaded.ContextManagement.ReminderPercent);
        Assert.True(reloaded.ContextManagement.VectorRetrieval);
    }

    [Theory]
    [InlineData("continuation", ContextMode.Continuation)]
    [InlineData("  CONTINUATION  ", ContextMode.Continuation)]
    [InlineData("compact", ContextMode.Compact)]
    [InlineData("", ContextMode.Compact)]
    [InlineData("continuatoin", ContextMode.Compact)] // 拼错 → 不静默切换
    [InlineData(null, ContextMode.Compact)]
    public void ToOptions_NormalizesMode(string? mode, ContextMode expected)
    {
        var config = new ContextManagementConfig { Mode = mode! };

        Assert.Equal(expected, config.ToOptions().Mode);
    }

    [Fact]
    public void ToOptions_ClampsReminderPercentToValidRange()
    {
        Assert.Equal(
            0,
            new ContextManagementConfig { ReminderPercent = -5 }
                .ToOptions()
                .ReminderPercent
        );
        Assert.Equal(
            99,
            new ContextManagementConfig { ReminderPercent = 250 }
                .ToOptions()
                .ReminderPercent
        );
        Assert.Equal(
            80,
            new ContextManagementConfig { ReminderPercent = 80 }
                .ToOptions()
                .ReminderPercent
        );
    }
}
