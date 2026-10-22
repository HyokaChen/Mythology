using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Stability;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// Agent 稳定性配置工厂（Desktop 本地移植自 HostBootstrapper.ConfigureAgentStability，
/// Desktop 不引用 Host）：ModelContextProfile 从配置推导，稳定器组件阈值全部由
/// profile 动态计算。宿主启动装配一次（Program.InitializeRuntime），模型/思考级别/
/// 上下文窗口变更后由 <see cref="Reapply"/> 重算并同步到全部组件。
/// </summary>
public static class AgentProfile
{
    /// <summary>
    /// 从模型名 + 用户配置的上下文窗口 + 思考级别构建 ModelContextProfile。
    /// Adaptive-only thinking 模型不接受 manual budget_tokens，
    /// ThinkingBudgetTokens 语义为"未知/不可预测"，设为 0（HasThinking 通过
    /// IsAdaptiveThinking 联合判断，不依赖此数值）。
    /// </summary>
    public static ModelContextProfile BuildProfile(
        string? modelName,
        int contextWindowSize,
        string? thinkingLevelStr
    )
    {
        var effectiveContextSize = ModelCapabilityResolver.ResolveEffectiveContextSize(
            modelName,
            contextWindowSize
        );
        var thinkingLevel = ThinkingLevelBudgets.FromString(thinkingLevelStr);
        var isAdaptiveThinking = ModelCapabilityResolver.IsAdaptiveThinkingModel(modelName);
        var thinkingBudget = isAdaptiveThinking
            ? 0
            : ThinkingLevelBudgets.GetBudgetTokens(thinkingLevel, effectiveContextSize);

        return new ModelContextProfile
        {
            ContextWindowTokens = effectiveContextSize,
            ThinkingBudgetTokens = thinkingBudget,
            IsAdaptiveThinking = isAdaptiveThinking,
        };
    }

    /// <summary>
    /// 创建并装配全部稳定器组件（GrowthRatioGuard / VSA / RGDecimator），
    /// 同步 ConversationLoop 的 ModelProfile。启动装配入口。
    /// </summary>
    public static void Apply(Agent agent, ModelContextProfile modelProfile)
    {
        // GrowthRatioGuard：阈值从 ModelContextProfile 计算
        var stabilityConfig = new StabilityHarness.Lyapunov.StabilityConfig();
        stabilityConfig.ApplyProfile(modelProfile);
        agent.StabilityMonitor = StabilityHarness.StabilityHarness.CreateGuard(stabilityConfig);

        // 全息引擎：漂移检测阈值从 ModelContextProfile 计算
        var vsaConfig = new StabilityHarness.VSA.VSAConfig();
        vsaConfig.ApplyProfile(modelProfile);
        agent.HolographicEngine = StabilityHarness.StabilityHarness.CreateHolographicEngine(
            vsaConfig
        );

        // 消息评分器：MaxRetained 和 Threshold 从 ModelContextProfile 计算
        var rgConfig = new StabilityHarness.RGDecimator.RGConfig();
        rgConfig.ApplyProfile(modelProfile);
        agent.MessageScorer = (IMessageScorer)
            StabilityHarness.StabilityHarness.CreateDecimator(rgConfig);

        // 同步 ConversationLoop 的 ModelContextProfile + Agent 层组件
        agent.ApplyModelProfile(modelProfile);
    }

    /// <summary>
    /// 从配置重算 profile 并应用到既有稳定器组件（模型/上下文窗口/thinking 变更后调用；
    /// 阈值经 ApplyProfile 重算，组件实例复用）。
    /// </summary>
    public static void Reapply(Agent agent, ConfigStore store)
    {
        var providerConfig = store.Load();
        var profile = BuildProfile(
            providerConfig.Providers.DefaultModel,
            providerConfig.Providers.ContextWindowSize,
            providerConfig.Providers.ThinkingLevel
        );
        agent.ApplyModelProfile(profile);
    }
}
