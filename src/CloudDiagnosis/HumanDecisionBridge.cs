using System.Globalization;
using System.Text.Json;
using Core.Cloud;
using Core.Interactions;
using Core.Shared;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>单次 DECISION 记录（HARNESS-CLOUD §16.2 DecisionHistory）。</summary>
public sealed record DecisionHistoryEntry(
    string NovelId,
    DateTimeOffset At,
    string Question,
    string Answer,
    double ScenePlanScore,
    bool WasSkipped,
    DecisionAnswerKind AnswerKind = DecisionAnswerKind.Unknown
)
{
    /// <summary>
    /// U-2：结构化分类——显式 code 优先；缺省（本次改动前落库的行、直接构造的测试行）
    /// 回退按 Answer 文本归类，存量统计口径不变。
    /// </summary>
    public DecisionAnswerKind EffectiveAnswerKind =>
        AnswerKind != DecisionAnswerKind.Unknown
            ? AnswerKind
            : HumanDecisionBridge.ClassifyAnswerLegacy(Answer);
}

/// <summary>决策回答的结构化分类（U-2：§16.2 模式统计不再依赖 Answer 文本子串匹配）。</summary>
public enum DecisionAnswerKind
{
    /// <summary>未知（存量行/未显式给出）→ 由 Answer 文本回退归类。</summary>
    Unknown,

    /// <summary>采纳建议（默认模板首项「按建议继续自动迭代」）。</summary>
    FollowSuggestion,

    /// <summary>自行指定方向（聚焦维度选项、自定义方向或自由文本）。</summary>
    OwnDirection,
}

/// <summary>决策模式（§16.2 用户决策模式统计：控制型 vs 信任型）。</summary>
public enum DecisionProfileKind
{
    /// <summary>数据不足，无法判定。</summary>
    Unknown,

    /// <summary>信任型：多数情况下选择「按建议继续」。</summary>
    Trusting,

    /// <summary>控制型：多数情况下自行指定方向 / 自定义选项。</summary>
    Controlling,
}

/// <summary>
/// 人工裁决回路（HARNESS-CLOUD §5 组件 C）。
/// DECISION（scene.plan 的 human_decision_required）→ QuestionDef → InteractionHub
/// → WaitForQuestionAnswerAsync（链接超时 CTS）→ 答案回注文本 / 超时挂起文本。
/// 首次对低阈值裁决选择“按建议继续自动迭代”后，本作品在当前进程内获得持续授权；
/// 后续低阈值裁决直接自动继续，不再创建问题（授权句复用既有 [人工裁决] 信号）。
/// UI 弹窗由宿主从 <see cref="InteractionHub.Changed"/> claim/render；本组件不持有任何 UI 类型。
/// P2/P3：DecisionHistory 持久化（SQLite vectors.db decision_history 表）+ 决策模式统计。
/// </summary>
public sealed class HumanDecisionBridge
{
    /// <summary>SQLite 持久化路径（null = 仅内存，测试/未配置）。</summary>
    private readonly string? _dbPath;

    private readonly InteractionHub _hub;
    private readonly int _decisionTimeoutSeconds;
    private readonly bool _skipAnswerInjection;
    private readonly ILogger? _logger;

    private readonly List<DecisionHistoryEntry> _decisions;

    /// <summary>已对低阈值裁决授权“按建议继续自动迭代”的作品；进程会话级。</summary>
    private readonly HashSet<string> _authorizedNovels = new(StringComparer.Ordinal);

    /// <summary>
    /// 已由作者回答过的章节裁决（novelId + 章节序 + 问题）；用于阻止同一章在连续
    /// 批量改写中反复弹同一张决策卡。只在进程内有效，跨进程由 DecisionHistory 统计兜底。
    /// </summary>
    private readonly Dictionary<string, string> _answeredDecisions = new(StringComparer.Ordinal);
    private readonly object _decisionGate = new();

    /// <summary>默认模板兜底首项（§5.2）：语义 = 采纳建议（U-2 结构化分类依据）。</summary>
    private const string ContinueOption = "按建议继续自动迭代";

