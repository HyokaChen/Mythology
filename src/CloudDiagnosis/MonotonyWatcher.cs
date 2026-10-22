namespace CloudDiagnosis;

/// <summary>
/// 单调检测与灵感供给（HARNESS-CLOUD §7 组件 E）。
/// M-1 连续自动推进轮数 + M-2 平坦窗口双信号；到期后生成低强度灵感信号
/// （[风格观察] 锚点），每会话硬限 ≤2 次。全部为廉价本地计数，零云端调用。
/// </summary>
public sealed class MonotonyWatcher(CloudDiagnosisOptions options)
{
    /// <summary>频率红线（§7.2）：每会话最多 2 次灵感注入。</summary>
    public const int MaxInspirationsPerSession = 2;

    private readonly object _lock = new();

    /// <summary>M-1：自上次人工介入/灵感以来连续自动推进章数。</summary>
    private int _autoAdvances;

    /// <summary>M-2：最近 N 章平坦标记滑动窗口（true = hookType 缺失且信息差为 0）。</summary>
    private readonly Queue<bool> _flatWindow = new();

    private int _emitted;

    /// <summary>
    /// Hook 每次写入类工具成功后记录本章状态：M-1 计数推进 + 平坦窗口入队。
    /// </summary>
    public void RecordChapter(bool isFlat)
    {
        lock (_lock)
        {
            _autoAdvances++;
            _flatWindow.Enqueue(isFlat);
            var capacity = Math.Max(1, options.MonotonyFlatWindow);
            while (_flatWindow.Count > capacity)
                _flatWindow.Dequeue();
        }
    }

    /// <summary>M-3：最近 N 次云端「爽感」维度得分（P2 云端增强；null 项为该章无数据）。</summary>
    private readonly Queue<double?> _satisfactionScores = new();

    /// <summary>
    /// Hook 在云端成功轮喂入本章「爽感」维度分（context.retrieve dimensionScores；
    /// 无该维度时传 null 占位，保持窗口对齐章节序列）。
    /// </summary>
    public void RecordSatisfactionScore(double? score)
    {
        lock (_lock)
        {
            _satisfactionScores.Enqueue(score);
            var capacity = Math.Max(1, options.MonotonyFlatWindow);
            while (_satisfactionScores.Count > capacity)
                _satisfactionScores.Dequeue();
        }
    }

    /// <summary>
    /// M-3 判定：窗口内非空分数 ≥3 个且严格单调递减（CHANGE_03 RSI/MACD 衰减语义的
    /// 离散近似——连续走低即爽感动能流失）。全空/不足返回 false。
    /// </summary>
    private bool IsSatisfactionMonotonicDeclining()
    {
        lock (_lock)
        {
            var scores = _satisfactionScores.Where(s => s.HasValue).Select(s => s!.Value).ToList();
            if (scores.Count < 3)
                return false;
            return scores.Zip(scores.Skip(1)).All(pair => pair.Second < pair.First);
        }
    }

    /// <summary>双信号是否到期（§7.1：M-1 ≥ 阈值 或 平坦窗口满 或 M-3 爽感单调递减）且配额未耗尽。</summary>
    public bool IsInspirationDue()
    {
        lock (_lock)
        {
            if (_emitted >= MaxInspirationsPerSession)
                return false;
            if (_autoAdvances >= options.MonotonyChapterThreshold)
                return true;
            if (
                _flatWindow.Count == Math.Max(1, options.MonotonyFlatWindow)
                && !_flatWindow.Contains(false)
            )
                return true;
            return IsSatisfactionMonotonicDeclining();
        }
    }

    /// <summary>人工介入（DECISION 回答 / ask_user / 灵感注入）重置 M-1（§7.1）。</summary>
    public void NotifyIntervention()
    {
        lock (_lock)
        {
            _autoAdvances = 0;
            _flatWindow.Clear();
        }
    }

    /// <summary>当前计数快照（灵感文本证据段使用；调用方在 TryBegin 前取值）。</summary>
    public (int AutoAdvances, int FlatCount) SnapshotCounters()
    {
        lock (_lock)
        {
            return (_autoAdvances, _flatWindow.Count(f => f));
        }
    }

    /// <summary>
    /// 占用一次灵感配额并重置计数（到期检查 + 占用为原子语义）。
    /// 返回 false = 未到期或配额耗尽，调用方不注入。
    /// </summary>
    public bool TryBeginInspiration()
    {
        lock (_lock)
        {
            if (_emitted >= MaxInspirationsPerSession || !IsInspirationDue())
                return false;
            _emitted++;
            _autoAdvances = 0;
            _flatWindow.Clear();
            return true;
        }
    }

    /// <summary>
    /// 组装 [风格观察] 灵感文本（§7.2 模板）。候选来自本地数据：
    /// 缺席配角（最近 3 章）与最沉睡暗线；无候选时省略对应方向。
    /// </summary>
    public string BuildInspirationMessage(
        int autoAdvances,
        int flatCount,
        IReadOnlyList<string> absentSupporting,
        string? dormantPlotline
    )
    {
        var text =
            $"[风格观察] 近{options.MonotonyChapterThreshold}章收敛度偏高"
            + $"（证据：M-1 计数={autoAdvances}，平坦窗口={flatCount}/{options.MonotonyFlatWindow}）。"
            + "可选方向：";

        var directions = new List<string>();
        if (absentSupporting.Count > 0)
            directions.Add($"① 引入配角支线（候选：{string.Join("、", absentSupporting)}）");
        if (!string.IsNullOrEmpty(dormantPlotline))
            directions.Add($"③ 激活沉睡暗线（候选：{dormantPlotline}）");
        if (directions.Count == 0)
            directions.Add("② 切换场景类型或节奏（当前连续同质化）");
        text += string.Join(" ", directions);
        text += " 如需保持现状请忽略本条。";
        return text;
    }
}
