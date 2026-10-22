namespace Tui.Services;

/// <summary>
/// 上下文 token 口径校准器：把字符估算投影到 API 实测（PromptTokens）口径。
///
/// 模型：measured ≈ offset + rate × estimate。
/// - offset：固定开销（系统提示 + 工具定义 + 小说上下文），字符估算完全数不到的部分；
/// - rate：chars/token 换算修正（CJK 1.5 chars/token 是近似，实测更准确）。
///
/// 对滚动窗口内的 (字符估算, API 实测) 采样点做最小二乘拟合，
/// 窗口滑出旧采样以自适应模型切换 / 工具集变化。
///
/// 三种口径：
/// - <see cref="Project"/>：完整 prompt（含固定开销）——compact 前检查用；
/// - <see cref="ProjectConversation"/>：会话上下文（0 起算，不含固定开销）——状态栏展示用；
/// - <see cref="ToConversationTokens"/>：把实测值扣除固定开销——LLM 调用进行中的实时展示用。
/// </summary>
public sealed class ContextTokenCalibrator
{
    /// <summary>滚动窗口大小：最多保留最近 N 个采样点。</summary>
    private const int MaxPoints = 12;

    /// <summary>斜率可用所需的最小估算跨度（tokens），防止采样点过于聚集时斜率不可靠。</summary>
    private const double MinSpanTokens = 500;

    /// <summary>滚动窗口内的采样点：(字符估算, API 实测)。</summary>
    private readonly List<(double Est, double Measured)> _points = [];

    /// <summary>拟合截距：固定开销（系统提示 + 工具定义 + 小说上下文）。</summary>
    private double _offset;

    /// <summary>拟合斜率：chars/token 换算修正系数。</summary>
    private double _rate;

    /// <summary>是否已学到有效校准参数。</summary>
    private bool _hasCalibration;

    /// <summary>是否已学到有效校准参数。</summary>
    public bool HasCalibration => _hasCalibration;

    /// <summary>
    /// 加入一个 (字符估算, API 实测) 采样点并重新拟合。
    /// </summary>
    /// <param name="est">字符估算的上下文 token 数。</param>
    /// <param name="measured">API 实测的 PromptTokens（完整 prompt 大小）。</param>
    public void AddSample(double est, double measured)
    {
        // 忽略无效采样点
        if (est <= 0 || measured <= 0)
            return;
        // 加入新采样点
        _points.Add((est, measured));
        // 滑动窗口：超出上限时移除最旧的采样点
        if (_points.Count > MaxPoints)
            _points.RemoveAt(0);
        // 重新拟合校准参数
        Refit();
    }

    /// <summary>
    /// 把字符估算投影到 API 实测口径（完整 prompt：含固定开销）。compact 前检查使用。
    /// 未校准时原样返回估算值。
    /// </summary>
    public long Project(long estimate)
    {
        if (!_hasCalibration)
            return estimate;
        var projected = _offset + _rate * estimate;
        return projected > 0 ? (long)projected : 0;
    }

    /// <summary>
    /// 会话上下文口径（0 起算，不含固定开销）：rate × 字符估算。
    /// 状态栏展示使用——与 pi/Claude Code 的上下文指示器一致：
    /// 新会话从 0 开始，随消息增长，compact 后重算。未校准时退回原始估算。
    /// </summary>
    public long ProjectConversation(long estimate)
    {
        if (!_hasCalibration)
            return estimate;
        var projected = _rate * estimate;
        return projected > 0 ? (long)projected : 0;
    }

    /// <summary>
    /// 把 API 实测（完整 prompt）换算到会话上下文口径：measured − 固定开销。
    /// LLM 调用进行中显示用。未校准时（无法扣除开销）原样返回实测。
    /// </summary>
    public long ToConversationTokens(long measured)
    {
        if (!_hasCalibration)
            return measured;
        var conversation = measured - _offset;
        return conversation > 0 ? (long)conversation : 0;
    }

    /// <summary>固定开销 tokens（系统提示 + 工具定义 + 小说上下文）。未校准时为 0。</summary>
    public long OverheadTokens => _hasCalibration ? (long)Math.Max(0, _offset) : 0;

    /// <summary>清空校准（模型 / 上下文窗口 / thinking 级别变更后调用，下一轮重新学习）。</summary>
    public void Reset()
    {
        _points.Clear();
        _hasCalibration = false;
        _offset = 0;
        _rate = 0;
    }

    /// <summary>最小二乘拟合 measured = offset + rate × est。</summary>
    private void Refit()
    {
        // 至少需要 2 个采样点才能拟合
        if (_points.Count < 2)
            return;
        // 检查估算值跨度，防止采样点过于聚集导致斜率不可靠
        var span = _points.Max(p => p.Est) - _points.Min(p => p.Est);
        if (span < MinSpanTokens)
            return; // 采样点过于聚集，斜率不可靠，保留旧校准

        // 累计最小二乘统计量
        var n = _points.Count;
        double sx = 0,
            sy = 0,
            sxx = 0,
            sxy = 0;
        foreach (var (e, m) in _points)
        {
            sx += e;
            sy += m;
            sxx += e * e;
            sxy += e * m;
        }
        // 分母接近零说明所有估算值几乎相同，无法拟合
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9)
            return;
        // 计算斜率 rate 和截距 offset
        var rate = (n * sxy - sx * sy) / denom;
        if (rate <= 0)
            return; // 退化数据（上下文不随消息增长），沿用旧校准

        _rate = rate;
        _offset = (sy - rate * sx) / n;
        _hasCalibration = true;
    }
}