    /// <summary>默认模板兜底次项（§5.2）：语义 = 自行指定方向。</summary>
    private const string OwnDirectionOption = "我来指定方向";

    /// <summary>首次授权后的持续授权句；追加到既有 [人工裁决] 文本里，不新增信号类型。</summary>
    private const string AuthorizationSentence =
        "本作品后续低风险、可回退的场景规划按建议继续自动迭代，不再逐次询问。"
        + "仅覆盖可回退的写作推进；删除/覆盖/改结局等不可逆动作仍需确认。";

    /// <summary>TUI 走答案追加链路时，bridge 只补这一条带 [人工裁决] 前缀的授权块。</summary>
    private const string AuthorizationBlock = "[人工裁决] " + AuthorizationSentence;

    /// <summary>
    /// U-2 存量回退：旧行（无 answer_code）按 Answer 文本一次性归类；
    /// 新记录一律由 <see cref="DecideAsync"/> 按选项身份给出显式 code。
    /// </summary>
    internal static DecisionAnswerKind ClassifyAnswerLegacy(string answer) =>
        answer.Contains(ContinueOption, StringComparison.Ordinal)
            ? DecisionAnswerKind.FollowSuggestion
            : DecisionAnswerKind.OwnDirection;

    /// <summary>
    /// P3 DECISION 前置过滤（§16.2）：判断当前 DECISION 是否应被跳过。
    /// 返回跳过原因（空 = 不过滤，正常走裁决流程）。
    /// 过滤条件：角色完整性不足（结构化角色数 &lt; 3） / 小说早期（Order &lt; 5） /
    /// 历史稳定性（连续 3 次 score &gt;= 0.6 且当前 score 处于 [下沿, warning 阈值) 临界区）。
    /// 频率上限由调度器 TryConsumeDecisionSlot() 处理（会话级）。
    /// </summary>
    public static string? ShouldSkipDecision(
        DiagnosisReport outcome,
        int characterCount,
        int currentOrder,
        IReadOnlyList<DecisionHistoryEntry>? history = null,
        int minCharacters = 3,
        int minChapterOrder = 5
    )
    {
        // 条件1：角色完整性不足（结构化角色数 < DecisionMinCharacters）
        if (characterCount < minCharacters)
            return $"角色信息不足（目前 {characterCount} 个角色），场景规划的判断可能不准，先按建议继续";

        // 条件2：小说早期（Order < DecisionMinChapterOrder）
        if (currentOrder < minChapterOrder)
            return $"目前仍是小说早期（前 {minChapterOrder} 章），设定还没充分展开，先按建议继续";

        // 条件3：历史稳定性——连续 3 次 scene.plan score >= 0.6 且当前 score 在 [下沿, 0.55) 临界区
        // → 系统更信任自动决策，跳过 DECISION（临界区上沿 = 云端 warning 阈值 0.55；
        //    下沿从 0.55 降到 0.4 才触发）。
        // 若用户为「信任型」（多数选按建议继续），临界区下沿进一步放宽到 0.35。
        if (history is { Count: >= 3 })
        {
            var recent = history.TakeLast(3).ToList();
            // 单位归一：OverallScore 为百分制（ParseScenePlan ×100），历史同存百分制；
            // 临界区阈值（0.6 / 0.4 / 0.55）为 0-1 云端原始分语义——统一 ÷100 再比较，
            // 否则百分制下历史 ≥0.6 恒真、当前 <0.55 恒假，稳定性过滤成为死代码。
            var trusted =
                recent.All(h => h.ScenePlanScore / 100.0 >= 0.6) && !recent[^1].WasSkipped;
            if (trusted)
            {
                var lowerBound =
                    ComputeProfile(recent) == DecisionProfileKind.Trusting ? 0.35 : 0.4;
                var score = (outcome.Score ?? 0) / 100.0;
                if (score >= lowerBound && score < 0.55)
                    return "前几次场景规划一直稳定，本章也接近可继续的范围，先按建议继续。";
            }
        }

        // 频率上限由调度器的 TryConsumeDecisionSlot() 处理（会话级）。
        return null;
    }

