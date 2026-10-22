using CloudDiagnosis;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// HARNESS-CLOUD §16.5 方案 B/C：每小说预算池 + 预算银行。
/// 覆盖：初始化池 / 消费扣减 / 银行优先 / 跨实例持久化往返 / 会话结算结余入银行。
/// </summary>
public sealed class NovelBudgetStoreTests
{
    private static string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"novel_budget_{Guid.NewGuid():N}.db");
        return path;
    }

    [Fact]
    public void GetOrCreate_FirstUse_InitializesPool()
    {
        var store = new NovelBudgetStore();
        var state = store.GetOrCreate("novel_a", deepBudgetPerNovel: 20);

        Assert.Equal(20, state.PoolRemaining);
        Assert.Equal(0, state.BankBalance);
        Assert.Equal(20, state.TotalRemaining);
    }

    [Fact]
    public void TryConsume_DecrementsPool()
    {
        var store = new NovelBudgetStore();
        var ok = store.TryConsume("novel_a", deepBudgetPerNovel: 20);

        Assert.True(ok);
        var state = store.GetOrCreate("novel_a", 20);
        Assert.Equal(19, state.PoolRemaining);
        Assert.Equal(1, state.SessionUsed);
    }

    [Fact]
    public void TryConsume_PoolExhausted_ThenUsesBank()
    {
        var store = new NovelBudgetStore();
        // 池 = 5：用 1 次后池剩 4（会话已用 1）
        Assert.True(store.TryConsume("novel_a", deepBudgetPerNovel: 5));

        // 会话结算（总量守恒）：leftover = min(4-1, 池剩 4) = 3 → 池 4-3=1、银行 3
        var settled = store.SettleSession(
            "novel_a",
            deepBudgetPerNovel: 5,
            deepBudgetPerSession: 4
        );
        Assert.Equal(3, settled.BankBalance);
        Assert.Equal(1, settled.PoolRemaining);

        // 银行优先：扣银行
        Assert.True(store.TryConsume("novel_a", deepBudgetPerNovel: 5));
        var after = store.GetOrCreate("novel_a", 5);
        Assert.Equal(2, after.BankBalance);
        Assert.Equal(1, after.PoolRemaining);
    }

    [Fact]
    public void TryConsume_AllExhausted_ReturnsFalse()
    {
        var store = new NovelBudgetStore();
        // 池 = 1 用掉，银行 0
        Assert.True(store.TryConsume("novel_a", deepBudgetPerNovel: 1));
        Assert.False(store.TryConsume("novel_a", deepBudgetPerNovel: 1));
    }

    [Fact]
    public void Persistence_CrossInstance_RoundTrips()
    {
        var db = TempDb();
        try
        {
            var store1 = new NovelBudgetStore(db);
            store1.TryConsume("novel_a", deepBudgetPerNovel: 20);
            store1.SettleSession("novel_a", deepBudgetPerNovel: 20, deepBudgetPerSession: 5);

            // 新实例（模拟新会话）回源：总量守恒——leftover=4 从池转入银行，池 19-4=15
            var store2 = new NovelBudgetStore(db);
            var state = store2.GetOrCreate("novel_a", 20);
            Assert.Equal(15, state.PoolRemaining);
            Assert.Equal(4, state.BankBalance); // 5 - 1 会话已用 = 4 结余
            Assert.Equal(19, state.TotalRemaining); // 池+银行 = 20 - 已用 1，无膨胀
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池持有物理句柄：先清池再删文件，否则 Windows 上 IOException
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    /// <summary>跨会话自愈（2026-08-28 增强）：宿主退出未显式结算时，回源发现残留 SessionUsed
    /// 自动把未用配额转入银行并清零（回源即跨会话边界，等价宿主退出隐式结算）。</summary>
    [Fact]
    public void GetOrCreate_CrossSession_StaleSessionUsed_SettlesToBankAutomatically()
    {
        var db = TempDb();
        try
        {
            var store1 = new NovelBudgetStore(db);
            store1.TryConsume("novel_a", deepBudgetPerNovel: 20); // 会话用了 1 次，宿主退出未结算

            // 新实例（模拟新会话/新进程）回源：传 deepBudgetPerSession → 残留自动结算入银行
            // 总量守恒：leftover=4 从池转入银行，池 19-4=15；Pool+Bank = 20-1 无膨胀
            var store2 = new NovelBudgetStore(db);
            var state = store2.GetOrCreate("novel_a", 20, deepBudgetPerSession: 5);

            Assert.Equal(15, state.PoolRemaining);
            Assert.Equal(4, state.BankBalance); // 5 - 1 残留 = 4 结余
            Assert.Equal(0, state.SessionUsed); // 清零，新会话额度完整
            Assert.Equal(19, state.TotalRemaining);
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池持有物理句柄：先清池再删文件，否则 Windows 上 IOException
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    /// <summary>不传 deepBudgetPerSession → 保持旧语义（不自动结算，兼容既有调用）。</summary>
    [Fact]
    public void GetOrCreate_WithoutDeepBudgetPerSession_KeepsStaleSessionUsed()
    {
        var db = TempDb();
        try
        {
            var store1 = new NovelBudgetStore(db);
            store1.TryConsume("novel_a", deepBudgetPerNovel: 20);

            var store2 = new NovelBudgetStore(db);
            var state = store2.GetOrCreate("novel_a", 20);
            Assert.Equal(1, state.SessionUsed); // 旧语义：原样回源
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池持有物理句柄：先清池再删文件，否则 Windows 上 IOException
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public void IsExhausted_ConsidersPoolAndBank()
    {
        var store = new NovelBudgetStore();
        // 池 1 用掉，会话预算 1 也用掉 → 耗尽
        Assert.True(store.TryConsume("novel_a", deepBudgetPerNovel: 1));
        Assert.True(store.IsExhausted("novel_a", deepBudgetPerNovel: 1, deepBudgetPerSession: 1));

        // 会话结算（守恒：池已空 → leftover=0，无新增额度）→ 会话清零，不再视为耗尽
        store.SettleSession("novel_a", deepBudgetPerNovel: 1, deepBudgetPerSession: 3);
        Assert.False(store.IsExhausted("novel_a", deepBudgetPerNovel: 1, deepBudgetPerSession: 1));
    }

    /// <summary>产品决策（2026-08-28）：结算总量守恒——Pool+Bank 恒等于 DeepBudgetPerNovel − 累计消耗，
    /// 连续结算不膨胀、不刷额度。</summary>
    [Fact]
    public void SettleSession_TotalConserved_NoInflation()
    {
        var store = new NovelBudgetStore();
        // 池 20：用 5 次（SessionUsed 5、池 15）
        for (var i = 0; i < 5; i++)
            Assert.True(store.TryConsume("novel_a", deepBudgetPerNovel: 20));

        // 首次结算：leftover = min(20-5, 池 15) = 15 → 池 0、银行 15、总量 15 = 20-5
        var settled = store.SettleSession(
            "novel_a",
            deepBudgetPerNovel: 20,
            deepBudgetPerSession: 20
        );
        Assert.Equal(0, settled.PoolRemaining);
        Assert.Equal(15, settled.BankBalance);
        Assert.Equal(15, settled.TotalRemaining);

        // 再次结算：池已空 → leftover=0，总量不变（不膨胀）
        var again = store.SettleSession(
            "novel_a",
            deepBudgetPerNovel: 20,
            deepBudgetPerSession: 20
        );
        Assert.Equal(15, again.TotalRemaining);
        Assert.Equal(15, again.BankBalance);

        // 多次切换式结算也不膨胀：总量始终 = 20 - 已用
        for (var i = 0; i < 10; i++)
            _ = store.SettleSession("novel_a", deepBudgetPerNovel: 20, deepBudgetPerSession: 20);
        var final = store.GetOrCreate("novel_a", 20);
        Assert.Equal(15, final.TotalRemaining);
    }
}
