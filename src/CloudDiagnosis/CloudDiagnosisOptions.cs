using Core.Shared;
using Microsoft.Extensions.Configuration;

namespace CloudDiagnosis;

/// <summary>
/// 云端诊断配置（HARNESS-CLOUD §9 配置节 CloudDiagnosis:*）。
/// 不可变；由 IConfiguration 读取构造，默认值对齐文档 §3.4 成本护栏。
/// </summary>
public sealed class CloudDiagnosisOptions
{
    /// <summary>总开关。false 时 Hook 直接走本地信号路径。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 会话内深度诊断（fiction.story.check）预算上限。
    /// 默认 100（2026-10-03 调整：20 → 100）——本地预算 = 深度诊断的**本地额度上限**，目标支持
    /// ~50–100 章的深度判断（每章至多 1 次）；真正的硬限流在云端积分/额度
    /// （额度耗尽 → 402）。基础档（context.retrieve / scene.plan，各 1 积分）不占预算。
    /// </summary>
    public int DeepBudgetPerSession { get; init; } = 100;

    /// <summary>
    /// §16.5 方案 B：每小说持久预算池上限（跨会话累计）。首次使用该小说时初始化，
    /// 每消耗一次深度诊断 -1；配合方案 C 预算银行（会话未用结余转入银行）。
    /// 默认 100（2026-10-03 调整：20 → 100）——每本小说最多 ~100 次深度诊断的本地额度上限。
    /// </summary>
    public int DeepBudgetPerNovel { get; init; } = 100;

    /// <summary>单请求超时秒数（链接 CTS；60s CloudApiTimeout 仅作安全网）。</summary>
    public int RequestTimeoutSeconds { get; init; } = 15;

    /// <summary>人工裁决等待超时秒数（§5.2：超时留题 + 挂起轻信号）。</summary>
    public int DecisionTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// PASS 判定阈值（百分制；云端 overallScore 为 0-1，解析时 ×100）。
    /// 默认 62 与云端 `Cloud:ScenePlanPassScore`（0.62）对齐——临时人工标定（2026-10-03）：
    /// 普通样本实测带 0.54–0.66，70 使普通样本恒不落 Pass（只有命中充分样本可达）；
    /// 正式值随云端语料分位数标定同步调整。
    /// </summary>
    public double PassScoreThreshold { get; init; } = 62;

    /// <summary>M-1 阈值：连续自动推进章数（§7.1，默认 8）。</summary>
    public int MonotonyChapterThreshold { get; init; } = 8;

    /// <summary>M-2 平坦窗口大小（§7.1，默认 5）。</summary>
    public int MonotonyFlatWindow { get; init; } = 5;

    /// <summary>
    /// P3 基础档反馈升级阈值（百分制）。基础档 overallScore 低于此值且
    /// signals 中 high severity >= 2 时，即使 L2 未命中也强行升级到深度档。
    /// 默认 62 = 与云端 pass 线（0.62）对齐（2026-10-03 调整：50 → 62）：
    /// 普通样本实测带 0.54–0.66，50 使该路径实际不可触发；62 让「未达 pass 且 ≥2 条高危信号」
    /// 的章节真正获得一次深度诊断（本地额度上限见 DeepBudgetPerNovel）。
    /// </summary>
    public double UpgradeScoreThreshold { get; init; } = 62;

    /// <summary>
    /// P3 DECISION 每会话频率上限（§16.2）。
    /// </summary>
    public int DecisionMaxPerSession { get; init; } = 3;

    /// <summary>
    /// 层 1「采样门槛（本地）」：当前章**实质性字符**数（去空白/标点/符号后的字数）低于此值 → 本地跳过云端诊断。
    /// 默认 2 = 仅拦「没有实质内容」的退化输入（空/纯标点，实测 5 字纯标点实质字符为 0，其分数 0.6576
    /// 高于 219 字连贯正文 0.5938——引擎四个 quick 维度在「无信号」时各自返回先验基准分
    /// （爽感 20 / 代入感 70 / 去AI味 100 / 文笔质量 ≈80），几何均值恒 ≈0.61，与内容无关）。
    /// 短但非退化的样本由**能力层兜底**：云端 `Cloud:ScenePlanMinSubstantiveChars`（默认 200 实质字）
    /// 之下 scene.plan 返回 `insufficient_data`（不产分/不触发人工裁决，见 CHANGELOG 层 1/层 2）；
    /// 本值只需拦退化输入，调高即等于更早跳过云端（省额度，但失去基础档快照信号）。
    /// </summary>
    public int MinTargetContentChars { get; init; } = 2;

    /// <summary>§16.2 DECISION 前置过滤：结构化角色数低于此值 → 跳过人工裁决（默认 3）。</summary>
    public int DecisionMinCharacters { get; init; } = 3;

    /// <summary>§16.2 DECISION 前置过滤：章节序低于此值（小说早期）→ 跳过人工裁决（默认 5）。</summary>
    public int DecisionMinChapterOrder { get; init; } = 5;

    /// <summary>
    /// P3 章节重要性权重：后半段（Order > 总章数 x 此比值）L2 命中时
    /// 强制允许深度诊断，即使预算耗尽。（§16.5 方案 A）
    /// </summary>
    public double LateNovelOrderRatio { get; init; } = 0.7;

    /// <summary>用量对账章序间隔：每 N 章低频拉取一次服务端用量摘要（/v1/agent/usage）。</summary>
    public int UsageReconcileChapterInterval { get; init; } = 5;

    /// <summary>服务端余额预警阈值：剩余单位 ≤ 此值时一次性提示（基础档/深度档共用额度）。</summary>
    public int UsageLowRemainingThreshold { get; init; } = 100;

