using System.Text;
using System.Text.Json;
using Core.Agent;
using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 双速自进化账本（单账本折叠）核心验收：
/// ① 折叠计数与 Laplace 胜率；② 退役阈值；③ 同小说同触发抑制；
/// ④ UCB1 探索项优先；⑤ 坏行抛异常（带行号）；另附键形契约与重建一致性。
/// </summary>
public sealed class TacticLedgerTests : IDisposable
{
    private const string Novel = "n1";
    private const string Genre = "都市爽文";

    private readonly string _dir;
    private readonly string _ledgerPath;

    public TacticLedgerTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "TacticLedgerTests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_dir);
        _ledgerPath = Path.Combine(_dir, "evolution", "ledger.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
        AtomicFile.ForgetPath(_ledgerPath);
    }

    /// <summary>以原始行写账本（折叠测试直接喂历史线，绕过写入 API）。</summary>
    private void WriteLedger(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        File.WriteAllText(_ledgerPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        AtomicFile.ForgetPath(_ledgerPath);
    }

    private static string Learn(
        string tacticId,
        string trigger,
        string genre = Genre,
        string prescription = "建议引入对抗或两难",
        string boundary = "来源：基础档 context.retrieve signals"
    ) =>
        $$"""
            {"kind":"learn","ts":10,"tactic":"{{tacticId}}","trigger":"{{trigger}}","genre":"{{genre}}","prescription":"{{prescription}}","boundary":"{{boundary}}"}
            """;

    private static string Inject(
        string tacticId,
        string trigger,
        long ts,
        int chapter,
        string novel = Novel
    ) =>
        $$"""
            {"kind":"inject","ts":{{ts}},"novel":"{{novel}}","chapter":{{chapter}},"tactic":"{{tacticId}}","trigger":"{{trigger}}"}
            """;

    private static string Verdict(
        string tacticId,
        long ts,
        int chapter,
        string outcome,
        string novel = Novel
    ) =>
        $$"""
            {"kind":"verdict","ts":{{ts}},"novel":"{{novel}}","chapter":{{chapter}},"tactic":"{{tacticId}}","outcome":"{{outcome}}"}
            """;

    [Fact]
    public void Fold_ComputesWinRateAndUses_AndStaysActive()
    {
        var tacticId = TacticLedger.IdFor("F-2", Genre);
        WriteLedger(
            Learn(tacticId, "F-2"),
            Inject(tacticId, "F-2", ts: 100, chapter: 1),
            Verdict(tacticId, ts: 101, chapter: 2, outcome: "pass"),
            Verdict(tacticId, ts: 102, chapter: 3, outcome: "fail"),
            Verdict(tacticId, ts: 103, chapter: 4, outcome: "pass"),
            Inject(tacticId, "F-2", ts: 104, chapter: 5)
        );

        var ledger = new TacticLedger(_dir);
        var stat = Assert.Single(ledger.Snapshot);

        Assert.Equal(tacticId, stat.Id);
        Assert.Equal(2, stat.Wins);
        Assert.Equal(1, stat.Losses);
        Assert.Equal(2, stat.Uses);
        Assert.Equal(104, stat.LastUsedUnix);
        Assert.Equal(0.60, stat.WinRate, 6); // (2+1)/(2+1+2) = 3/5
        Assert.Equal(TacticStatus.Active, stat.Status); // 结算样本 3 < 8
    }

    [Fact]
    public void Fold_RetiresTacticBelowThreshold_AndSelectReturnsEmpty()
    {
        var tacticId = TacticLedger.IdFor("F-2", Genre);
        var lines = new List<string> { Learn(tacticId, "F-2"), Inject(tacticId, "F-2", 200, 1) };
        for (var i = 0; i < 3; i++)
            lines.Add(Verdict(tacticId, 210 + i, 2 + i, "pass"));
        for (var i = 0; i < 6; i++)
            lines.Add(Verdict(tacticId, 220 + i, 5 + i, "fail"));
        WriteLedger([.. lines]);

        var ledger = new TacticLedger(_dir);
        var stat = Assert.Single(ledger.Snapshot);

        Assert.Equal(3, stat.Wins);
        Assert.Equal(6, stat.Losses);
        Assert.Equal(4.0 / 11, stat.WinRate, 6); // ≈0.3636 < 0.40
        Assert.Equal(TacticStatus.Retired, stat.Status);
        Assert.Empty(ledger.Select(["F-2"], Genre, Novel));
    }

    [Fact]
    public void Select_SuppressesAfterThreeFails_PerNovel()
    {
        var tacticId = TacticLedger.IdFor("F-2", Genre);
        WriteLedger(
            Learn(tacticId, "F-2"),
            Verdict(tacticId, ts: 301, chapter: 1, outcome: "fail"),
            Verdict(tacticId, ts: 302, chapter: 2, outcome: "fail"),
            Verdict(tacticId, ts: 303, chapter: 3, outcome: "fail")
        );

        var ledger = new TacticLedger(_dir);
        Assert.True(ledger.IsSuppressed(Novel, "F-2"));
        Assert.False(ledger.IsSuppressed("n2", "F-2"));
        Assert.Empty(ledger.Select(["F-2"], Genre, Novel)); // 同小说同触发 3 fail → 抑制
        Assert.Single(ledger.Select(["F-2"], Genre, "n2")); // 其他小说不受影响
    }

    [Fact]
    public void Select_PrefersUnexploredOverEightWins()
    {
        // 0 次使用：0.5 + sqrt(2·ln(9)/1) ≈ 2.60；8 次全胜：0.9 + sqrt(2·ln(9)/9) ≈ 1.60。
        var proven = TacticLedger.IdFor("F-2", Genre);
        var fresh = TacticLedger.IdFor("S-1(节奏)", Genre);
        var lines = new List<string> { Learn(proven, "F-2"), Learn(fresh, "S-1(节奏)") };
        for (var i = 0; i < 8; i++)
        {
            lines.Add(Inject(proven, "F-2", 400 + i, i + 1));
            lines.Add(Verdict(proven, 400 + i, i + 1, "pass"));
        }
        WriteLedger([.. lines]);

        var ledger = new TacticLedger(_dir);
        Assert.Equal(TacticStatus.Promoted, ledger.Find(proven)!.Status);

        var picks = ledger.Select(["F-2", "S-1(节奏)"], Genre, Novel);
        Assert.Equal(2, picks.Count);
        Assert.Equal(fresh, picks[0].Id);
        Assert.Equal(proven, picks[1].Id);
    }

    [Fact]
    public void Select_MatchesGenreOrUniversal_AndRejectsForeignGenre()
    {
        WriteLedger(
            Learn(TacticLedger.IdFor("F-2", Genre), "F-2", genre: Genre),
            Learn(TacticLedger.IdFor("F-2", ""), "F-2", genre: "")
        );

        var ledger = new TacticLedger(_dir);
        Assert.Equal(2, ledger.Select(["F-2"], Genre, Novel).Count); // 同题材 + 通用
        Assert.Single(ledger.Select(["F-2"], "悬疑", Novel)); // 异题材被排除，仅通用命中
    }

    [Theory]
    [InlineData("{not json", "第 1 行")] // JSON 解析失败
    [InlineData("""{"ts":1}""", "第 1 行")] // 缺 kind
    [InlineData("""{"kind":"unknown","ts":1}""", "第 1 行")] // 未知 kind
    [InlineData("""{"kind":"learn","ts":1}""", "第 1 行")] // 缺关键字段
    public void Fold_ThrowsOnBadLine_WithLineNumber(string line, string expectedFragment)
    {
        WriteLedger(line);

        var ex = Assert.Throws<InvalidDataException>(() => new TacticLedger(_dir));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Fold_SkipsBlankLines_AndMissesMissingFile()
    {
        WriteLedger(Learn(TacticLedger.IdFor("F-2", Genre), "F-2"), "", "");

        var ledger = new TacticLedger(_dir);
        Assert.Single(ledger.Snapshot);

        var missing = new TacticLedger(Path.Combine(_dir, "not-created"));
        Assert.Empty(missing.Snapshot);
    }

    [Fact]
    public async Task Append_RoundTrips_AndKeepsFlatLowerCaseKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var tacticId = TacticLedger.IdFor("S-1(节奏)", Genre);
        var ledger = new TacticLedger(_dir);

        await ledger.LearnAsync(
            new TacticStat(tacticId, "S-1(节奏)", Genre, "建议引入对抗", "来源：基础档"),
            ct
        );
        var tactic = ledger.Find(tacticId)!;
        await ledger.InjectAsync(Novel, 12, tactic, ct);
        await ledger.DiagnoseAsync(Novel, 12, Genre, "action", ["S-1(节奏)"], ["节奏"], ct);
        await ledger.VerdictAsync(Novel, 13, tactic, pass: false, ct);

        // 键形契约：扁平小写键（课程 CLI 直接按键读取，改名即破）。
        var lines = File.ReadAllLines(_ledgerPath);
        Assert.Equal(4, lines.Length);

        using (var learn = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("learn", learn.RootElement.GetProperty("kind").GetString());
            Assert.Equal(tacticId, learn.RootElement.GetProperty("tactic").GetString());
            Assert.Equal("S-1(节奏)", learn.RootElement.GetProperty("trigger").GetString());
            Assert.Equal(Genre, learn.RootElement.GetProperty("genre").GetString());
            Assert.Equal("建议引入对抗", learn.RootElement.GetProperty("prescription").GetString());
        }
        using (var inject = JsonDocument.Parse(lines[1]))
        {
            Assert.Equal("inject", inject.RootElement.GetProperty("kind").GetString());
            Assert.Equal(12, inject.RootElement.GetProperty("chapter").GetInt32());
            Assert.True(inject.RootElement.GetProperty("ts").GetInt64() > 0);
        }
        using (var diagnose = JsonDocument.Parse(lines[2]))
        {
            Assert.Equal("diagnose", diagnose.RootElement.GetProperty("kind").GetString());
            Assert.Equal("action", diagnose.RootElement.GetProperty("verdict").GetString());
            Assert.Equal("S-1(节奏)", diagnose.RootElement.GetProperty("codes")[0].GetString());
            Assert.Equal("节奏", diagnose.RootElement.GetProperty("dims")[0].GetString());
        }
        using (var verdict = JsonDocument.Parse(lines[3]))
        {
            Assert.Equal("fail", verdict.RootElement.GetProperty("outcome").GetString());
        }

        // 折叠重建：新实例读回同状态（Wins/Losses/Uses 与 failCounts 一致）。
        var reloaded = new TacticLedger(_dir);
        var stat = reloaded.Find(tacticId)!;
        Assert.Equal(0, stat.Wins);
        Assert.Equal(1, stat.Losses);
        Assert.Equal(1, stat.Uses);
        Assert.False(reloaded.IsSuppressed(Novel, "S-1(节奏)"));
    }

    [Fact]
    public void StopRequested_ReflectsStopFile()
    {
        var ledger = new TacticLedger(_dir);
        Assert.False(ledger.StopRequested);

        Directory.CreateDirectory(Path.Combine(_dir, "evolution"));
        File.WriteAllText(Path.Combine(_dir, "evolution", "evolution-stop"), "");

        Assert.True(ledger.StopRequested);
    }

    [Fact]
    public async Task LearnAsync_IgnoresDuplicateId()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = new TacticLedger(_dir);
        var tacticId = TacticLedger.IdFor("F-2", Genre);

        await ledger.LearnAsync(new TacticStat(tacticId, "F-2", Genre, "首次处方", "边界"), ct);
        await ledger.LearnAsync(new TacticStat(tacticId, "F-2", Genre, "二次处方", "边界"), ct);

        var lines = File.ReadAllLines(_ledgerPath);
        Assert.Single(lines); // 同 id 只写一条 learn
        Assert.Equal("首次处方", ledger.Find(tacticId)!.Prescription); // 处方取首次观测文本
    }
}
