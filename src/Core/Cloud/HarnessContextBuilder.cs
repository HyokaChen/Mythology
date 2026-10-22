using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Data;

namespace Core.Cloud;

/// <summary>
/// Harness 请求上下文构造 — 从本地 Novel 实体上传结构化上下文（角色/情节线/物品/时间线/标题）。
/// 云端 FictionAnalyzers 按 CHANGE_03/04 因子族需要结构化输入：
/// 仅传角色名时 CharacterArc/Immersion/Naming/PowerSystem/PlotHole 等分析器会降级。
/// 三个调用点（CloudAnalysisTool / DiagnoseWritingTool / EnhancementEngine）共用，保证一致。
/// </summary>
public static class HarnessContextBuilder
{
    /// <summary>
    /// 由 Novel 构造完整 AnalysisContextInput：角色名 hints（兼容旧云端）+ 结构化详情 + 情节线 + 物品 + 时间线 + 标题。
    /// CharacterVersion/PlotlineVersion 为结构化数据的 SHA256 指纹，云端用于跨请求去重传输。
    /// </summary>
    public static AnalysisContextInput Build(Novel novel, string? genreHint = null)
    {
        var characters = novel.Characters.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        var plotlines = novel.Plotlines.ToList();

        // 可确定性序列化以便远端比对（按 Name 排序 → 稳定 JSON → SHA256 hex）
        var charVersion = ComputeJsonVersionHash(
            characters
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    c.Name,
                    Role = c.Role.ToString(),
                    c.Gender,
                    c.Age,
                    Personality = c.Personality ?? "",
                    Background = c.Background ?? "",
                    Motivation = c.Motivation ?? "",
                    Abilities = c.Abilities ?? "",
                    Weakness = c.Weakness ?? "",
                    Habits = c.Habits ?? "",
                })
        );

        var plotVersion = ComputeJsonVersionHash(
            plotlines
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Name,
                    Type = p.Type.ToString(),
                    p.ResolvedAtChapterId,
                    p.PlannedRevealChapterId,
                })
        );

        var items = novel
            .Items.Where(i => !string.IsNullOrEmpty(i.Name))
            .Select(i => new CloudItemInput(i.Name))
            .ToList();

        // 时间线事件（2026-10-18 接线）：PlotHoleDetector 时间线冲突检查的唯一数据源。
        // 事件归属章节引用用本地章 ID（与 ChapterInput.ClientChapterRef 同一命名空间）；
        // 无事件时发 null 而非空数组，避免在 wire 上新增无意义的占位键。
        var timeline = novel
            .TimelineEvents.Where(e => !string.IsNullOrEmpty(e.Id))
            .Select(e => new CloudTimelineEventInput(e.Id, e.Title, e.Description, e.ChapterId))
            .ToList();

        return new AnalysisContextInput(
            "none",
            null,
            characters.Select(c => c.Name).ToList(),
            genreHint is null ? [] : [genreHint],
            characters
                .Select(c => new CloudCharacterInput(
                    c.Name,
                    c.Role.ToString().ToLowerInvariant(),
                    c.Gender,
                    c.Age,
                    c.Personality,
                    c.Background,
                    c.Motivation,
                    c.Abilities,
                    c.Weakness,
                    c.Habits
                ))
                .ToList(),
            plotlines
                .Select(p => new CloudPlotlineInput(
                    p.Name,
                    p.Type.ToString().ToLowerInvariant(),
                    p.ResolvedAtChapterId,
                    p.PlannedRevealChapterId
                ))
                .ToList(),
            items,
            string.IsNullOrWhiteSpace(novel.Title) ? null : novel.Title,
            CharacterVersion: charVersion,
            PlotlineVersion: plotVersion,
            Timeline: timeline.Count > 0 ? timeline : null
        );
    }

    private static string ComputeJsonVersionHash<T>(IEnumerable<T> items)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(items, SystemTextJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SystemTextJsonOptions = new(
        JsonSerializerDefaults.Web
    )
    {
        WriteIndented = false,
    };
}
