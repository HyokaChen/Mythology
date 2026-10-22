using Desktop.Services;
using Xunit;

namespace Desktop.Tests;

/// <summary>版本归一化/比较（自动更新核心判定：仅 current &lt; latest 才算有更新）。</summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v0.0.9", "0.0.9")]
    [InlineData("0.0.9+g60ae392", "0.0.9")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData(" 0.0.2-alpha.1 ", "0.0.2-alpha.1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsPrefixAndBuildMetadata(string? raw, string expected) =>
        Assert.Equal(expected, UpdateVersion.Normalize(raw));

    [Theory]
    [InlineData("v0.0.9", "0.0.9+g60ae392", 0)] // 同版本（剥 +sha）不误报
    [InlineData("0.0.9", "0.0.10", -1)] // 数值比较：9 < 10（字符串比较会判反）
    [InlineData("0.1.0", "0.0.9", 1)]
    [InlineData("0.0.9", "0.0.9.1", -1)] // 缺段补 0
    [InlineData(null, "0.0.9", -1)]
    [InlineData("0.0.2-alpha.1", "v0.0.2-alpha.1", 0)]
    public void Compare_ReturnsSign(string? a, string? b, int expected) =>
        Assert.Equal(expected, UpdateVersion.Compare(a, b));
}

/// <summary>RID → 资产名映射（与 installer/build-*.{sh,ps1} 的产物命名是同一契约）。</summary>
public sealed class UpdateAssetMapTests
{
    [Theory]
    [InlineData("win-x64", "0.0.2-alpha.1", "Mythology-Desktop-0.0.2-alpha.1-win-x64-setup.exe")]
    [InlineData("linux-x64", "0.0.3", "Mythology-Desktop-0.0.3-linux-x64.deb")]
    [InlineData("osx-arm64", "0.0.3", "Mythology-Desktop-0.0.3-osx-arm64.dmg")]
    public void InstallerNameFor_MapsKnownRids(string rid, string version, string expected) =>
        Assert.Equal(expected, UpdateAssetMap.InstallerNameFor(rid, version));

    [Theory]
    [InlineData("linux-arm64", "1.0.0")]
    [InlineData("win10-x64", "1.0.0")]
    [InlineData("win-x64", "")]
    [InlineData("", "1.0.0")]
    public void InstallerNameFor_UnknownRidOrEmpty_ReturnsNull(string rid, string version) =>
        Assert.Null(UpdateAssetMap.InstallerNameFor(rid, version));
}

/// <summary>东八区 03:00 对齐的调度时长（固定 +8，无夏令时）。</summary>
public sealed class UpdateCheckSchedulerDelayTests
{
    [Fact]
    public void BeforeThree_ReturnsTimeToToday()
    {
        var now = new DateTimeOffset(2026, 10, 18, 2, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(1), UpdateCheckScheduler.NextCheckDelay(now));
    }

    [Fact]
    public void ExactlyThree_JumpsToNextDay()
    {
        var now = new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(24), UpdateCheckScheduler.NextCheckDelay(now));
    }

    [Fact]
    public void AfterThree_ReturnsTimeToTomorrow()
    {
        var now = new DateTimeOffset(2026, 10, 18, 4, 30, 0, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(22.5), UpdateCheckScheduler.NextCheckDelay(now));
    }
}