    /// <summary>
    /// 已授权作品的低阈值裁决：返回自动继续文本；null = 仍需人工问题。
    /// 自动继续不是人工决策，不写 decision_history，避免污染 DecisionProfile。
    /// </summary>
    public string? TryAutoContinue(DiagnosisReport outcome, string? novelId = null)
    {
        if (
            string.IsNullOrEmpty(novelId)
            || outcome.Action != "ask-author"
            || !_authorizedNovels.Contains(novelId)
        )
            return null;

        return $"{SignalBuilder.Prefix} 场景规划建议继续（沿用上次 [人工裁决] 的授权），"
            + $"已按建议继续自动迭代；{outcome.Summary}。不要停下来等待用户确认。";
    }

    /// <summary>
    /// §16.2 决策模式统计：根据最近历史回答模式判定用户是「信任型」还是「控制型」。
    /// 信任型 = 多数选择「按建议继续」；控制型 = 多数自行指定方向 / 自定义选项。
    /// </summary>
    public static DecisionProfileKind ComputeProfile(IReadOnlyList<DecisionHistoryEntry> history)
    {
        if (history.Count == 0)
            return DecisionProfileKind.Unknown;

        var trusting = history.Count(h =>
            h.EffectiveAnswerKind == DecisionAnswerKind.FollowSuggestion
        );
        var controlling = history.Count - trusting;
        if (trusting == controlling)
            return DecisionProfileKind.Unknown;
        return trusting > controlling
            ? DecisionProfileKind.Trusting
            : DecisionProfileKind.Controlling;
    }

    public HumanDecisionBridge(
        InteractionHub hub,
        int decisionTimeoutSeconds = 120,
        bool skipAnswerInjection = false,
        string? persistenceDbPath = null,
        ILogger? logger = null
    )
    {
        _hub = hub;
        _decisionTimeoutSeconds = decisionTimeoutSeconds;
        _skipAnswerInjection = skipAnswerInjection;
        _dbPath = persistenceDbPath;
        _logger = logger;
        _decisions = LoadFromDb();
    }

    /// <summary>当前会话加载后的 DECISION 历史（§16.2 DecisionHistory；含持久化回源）。</summary>
    public IReadOnlyList<DecisionHistoryEntry> DecisionHistory => _decisions;

    /// <summary>当前用户的决策模式统计（§16.2：控制型 vs 信任型）。</summary>
    public DecisionProfileKind DecisionProfile => ComputeProfile(_decisions);

    /// <summary>记录一次 DECISION 回答（内存 + 持久化）。</summary>
    public void RecordDecision(
        string novelId,
        string question,
        string answer,
        double scenePlanScore,
        bool wasSkipped = false,
        DecisionAnswerKind answerKind = DecisionAnswerKind.Unknown
    )
    {
        var entry = new DecisionHistoryEntry(
            novelId,
            DateTimeOffset.UtcNow,
            question,
            answer,
            scenePlanScore,
            wasSkipped,
            answerKind
        );
        _decisions.Add(entry);
        SaveToDb(entry);
    }

    /// <summary>
    /// 同一章、同一问题已被作者回答时返回继续文本；调用方应在消费决策名额前先用它，
    /// 避免连续段落改写把同一张决策卡再弹一次。章节序变化后自然失效。
    /// </summary>
    public string? TryGetRepeatedDecision(
        string? novelId,
        int chapterOrder,
        DiagnosisReport outcome
    )
    {
        if (string.IsNullOrEmpty(novelId))
            return null;

        var key = DecisionKey(novelId, chapterOrder, BuildQuestion(outcome).Question);
        string? answer;
        lock (_decisionGate)
            _answeredDecisions.TryGetValue(key, out answer);
        if (answer is null)
            return null;

        return $"{SignalBuilder.Prefix} 本章已按上次人工裁决“{OneLine(answer)}”继续处理，"
            + "不再重复询问；请继续按该方向修改，不要停下来等待确认。";
    }

