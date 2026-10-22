using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// CloudLoginDialog 登录保存逻辑测试（§13 Tier 2 TUI 对齐，与 Desktop ResolveExpiry 语义一致）：
/// - 服务端签发（OAuth/设备流）：服务端 expiresAt 优先采用真值，缺失回退 now + 30d；
/// - 手动 Key：ExpiresAt = null 永不本地过期（有效性由服务端 401/403 裁决）。
/// 保存逻辑抽为 internal 静态工厂（CloudLoginDialog.CreateServerSession / CreateManualKeySession），
/// 测试经 InternalsVisibleTo 访问——与 MythMark 的 internal 测试缝同模式。
/// </summary>
public sealed class CloudLoginDialogTests
{
    private const string ValidKey = "mk_live_dev123_secret";

    [Fact]
    public void CreateServerSession_WithServerExpiresAt_AdoptsServerTruth()
    {
        var server = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var session = CloudLoginDialog.CreateServerSession(
            ValidKey,
            "dev123",
            "ws1",
            "free",
            server
        );

        // 服务端说了算：采用真值而非客户端 +30d 默认
        Assert.Equal(server, session.ExpiresAt);
        Assert.Equal(ValidKey, session.ApiKey);
        Assert.Equal("ws1", session.WorkspaceId);
        Assert.Equal("free", session.Plan);
    }

    [Fact]
    public void CreateServerSession_WithoutServerExpiresAt_FallsBackTo30Days()
    {
        var session = CloudLoginDialog.CreateServerSession(ValidKey, "dev123", "ws1", "free", null);

        // 旧服务端不返回 expiresAt → 回退客户端滑动 30 天（向后兼容，与 Desktop 一致）
        Assert.NotNull(session.ExpiresAt);
        Assert.InRange(
            session.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(30).AddSeconds(-5),
            DateTimeOffset.UtcNow.AddDays(30).AddSeconds(5)
        );
    }

    [Fact]
    public void CreateServerSession_WithPastServerExpiresAt_AdoptsAsIs()
    {
        // 与 Desktop ResolveExpiry 完全对齐：不防御过去值——过期/矛盾响应由后续
        // RenewIfDue 的防御逻辑（服务端真值需为未来时间，否则回退滑动）纠正。
        var past = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var session = CloudLoginDialog.CreateServerSession(ValidKey, "dev123", "ws1", "free", past);

        Assert.Equal(past, session.ExpiresAt);
    }

    [Fact]
    public void CreateManualKeySession_ExpiresAtNull_NeverLocallyExpires()
    {
        var session = CloudLoginDialog.CreateManualKeySession(ValidKey, "dev123");

        // 手动 Key 永不本地过期，有效性完全由服务端 401/403 裁决（与 Desktop 语义对齐）
        Assert.Null(session.ExpiresAt);
        Assert.Equal("dev123", session.PublicId);
        Assert.Equal("", session.WorkspaceId); // 手动 Key 无工作区
        Assert.Equal("free", session.Plan);
    }
}