    /// <summary>
    /// 双速自进化（单账本折叠架构）总开关。默认 false：零战术注入、零账本写入、零行为变化。
    /// </summary>
    public bool EvolutionEnabled { get; init; }

    /// <summary>本体 harness 总开关（写作前介入 + 硬偏差阻断云端）。默认 true。</summary>
    public bool OntologyHarnessEnabled { get; init; } = true;

    /// <summary>每章最多注入的 harness 信号数（默认 4）。</summary>
    public int OntologyHarnessMaxSignals { get; init; } = 4;

    /// <summary>情节线停转判定窗口（默认 5 章）。</summary>
    public int OntologyHarnessStallWindow { get; init; } = 5;

    /// <summary>硬偏差时阻断本轮云端诊断（默认 true）。</summary>
    public bool OntologyHarnessGateCloud { get; init; } = true;

    /// <summary>客户端预取素材提示（knowledge.hint，按次计费）。默认 false。</summary>
    public bool MaterialHintsEnabled { get; init; }

    /// <summary>改用 context.retrieve 捎带 material-hints（与 MaterialHintsEnabled 互斥）。</summary>
    public bool MaterialHintsFromHarnessEnabled { get; init; }

    /// <summary>提示块字符预算（默认 800；服务端 clamp 200–4000）。</summary>
    public int MaterialHintMaxChars { get; init; } = 800;

    /// <summary>预取超时毫秒（默认 3000；超时静默跳过，不阻塞写作轮）。</summary>
    public int MaterialHintTimeoutMs { get; init; } = 3000;

    /// <summary>双速自进化：会话内战术注入块上限（默认 20）。</summary>
    public int MaxInjectPerSession { get; init; } = 20;

    /// <summary>从 IConfiguration 读取（键见 ConfigurationKeys.CloudDiagnosis*）。</summary>
    public static CloudDiagnosisOptions FromConfiguration(IConfiguration config) =>
        new()
        {
            Enabled =
                config[ConfigurationKeys.CloudDiagnosisEnabled] is not { } enabled
                || !bool.TryParse(enabled, out var on)
                || on,
            DeepBudgetPerSession = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDeepBudgetPerSession]
                    ?? config[ConfigurationKeys.CloudDiagnosisLegacyBudgetPerSession],
                100
            ),
            DeepBudgetPerNovel = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDeepBudgetPerNovel]
                    ?? config[ConfigurationKeys.CloudDiagnosisLegacyBudgetPerNovel],
                100
            ),
            RequestTimeoutSeconds = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisRequestTimeoutSeconds],
                15
            ),
            DecisionTimeoutSeconds = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDecisionTimeoutSeconds],
                120
            ),
            PassScoreThreshold = ParseDouble(
                config[ConfigurationKeys.CloudDiagnosisPassScoreThreshold],
                62
            ),
            MonotonyChapterThreshold = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisMonotonyChapterThreshold],
                8
            ),
            MonotonyFlatWindow = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisMonotonyFlatWindow],
                5
            ),
            UpgradeScoreThreshold = ParseDouble(
                config[ConfigurationKeys.CloudDiagnosisUpgradeScoreThreshold],
                62
            ),
            DecisionMaxPerSession = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDecisionMaxPerSession],
                3
            ),
            DecisionMinCharacters = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDecisionMinCharacters],
                3
            ),
            MinTargetContentChars = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisMinTargetContentChars],
                2
            ),
            DecisionMinChapterOrder = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisDecisionMinChapterOrder],
                5
            ),
            LateNovelOrderRatio = ParseDouble(
                config[ConfigurationKeys.CloudDiagnosisLateNovelOrderRatio],
                0.7
            ),
            UsageReconcileChapterInterval = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisUsageReconcileChapterInterval],
                5
            ),
            UsageLowRemainingThreshold = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisUsageLowRemainingThreshold],
                100
            ),
            EvolutionEnabled =
                bool.TryParse(
                    config[ConfigurationKeys.CloudDiagnosisEvolutionEnabled],
                    out var evolution
                ) && evolution,
            OntologyHarnessEnabled =
                config[ConfigurationKeys.CloudDiagnosisOntologyHarnessEnabled] is not { } harnessOn
                || !bool.TryParse(harnessOn, out var harnessEnabled)
                || harnessEnabled,
            OntologyHarnessMaxSignals = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisOntologyHarnessMaxSignals],
                4
            ),
            OntologyHarnessStallWindow = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisOntologyHarnessStallWindow],
                5
            ),
            OntologyHarnessGateCloud =
                config[ConfigurationKeys.CloudDiagnosisOntologyHarnessGateCloud] is not { } gate
                || !bool.TryParse(gate, out var gateCloud)
                || gateCloud,
            MaterialHintsEnabled =
                bool.TryParse(
                    config[ConfigurationKeys.CloudDiagnosisMaterialHintsEnabled],
                    out var materialHints
                ) && materialHints,
            MaterialHintsFromHarnessEnabled =
                bool.TryParse(
                    config[ConfigurationKeys.CloudDiagnosisMaterialHintsFromHarnessEnabled],
                    out var harnessHints
                ) && harnessHints,
            MaterialHintMaxChars = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisMaterialHintMaxChars],
                800
            ),
            MaterialHintTimeoutMs = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisMaterialHintTimeoutMs],
                3000
            ),
            MaxInjectPerSession = ParseInt(
                config[ConfigurationKeys.CloudDiagnosisEvolutionMaxInjectPerSession],
                20
            ),
        };

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) ? value : fallback;

    /// <summary>double 配置解析：拒绝 NaN/Infinity（NaN 阈值会使所有比较恒 false）。</summary>
    private static double ParseDouble(string? raw, double fallback) =>
        double.TryParse(raw, out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value
            : fallback;
}
