using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Shared;

namespace Core.Agent;

/// <summary>战术生命周期状态（双速自进化「单账本折叠」派生）。</summary>
public enum TacticStatus
{
    /// <summary>观察中（结算样本不足，或胜率居中）。</summary>
    Active,

    /// <summary>已晋升（结算样本 ≥8 且胜率 ≥ 0.70）。</summary>
    Promoted,

    /// <summary>已退役（结算样本 ≥8 且胜率 &lt; 0.40；不再参与注入选择）。</summary>
    Retired,
}

/// <summary>
/// 单条战术统计（可变计数：每事件就地更新，不产生分配）。
/// 派生量 <see cref="WinRate"/> / <see cref="Status"/> 为纯函数，不落盘、不缓存。
/// </summary>
public sealed class TacticStat(
    string id,
    string trigger,
    string genre,
    string prescription,
    string boundary
)
{
    /// <summary>战术 ID（<c>tt-</c> + trigger|genre 内容哈希前 8 位）。</summary>
    public string Id { get; } = id;

    /// <summary>触发该战术的行动项规则码（SignalRules.ActionItem.Code）。</summary>
    public string Trigger { get; } = trigger;

    /// <summary>来源题材（空 = 跨题材通用）。</summary>
    public string Genre { get; } = genre;

    /// <summary>处方文本（首次观测绑定，后续 learn 不覆盖）。</summary>
    public string Prescription { get; } = prescription;

    /// <summary>边界声明文本（首次观测绑定）。</summary>
    public string Boundary { get; } = boundary;

    /// <summary>胜场数（verdict:pass 计数）。</summary>
    public int Wins { get; set; }

    /// <summary>败场数（verdict:fail 计数）。</summary>
    public int Losses { get; set; }

    /// <summary>注入次数（inject 事件计数）。</summary>
    public int Uses { get; set; }

    /// <summary>最近一次注入的 unix 秒时间戳（0 = 从未注入）。</summary>
    public long LastUsedUnix { get; set; }

    /// <summary>Laplace 平滑胜率（(Wins+1)/(Wins+Losses+2)）。</summary>
    public double WinRate => (Wins + 1d) / (Wins + Losses + 2);

    /// <summary>生命周期状态（结算样本 &lt;8 恒 Active）。</summary>
    public TacticStatus Status =>
        Wins + Losses < 8 ? TacticStatus.Active
        : WinRate >= 0.70 ? TacticStatus.Promoted
        : WinRate < 0.40 ? TacticStatus.Retired
        : TacticStatus.Active;
}

/// <summary>待结算的注入登记（内存态，不落盘；注入时登记，结算时按 tacticId 移除）。</summary>
public readonly record struct PendingInjection(
    string TacticId,
    string Trigger,
    string NovelId,
    int Chapter
);

/// <summary>
/// 双速自进化账本（单账本折叠架构）：唯一持久化为 append-only 的
/// <c>&lt;mythologyDir&gt;/evolution/ledger.jsonl</c>，四种事件（diagnose/learn/inject/verdict，扁平小写键）；
/// 战术统计、抑制、待结算全部由构造时的单次折叠派生——无第二份持久化。
/// <para>
/// 线程安全：会话状态（<c>_tactics</c> / <c>_failCounts</c> / <c>_pending</c>）无锁，**假设单写者**——
/// 同一时刻只由单个 Agent 写作循环调用（宿主按 novelId 隔离，不做多小说并发注入）。若将来允许并发调用，
/// 须在调用侧串行化或为这些状态补锁。构造时的一次折叠与依赖注入的只读参数不受此约束。
/// </para>
/// </summary>
public sealed class TacticLedger
{
    /// <summary>抑制阈值：同 (novel, trigger) 累计 fail 达此值后不再对该小说注入该触发规则的战术。</summary>
    private const int SuppressThreshold = 3;

