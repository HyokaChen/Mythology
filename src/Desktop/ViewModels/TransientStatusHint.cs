namespace Desktop.ViewModels;

/// <summary>
/// 状态栏一次性临时提示的状态机。
///
/// 只负责三件事：记录被覆盖的底层文案、保证同一忙碌周期只提示一次、用版本号拒绝过期计时器。
/// 计时与属性写入由宿主负责——这样计时器晚到、用户提前点击、忙碌状态结束三种路径都不会
/// 把旧诗词写回状态栏。
/// </summary>
internal sealed class TransientStatusHint
{
    private Snapshot? _restore;
    private long _version;

    /// <summary>当前是否有临时提示覆盖在状态栏上。</summary>
    public bool IsVisible => _restore is not null;

    /// <summary>本忙碌周期是否已经提示过（提示关闭后仍为 true）。</summary>
    public bool HasBeenShown { get; private set; }

    /// <summary>
    /// 尝试开始一次提示。同一忙碌周期内只有第一次会成功；返回的 <paramref name="version"/>
    /// 供计时器到点时校验，避免旧计时器覆盖后续状态。
    /// </summary>
    public bool TryShow(string baseText, string? baseTooltip, out long version)
    {
        version = _version;
        if (HasBeenShown)
            return false;

        HasBeenShown = true;
        _restore = new Snapshot(baseText, baseTooltip);
        version = ++_version;
        return true;
    }

    /// <summary>计时器到点：只有版本仍匹配且提示仍在显示时才还原底层文案。</summary>
    public bool TryRestore(long version, out string text, out string? tooltip)
    {
        if (version != _version || _restore is not { } snapshot)
        {
            text = "";
            tooltip = null;
            return false;
        }

        text = snapshot.Text;
        tooltip = snapshot.Tooltip;
        ClearVisible();
        return true;
    }

    /// <summary>用户点击提示：提前关闭并返回被覆盖的底层文案。</summary>
    public bool Dismiss(out string text, out string? tooltip)
    {
        if (_restore is not { } snapshot)
        {
            text = "";
            tooltip = null;
            return false;
        }

        text = snapshot.Text;
        tooltip = snapshot.Tooltip;
        ClearVisible();
        return true;
    }

    /// <summary>被其它正式状态覆盖：丢弃还原点，但保留「本周期已提示」标记。</summary>
    public void Cancel()
    {
        if (_restore is not null)
            ClearVisible();
    }

    /// <summary>新的忙碌周期：清空提示并允许再次提示。</summary>
    public void Reset()
    {
        ClearVisible();
        HasBeenShown = false;
    }

    private void ClearVisible()
    {
        _restore = null;
        _version++;
    }

    private sealed record Snapshot(string Text, string? Tooltip);
}
