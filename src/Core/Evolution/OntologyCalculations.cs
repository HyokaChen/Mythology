namespace Core.Evolution;

/// <summary>
/// 本体计算（ont_calculation）的稳定标识：素材 delta 用来说明「这条结论由哪条推导产生」。
/// 计算本身是派生视图，不写动作日志。
/// </summary>
public static class OntologyCalculations
{
    /// <summary>首次 Present 出场。</summary>
    public const string FirstAppearance = "first-appearance";

    /// <summary>出场区间内的状态变化。</summary>
    public const string StateDelta = "state-delta";

    /// <summary>情节线揭示/回收。</summary>
    public const string PlotlineReveal = "plotline-reveal";

    /// <summary>场景计划变化。</summary>
    public const string PlannedDelta = "planned-delta";

    /// <summary>世界规则触发。</summary>
    public const string RuleTrigger = "rule-trigger";

    /// <summary>设定变更日志（编辑器/工具改实体、关系、规则）。</summary>
    public const string SettingsChange = "settings-change";
}