    /// <summary>
    /// 执行人工裁决全流程。返回 InjectQueue 注入文本：
    /// 收到回答 → 「[人工裁决] …」；超时未答 → 挂起轻信号（问题留存 store）。
    /// 用户取消（ct）时 OperationCanceledException 向上抛出（§12 红线 5）。
    /// </summary>
    public async Task<string> DecideAsync(
        DiagnosisReport outcome,
        CancellationToken ct,
        string? novelId = null,
        int chapterOrder = 0
    )
    {
        ct.ThrowIfCancellationRequested();
        if (TryAutoContinue(outcome, novelId) is { } autoVerdict)
            return autoVerdict;

        if (TryGetRepeatedDecision(novelId, chapterOrder, outcome) is { } repeated)
            return repeated;

        var question = BuildQuestion(outcome);
        var created = _hub.CreateQuestionSet(_hub.CurrentContext, [question]);
        var pending = created.Questions!.Items[0].Question;

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _decisionTimeoutSeconds))
        );
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        string[] answers;
        try
        {
            answers = await _hub.WaitForQuestionAnswerAsync(created.Id, pending.Id, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _hub.Cancel(created.Id, "decision-cancelled", cancelWaiters: true);
            throw;
        }
        catch (OperationCanceledException)
        {
            // 超时：不终结问题，保留在 store 中供之后人工回答；本次注入保守路径轻信号（§5.2）
            return $"{SignalBuilder.Prefix} 存在待决问题 #{pending.Id}（{pending.Header}），"
                + "暂按保守路径继续：与该决策相关的剧情推进仅做铺垫不做定局。";
        }

        if (answers.Length == 1 && answers[0] == AskUserReplyFormatter.CancelledAnswer)
        {
            return $"{SignalBuilder.Prefix} 待决问题 #{pending.Id}（{pending.Header}）已被取消，"
                + "暂按保守路径继续：与该决策相关的剧情推进仅做铺垫不做定局。";
        }

        // 记录决策历史（§16.2 DecisionHistory；持久化 + U-2 结构化分类）
        var answerKind =
            answers.Length == 1
            && string.Equals(answers[0], question.Options[0], StringComparison.Ordinal)
                ? DecisionAnswerKind.FollowSuggestion
                : DecisionAnswerKind.OwnDirection;
        var authorizedNow =
            answerKind == DecisionAnswerKind.FollowSuggestion
            && outcome.Action == "ask-author"
            && !string.IsNullOrEmpty(novelId)
            && _authorizedNovels.Add(novelId);
        var answerText = string.Join("、", answers);
        if (!string.IsNullOrEmpty(novelId))
        {
            lock (_decisionGate)
                _answeredDecisions[DecisionKey(novelId, chapterOrder, question.Question)] =
                    answerText;
        }
        RecordDecision(
            novelId ?? "",
            question.Question,
            answerText,
            outcome.Score ?? 0,
            answerKind: answerKind
        );

        // 双通道去重（P2）：宿主复用 ask_user 渲染链路时会自动把答案追加为 user 消息，
        // 此时 bridge 不再重复注入答案；首次授权仍需补一条授权块，否则模型看不到持续授权。
        if (_skipAnswerInjection)
            return authorizedNow ? AuthorizationBlock : "";

        var verdict =
            $"[人工裁决] 就\"{question.Question}\"你选择了\"{string.Join("、", answers)}\"。"
            + $"结论：{outcome.Summary}。"
            + "请严格按此执行，不得擅自更改方向。";
        return authorizedNow ? verdict + "\n" + AuthorizationSentence : verdict;
    }

    /// <summary>组装 QuestionDef：问题与选项直接使用服务端报告，缺省时回退保守模板。</summary>
    private static QuestionDef BuildQuestion(DiagnosisReport outcome)
    {
        var questionText = string.IsNullOrWhiteSpace(outcome.DecisionQuestion)
            ? "云端诊断建议就当前场景规划征求你的方向"
            : outcome.DecisionQuestion;
        string[] options = outcome.DecisionOptions is { Count: > 0 } provided
            ? [.. provided]
            : [ContinueOption, OwnDirectionOption];

        return new QuestionDef
        {
            Header = "创作决策",
            Question = questionText,
            Options = options,
            MultiSelect = false,
        };
    }

    /// <summary>同一章同一问题的去重键；章节序变化后旧回答自然失效。</summary>
    private static string DecisionKey(string novelId, int chapterOrder, string question) =>
        string.Join(
            '\u001f',
            novelId,
            chapterOrder.ToString(CultureInfo.InvariantCulture),
            question
        );

    /// <summary>把答案压成单行，避免自定义答案里的换行污染注入文本。</summary>
    private static string OneLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── DecisionHistory 持久化（vectors.db decision_history 表；读写失败告警降级、不动已有表）──

    private List<DecisionHistoryEntry> LoadFromDb()
    {
        if (_dbPath is null)
            return [];

        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT novel_id, at_utc, question, answer, scene_plan_score, was_skipped, answer_code
                FROM decision_history ORDER BY at_utc
                """;
            var list = new List<DecisionHistoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(
                    new DecisionHistoryEntry(
                        reader.GetString(0),
                        DateTimeOffset.Parse(
                            reader.GetString(1),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind
                        ),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetDouble(4),
                        reader.GetInt64(5) != 0,
                        ParseAnswerKind(reader.GetString(6))
                    )
                );
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"决策历史回源失败：{ex.Message}");
            return [];
        }
    }

    private void SaveToDb(DecisionHistoryEntry entry)
    {
        if (_dbPath is null)
            return;
        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO decision_history (novel_id, at_utc, question, answer, scene_plan_score, was_skipped, answer_code)
                VALUES ($novel, $utc, $question, $answer, $score, $skipped, $code)
                """;
            cmd.Parameters.AddWithValue("$novel", entry.NovelId);
            cmd.Parameters.AddWithValue("$utc", entry.At.ToString("O"));
            cmd.Parameters.AddWithValue("$question", entry.Question);
            cmd.Parameters.AddWithValue("$answer", entry.Answer);
            cmd.Parameters.AddWithValue("$score", entry.ScenePlanScore);
            cmd.Parameters.AddWithValue("$skipped", entry.WasSkipped ? 1 : 0);
            cmd.Parameters.AddWithValue("$code", entry.AnswerKind.ToString());
            _ = cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"决策历史持久化失败：{ex.Message}");
        }
    }

    private static void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS decision_history (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                novel_id TEXT NOT NULL,
                at_utc TEXT NOT NULL,
                question TEXT NOT NULL,
                answer TEXT NOT NULL,
                scene_plan_score REAL NOT NULL,
                was_skipped INTEGER NOT NULL DEFAULT 0,
                answer_code TEXT NOT NULL DEFAULT ''
            )
            """;
        _ = cmd.ExecuteNonQuery();
        // U-2：存量库补列（旧表无 answer_code；空值回退按 Answer 文本归类）
        EnsureColumn(conn, "decision_history", "answer_code", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>旧库补列：列已存在则跳过（SQLite ADD COLUMN，只做一次结构升级）。</summary>
    private static void EnsureColumn(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        string table,
        string column,
        string definition
    )
    {
        var exists = false;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists)
            return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        _ = alter.ExecuteNonQuery();
    }

    /// <summary>解析持久化的 answer_code（空/未知值 → Unknown → 由 Answer 文本回退）。</summary>
    private static DecisionAnswerKind ParseAnswerKind(string stored) =>
        Enum.TryParse<DecisionAnswerKind>(stored, ignoreCase: true, out var kind)
            ? kind
            : DecisionAnswerKind.Unknown;

    private void LogDegraded(string reason) =>
        _logger?.LogWarning("[云端诊断] 决策历史存储失败：{Reason}", reason);
}
