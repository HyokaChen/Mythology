using Core.Data;
using Core.Shared;

namespace Plugins.WritingTools;

/// <summary>
/// 自动选择最佳变体 — 根据写作配置自动选择保守版或适度版。
/// commercial → 适度版（大胆改写，优先读者体验）
/// literary → 保守版（保留原文韵味）
/// balanced → 看分数差距选择
/// </summary>
public static class AutoSelectBest
{
    /// <summary>
    /// 根据写作配置自动选择最佳变体。
    /// </summary>
    /// <param name="candidates">候选变体列表</param>
    /// <param name="profile">写作配置</param>
    /// <param name="dimension">维度名称（用于 humanizer 特殊规则）</param>
    /// <param name="subType">问题子类型（用于 humanizer 特殊规则）</param>
    /// <param name="level">humanizer 分层（A/B/C/F/X/H；非 humanizer 可空）</param>
    /// <returns>选中的变体，null 表示无法选择</returns>
    public static EnhancementCandidate? Select(
        List<EnhancementCandidate> candidates,
        WritingProfile profile,
        string dimension = "",
        string? subType = null,
        string? level = null
    )
    {
        if (candidates.Count == 0)
            return null;

        // humanizer：只有显式 A/B 级、full 模式、且存在保守版时才自动改；
        // F/X/H/空级别（指纹/不改/人审/旧服务端）一律不自动写。
        if (dimension == DimensionKeys.Humanizer && subType != "fawning_tone")
        {
            if (
                !string.Equals(profile.AutoEnhance, "full", StringComparison.Ordinal)
                || level is not ("A" or "B")
            )
                return null;
            return candidates.FirstOrDefault(c => c.Label == "保守版");
        }

        if (candidates.Count == 1)
            return candidates[0];

        return profile.EnhancementProfile switch
        {
            "commercial" => candidates.FirstOrDefault(c => c.Label == "适度版")
                ?? candidates.FirstOrDefault(c => c.Label == "增强版")
                ?? candidates[0],

            "literary" => candidates.FirstOrDefault(c => c.Label == "保守版") ?? candidates[0],

            // balanced: 优先保守版，除非分数差距大
            _ => candidates.FirstOrDefault(c => c.Label == "保守版")
                ?? candidates.FirstOrDefault(c => c.Label == "适度版")
                ?? candidates[0],
        };
    }
}