    private readonly string _path;
    private readonly Dictionary<string, TacticStat> _tactics = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Novel, string Trigger), int> _failCounts = new();
    private readonly Dictionary<string, PendingInjection> _pending = new(StringComparer.Ordinal);

    /// <summary>构造时折叠一次账本（文件不存在 = 空状态；坏行抛异常不静默吞）。</summary>
    /// <param name="mythologyDir">Mythology 配置目录（~/.mythology）。</param>
    public TacticLedger(string mythologyDir)
    {
        _path = Path.Combine(mythologyDir, "evolution", "ledger.jsonl");
        Load();
    }

    /// <summary>战术统计快照（折叠缓存的值集合视图；供统计工具读取，不重读文件）。</summary>
    public IReadOnlyCollection<TacticStat> Snapshot => _tactics.Values;

    /// <summary>待结算注入快照（调用方按 novelId + 章序 结算并出队）。</summary>
    public IReadOnlyCollection<PendingInjection> PendingInjections => _pending.Values;

    /// <summary>
    /// 停机开关：账本同目录存在 <c>evolution-stop</c> 文件时所有写入与注入短路。
    /// 每次调用现查（一次 stat），不缓存——保证 <c>touch</c> 立即生效。
    /// </summary>
    public bool StopRequested =>
        File.Exists(Path.Combine(Path.GetDirectoryName(_path)!, "evolution-stop"));

    /// <summary>战术 ID：<c>"tt-" + SHA256(trigger + "|" + genre)</c> 前 8 位小写 hex。</summary>
    public static string IdFor(string trigger, string genre) =>
        "tt-"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trigger + "|" + genre)))[
            ..8
        ];

    /// <summary>按战术 ID 取统计（不存在返回 null）。</summary>
    public TacticStat? Find(string tacticId) => _tactics.GetValueOrDefault(tacticId);

    /// <summary>抑制判定：同 (novel, trigger) 的累计 fail 达阈值。</summary>
    public bool IsSuppressed(string novelId, string trigger) =>
        _failCounts.GetValueOrDefault((novelId, trigger)) >= SuppressThreshold;

    /// <summary>
    /// UCB1 选择（确定性，无 RNG）：一次调用处理本章全部 trigger，返回前 <paramref name="k"/> 条。
    /// 候选 = trigger 命中 ∪ 未退役 ∪ 题材匹配（空题材 = 通用）∪ 未被 (novelId, trigger) 抑制；
    /// 排名 <c>WinRate + sqrt(2·ln(totalUses+1) / (Uses+1))</c>，并列按 Id 序稳定排列。
    /// </summary>
    public IReadOnlyList<TacticStat> Select(
        IEnumerable<string> triggers,
        string genre,
        string novelId,
        int k = 3
    )
    {
        var triggerSet =
            triggers as ICollection<string> ?? triggers.ToHashSet(StringComparer.Ordinal);
        if (triggerSet.Count == 0)
            return [];

        var totalUses = 0L;
        foreach (var stat in _tactics.Values)
            totalUses += stat.Uses;

        var ranked = new List<(TacticStat Stat, double Score)>();
        foreach (var stat in _tactics.Values)
        {
            if (!triggerSet.Contains(stat.Trigger))
                continue;
            if (stat.Status == TacticStatus.Retired)
                continue;
            if (
                !string.IsNullOrEmpty(stat.Genre)
                && !string.Equals(stat.Genre, genre, StringComparison.Ordinal)
            )
                continue;
            if (IsSuppressed(novelId, stat.Trigger))
                continue;
            var score = stat.WinRate + Math.Sqrt(2 * Math.Log(totalUses + 1) / (stat.Uses + 1));
            ranked.Add((stat, score));
        }

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Stat.Id, StringComparer.Ordinal)
            .Take(k)
            .Select(x => x.Stat)
            .ToList();
    }

    /// <summary>记录一章判定（diagnose 事件；不参与战术统计，供失败簇课程直接读文件消费）。</summary>
    public async Task DiagnoseAsync(
        string novel,
        int chapter,
        string genre,
        string verdict,
        IReadOnlyList<string> codes,
        IReadOnlyList<string> dims,
        CancellationToken ct
    )
    {
        await AppendAsync(
            new LedgerLine
            {
                Kind = "diagnose",
                Ts = NowUnix(),
                Novel = novel,
                Chapter = chapter,
                Genre = genre,
                Verdict = verdict,
                Codes = [.. codes],
                Dims = [.. dims],
            },
            ct
        );
    }

    /// <summary>首次见到该战术时写一条 learn（处方/边界取首次观测文本；已存在则忽略）。</summary>
    public async Task LearnAsync(TacticStat tactic, CancellationToken ct)
    {
        if (_tactics.ContainsKey(tactic.Id))
            return;
        await AppendAsync(
            new LedgerLine
            {
                Kind = "learn",
                Ts = NowUnix(),
                Tactic = tactic.Id,
                Trigger = tactic.Trigger,
                Genre = tactic.Genre,
                Prescription = tactic.Prescription,
                Boundary = tactic.Boundary,
            },
            ct
        );
        _tactics[tactic.Id] = tactic;
    }

    /// <summary>登记一次战术注入（inject 行 + Uses/LastUsedUnix + 待结算登记；就地更新不重读文件）。</summary>
    public async Task InjectAsync(
        string novel,
        int chapter,
        TacticStat tactic,
        CancellationToken ct
    )
    {
        var ts = NowUnix();
        await AppendAsync(
            new LedgerLine
            {
                Kind = "inject",
                Ts = ts,
                Novel = novel,
                Chapter = chapter,
                Tactic = tactic.Id,
                Trigger = tactic.Trigger,
            },
            ct
        );
        tactic.Uses++;
        tactic.LastUsedUnix = ts;
        _pending[tactic.Id] = new PendingInjection(tactic.Id, tactic.Trigger, novel, chapter);
    }

    /// <summary>结算一次注入（verdict 行 + Wins/Losses；fail 计入 (novel, trigger) 抑制计数）。</summary>
    public async Task VerdictAsync(
        string novel,
        int chapter,
        TacticStat tactic,
        bool pass,
        CancellationToken ct
    )
    {
        await AppendAsync(
            new LedgerLine
            {
                Kind = "verdict",
                Ts = NowUnix(),
                Novel = novel,
                Chapter = chapter,
                Tactic = tactic.Id,
                Outcome = pass ? "pass" : "fail",
            },
            ct
        );
        if (pass)
        {
            tactic.Wins++;
        }
        else
        {
            tactic.Losses++;
            var key = (novel, tactic.Trigger);
            _failCounts[key] = _failCounts.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>出队一条待结算注入（结算后调用；无论是否写出 verdict 都要出队）。</summary>
    public void RemovePending(string tacticId) => _pending.Remove(tacticId);

    /// <summary>
    /// 单遍折叠：读账本重建内存状态。坏行（JSON 解析失败 / 未知 kind / 缺关键字段）抛
    /// <see cref="InvalidDataException"/>（消息含行号），不静默吞；文件不存在 = 空状态。
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
            return;

        var lineNo = 0;
        foreach (var line in File.ReadLines(_path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            LedgerLine? evt;
            try
            {
                evt = JsonSerializer.Deserialize<LedgerLine>(line, JsonHelper.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"ledger.jsonl 第 {lineNo} 行解析失败: {ex.Message}",
                    ex
                );
            }
            if (evt is null || string.IsNullOrEmpty(evt.Kind))
                throw new InvalidDataException($"ledger.jsonl 第 {lineNo} 行缺少 kind。");

            switch (evt.Kind)
            {
                case "learn":
                    if (string.IsNullOrEmpty(evt.Tactic))
                        throw new InvalidDataException(
                            $"ledger.jsonl 第 {lineNo} 行 learn 缺少 tactic。"
                        );
                    if (!_tactics.ContainsKey(evt.Tactic))
                        _tactics[evt.Tactic] = new TacticStat(
                            evt.Tactic,
                            evt.Trigger ?? "",
                            evt.Genre ?? "",
                            evt.Prescription ?? "",
                            evt.Boundary ?? ""
                        );
                    break;

                case "inject":
                    if (string.IsNullOrEmpty(evt.Tactic) || evt.Chapter is not { } injectChapter)
                        throw new InvalidDataException(
                            $"ledger.jsonl 第 {lineNo} 行 inject 缺少 tactic/chapter。"
                        );
                    var trigger = evt.Trigger ?? "";
                    if (_tactics.TryGetValue(evt.Tactic, out var injected))
                    {
                        injected.Uses++;
                        injected.LastUsedUnix = evt.Ts;
                        if (trigger.Length == 0)
                            trigger = injected.Trigger;
                    }
                    _pending[evt.Tactic] = new PendingInjection(
                        evt.Tactic,
                        trigger,
                        evt.Novel ?? "",
                        injectChapter
                    );
                    break;

                case "verdict":
                    if (string.IsNullOrEmpty(evt.Tactic) || evt.Chapter is null)
                        throw new InvalidDataException(
                            $"ledger.jsonl 第 {lineNo} 行 verdict 缺少 tactic/chapter。"
                        );
                    if (evt.Outcome is not ("pass" or "fail"))
                        throw new InvalidDataException(
                            $"ledger.jsonl 第 {lineNo} 行 verdict outcome 非法: {evt.Outcome}"
                        );
                    if (_tactics.TryGetValue(evt.Tactic, out var decided))
                    {
                        if (evt.Outcome == "pass")
                            decided.Wins++;
                        else
                        {
                            decided.Losses++;
                            if (!string.IsNullOrEmpty(evt.Novel))
                            {
                                var key = (evt.Novel, decided.Trigger);
                                _failCounts[key] = _failCounts.GetValueOrDefault(key) + 1;
                            }
                        }
                    }
                    // verdict 只可能产生于结算：同 id 的旧 pending 一并失效（重放幂等）。
                    _pending.Remove(evt.Tactic);
                    break;

                case "diagnose":
                    // 诊断记录不参与战术统计（由课程 CLI 直接读文件做失败簇）。
                    break;

                default:
                    throw new InvalidDataException(
                        $"ledger.jsonl 第 {lineNo} 行未知 kind: {evt.Kind}"
                    );
            }
        }
    }

    /// <summary>追加一行账本（UTF-8 无 BOM；目录不存在时自动创建）。</summary>
    private async Task AppendAsync(LedgerLine evt, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(evt, JsonHelper.JsonOptions);
        await AtomicFile.AppendLineAsync(_path, line, ct: ct);
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>账本行（扁平四态事件；null 字段不落盘，键为小写单词）。</summary>
    private sealed record LedgerLine
    {
        public string Kind { get; init; } = "";
        public long Ts { get; init; }
        public string? Novel { get; init; }
        public int? Chapter { get; init; }
        public string? Genre { get; init; }
        public string? Verdict { get; init; }
        public List<string>? Codes { get; init; }
        public List<string>? Dims { get; init; }
        public string? Tactic { get; init; }
        public string? Trigger { get; init; }
        public string? Prescription { get; init; }
        public string? Boundary { get; init; }
        public string? Outcome { get; init; }
    }
}
