using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 未登记死亡检测（简单确定性）：草稿章正文里出现角色名 + 死亡关键词，且本章没有该角色的
/// status 变化、角色当前也不在 Deceased，即作为「状态发现」候选交给发布预览确认。
/// </summary>
public static class DeathCandidateDetector
{
    private static readonly string[] DeathKeywords =
    [
        "死了",
        "身亡",
        "断气",
        "殒命",
        "陨落",
        "阵亡",
        "被杀",
        "暴毙",
        "咽气",
        "毙命",
        "身死",
        "陨灭",
    ];

    private static readonly string[] Exclusions =
    [
        "没死",
        "未死",
        "不会死",
        "假死",
        "诈死",
        "险些死",
        "差点死",
    ];

    /// <summary>检测草稿章中「正文暗示死亡但未登记」的角色。</summary>
    public static List<DetectedDeathCandidate> Detect(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<EntityStateChange> chapterChanges
    )
    {
        var result = new List<DetectedDeathCandidate>();
        if (chapter.Status != ChapterStatus.Draft || string.IsNullOrWhiteSpace(chapter.Content))
            return result;

        var recorded = chapterChanges
            .Where(c =>
                c.TargetEntityType == EntityType.Character
                && c.Effects.Any(e =>
                    string.Equals(e.Field, "status", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Select(c => c.TargetId)
            .ToHashSet(StringComparer.Ordinal);

        var paragraphs = chapter.Content.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var character in novel.Characters)
        {
            if (character.Status == CharacterStatus.Deceased)
                continue;
            if (recorded.Contains(character.Id))
                continue;
            if (string.IsNullOrWhiteSpace(character.Name))
                continue;

            foreach (var paragraph in paragraphs)
            {
                if (Exclusions.Any(paragraph.Contains))
                    continue;
                if (!paragraph.Contains(character.Name, StringComparison.Ordinal))
                    continue;
                var keyword = DeathKeywords.FirstOrDefault(paragraph.Contains);
                if (keyword is null)
                    continue;

                var evidence = paragraph.Length > 120 ? paragraph[..120] : paragraph;
                result.Add(
                    new DetectedDeathCandidate(
                        $"ev_{chapter.Id}_{character.Id}_detected",
                        character.Id,
                        character.Name,
                        evidence,
                        $"正文检测到「{character.Name}」死亡（{keyword}），但本章未登记状态变化"
                    )
                );
                break;
            }
        }
        return result;
    }
}
