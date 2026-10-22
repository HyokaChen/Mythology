namespace Core.Shared;

/// <summary>
/// 配置键集中定义，避免在业务代码中散落魔法字符串。
/// 键路径与 appsettings.json / 环境变量覆盖保持一致（":" 为层级分隔）。
/// </summary>
public static class ConfigurationKeys
{
    // ── 存储 ──
    /// <summary>小说根目录。</summary>
    public const string NovelsPath = "Novels:Path";

    /// <summary>方案 2B 门面读取模式：direct | incremental | shadow。</summary>
    public const string StoreMode = "Data:StoreMode";

    // ── Agent ──
    public const string AgentSystemPrompt = "Agent:SystemPrompt";
    public const string AgentThinkingLevel = "Agent:ThinkingLevel";
    public const string AgentToolSearchDeferredPlugins = "Agent:ToolSearchDeferredPlugins";
    public const string AgentToolSearchMode = "Agent:ToolSearchMode";
    public const string AgentToolSearchRateLimit = "Agent:ToolSearchRateLimit";

    // ── LLM / Embedding ──
    public const string LlmProvider = "LLM:Provider";
    public const string LlmModel = "LLM:Model";
    public const string LlmApiKey = "LLM:ApiKey";
    public const string EmbeddingModel = "Embedding:Model";

    // ── 云端诊断（HARNESS-CLOUD §9 配置节）──
    public const string CloudDiagnosisEnabled = "CloudDiagnosis:Enabled";
    public const string CloudDiagnosisDeepBudgetPerSession = "CloudDiagnosis:DeepBudgetPerSession";
    public const string CloudDiagnosisDeepBudgetPerNovel = "CloudDiagnosis:DeepBudgetPerNovel";

    /// <summary>旧键（2026-10-03 命名）：仅用于升级回退，新键优先。</summary>
    public const string CloudDiagnosisLegacyBudgetPerSession = "CloudDiagnosis:BudgetPerSession";
    public const string CloudDiagnosisLegacyBudgetPerNovel = "CloudDiagnosis:BudgetPerNovel";
    public const string CloudDiagnosisDecisionTimeoutSeconds =
        "CloudDiagnosis:DecisionTimeoutSeconds";
    public const string CloudDiagnosisRequestTimeoutSeconds =
        "CloudDiagnosis:RequestTimeoutSeconds";
    public const string CloudDiagnosisPassScoreThreshold = "CloudDiagnosis:PassScoreThreshold";
    public const string CloudDiagnosisMonotonyChapterThreshold =
        "CloudDiagnosis:MonotonyChapterThreshold";
    public const string CloudDiagnosisMonotonyFlatWindow = "CloudDiagnosis:MonotonyFlatWindow";
    public const string CloudDiagnosisUpgradeScoreThreshold =
        "CloudDiagnosis:UpgradeScoreThreshold";
    public const string CloudDiagnosisDecisionMaxPerSession =
        "CloudDiagnosis:DecisionMaxPerSession";
    public const string CloudDiagnosisDecisionMinCharacters =
        "CloudDiagnosis:DecisionMinCharacters";
    public const string CloudDiagnosisMinTargetContentChars =
        "CloudDiagnosis:MinTargetContentChars";
    public const string CloudDiagnosisDecisionMinChapterOrder =
        "CloudDiagnosis:DecisionMinChapterOrder";
    public const string CloudDiagnosisLateNovelOrderRatio = "CloudDiagnosis:LateNovelOrderRatio";
    public const string CloudDiagnosisUsageReconcileChapterInterval =
        "CloudDiagnosis:UsageReconcileChapterInterval";
    public const string CloudDiagnosisUsageLowRemainingThreshold =
        "CloudDiagnosis:UsageLowRemainingThreshold";

    // ── 双速自进化（单账本折叠架构）──
    /// <summary>战术注入/账本总开关（默认 false = 完全关闭）。</summary>
    public const string CloudDiagnosisEvolutionEnabled = "CloudDiagnosis:EvolutionEnabled";

    /// <summary>会话内战术注入块上限（默认 20）。</summary>
    public const string CloudDiagnosisEvolutionMaxInjectPerSession =
        "CloudDiagnosis:EvolutionMaxInjectPerSession";

    // ── 本体 harness（写作前介入 / 硬偏差阻断云端）──
    /// <summary>本体 harness 总开关（默认 true）。</summary>
    public const string CloudDiagnosisOntologyHarnessEnabled =
        "CloudDiagnosis:OntologyHarnessEnabled";

    /// <summary>每章最多注入的信号数（默认 4）。</summary>
    public const string CloudDiagnosisOntologyHarnessMaxSignals =
        "CloudDiagnosis:OntologyHarnessMaxSignals";

    /// <summary>情节线停转判定窗口（默认 5 章）。</summary>
    public const string CloudDiagnosisOntologyHarnessStallWindow =
        "CloudDiagnosis:OntologyHarnessStallWindow";

    /// <summary>硬偏差时阻断本轮云端诊断（默认 true）。</summary>
    public const string CloudDiagnosisOntologyHarnessGateCloud =
        "CloudDiagnosis:OntologyHarnessGateCloud";

    // ── 素材提示（knowledge.hint / context.retrieve 捎带）──
    public const string CloudDiagnosisMaterialHintsEnabled = "CloudDiagnosis:MaterialHintsEnabled";

    /// <summary>改用 context.retrieve 捎带 material-hints（默认 false，与预取互斥）。</summary>
    public const string CloudDiagnosisMaterialHintsFromHarnessEnabled =
        "CloudDiagnosis:MaterialHintsFromHarnessEnabled";

    /// <summary>提示块字符预算（默认 800）。</summary>
    public const string CloudDiagnosisMaterialHintMaxChars = "CloudDiagnosis:MaterialHintMaxChars";

    /// <summary>预取超时毫秒（默认 3000）。</summary>
    public const string CloudDiagnosisMaterialHintTimeoutMs =
        "CloudDiagnosis:MaterialHintTimeoutMs";
}
