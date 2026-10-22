using System.Text;
using Core.Data;

namespace Core.Prompts;

/// <summary>
/// 每轮注入模型的「当前小说环境」块（TUI 与 Desktop 共用同一实现，避免两个宿主的注入口径漂移）。
///
/// 三层大纲都在这里出现：<b>总纲</b>（<see cref="Novel.Outline"/>，截断摘要）、
/// <b>卷纲概览</b>（每卷一行的卷名 + 章数 + 梗概摘要）、<b>章节细纲</b>（不进注入块——
/// 细纲按需用 get_outline / 读章节文件获取，注入块只带章节标题与进度）。
///
/// 长度有界：长篇（上千章 / 上百卷）注入块大小恒定，不随规模增长。
/// </summary>
public static class NovelContextBlock
{
    /// <summary>总纲摘要上限（字符）。超出截断，避免长篇总纲吃掉上下文。</summary>
    public const int OutlineMaxChars = 200;

    /// <summary>单卷梗概摘要上限（字符）。</summary>
    public const int VolumeBriefMaxChars = 40;

    /// <summary>卷纲概览最多列几卷（超出只给总数）。</summary>
    public const int MaxVolumeLines = 8;

    /// <summary>章节进度最多列几章（超出只给总数）。</summary>
    public const int MaxChapterLines = 10;

    /// <summary>简介摘要上限（字符）。</summary>
    public const int DescriptionMaxChars = 200;

    /// <summary>
    /// 构建注入块。<paramref name="chapters"/> 需已按 <see cref="Chapter.Order"/> 升序；
    /// <paramref name="now"/> 为 null 时取本地时间（测试可传入固定值）。
    /// </summary>
    public static string Build(
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<Volume> volumes,
        DateTime? now = null
    )
    {
        var chapterLines = BuildChapterProgress(chapters);
        var volumeLines = BuildVolumeOverview(volumes, chapters);

        var sb = new StringBuilder(capacity: 1024);
        sb.Append("\n当前小说环境\n────────────\n");
        sb.Append("项目: ").Append(novel.Title).Append(" (ID: ").Append(novel.Id).Append(")\n");
        sb.Append("类型: ").Append(novel.Genre).Append('\n');
        sb.Append("风格: ").Append(novel.WritingStyle).Append('\n');
        sb.Append("简介: ").Append(Excerpt(novel.Description, DescriptionMaxChars)).Append('\n');

        // 大纲一层：总纲（作者内部规划，含剧透；模型据此保持走向一致）
        sb.Append("总纲: ").Append(Excerpt(novel.Outline, OutlineMaxChars)).Append('\n');

        // 大纲二层：卷纲概览（每卷一行；超过 MaxVolumeLines 只给总数）
        sb.Append("分卷: ")
            .Append(volumes.Count)
            .Append(" 卷 · ")
            .Append(chapters.Count)
            .Append(" 章\n");
        if (volumeLines.Count > 0)
        {
            foreach (var line in volumeLines)
                sb.Append("  ").Append(line).Append('\n');
        }

        sb.Append("章节数: ").Append(chapters.Count).Append('\n');
        sb.Append("当前进度:\n").Append(chapterLines).Append('\n');
        sb.Append("角色数: ").Append(novel.Characters.Count).Append('\n');
        sb.Append("情节线数: ").Append(novel.Plotlines.Count).Append('\n');
        sb.Append("势力数: ").Append(novel.Factions.Count).Append('\n');
        sb.Append("当前时间: ")
            .Append((now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm"))
            .Append("\n\n");
        sb.Append("⚡ 你已在小说「")
            .Append(novel.Title)
            .Append("」的工作环境中。所有需要 novel_id 的工具，传 \"")
            .Append(novel.Id)
            .Append(
                "\"。章节细纲用 get_outline 查看；不要调用 create_novel 除非用户明确要求创建新项目。"
                    + "如果用户的请求偏离当前小说设定，先用 ask_user 确认意图。\n"
            );
        return sb.ToString();
    }

    /// <summary>章节进度：最多 <see cref="MaxChapterLines"/> 行，最后一行标「← 当前进度」。</summary>
    private static string BuildChapterProgress(IReadOnlyList<Chapter> chapters)
    {
        if (chapters.Count == 0)
            return "  暂无章节";

        var last = chapters[^1];
        var sb = new StringBuilder();
        foreach (var c in chapters.Take(MaxChapterLines))
        {
            sb.Append("  ")
                .Append(c.Order)
                .Append(". ")
                .Append(c.Title)
                .Append(" (")
                .Append(c.Status)
                .Append(')');
            if (ReferenceEquals(c, last))
                sb.Append(" ← 当前进度");
            sb.Append('\n');
        }

        if (chapters.Count > MaxChapterLines)
            sb.Append("  ... 共 ").Append(chapters.Count).Append(" 章");
        else if (sb.Length > 0 && sb[^1] == '\n')
            sb.Length--; // 去掉尾随换行，保持单段
        return sb.ToString();
    }

    /// <summary>卷纲概览：每卷一行（卷名 + 章数 + 梗概摘要），超出上限只给总数。</summary>
    private static List<string> BuildVolumeOverview(
        IReadOnlyList<Volume> volumes,
        IReadOnlyList<Chapter> chapters
    )
    {
        var lines = new List<string>(Math.Min(volumes.Count, MaxVolumeLines) + 1);
        if (volumes.Count == 0)
            return lines;

        var chapterCountByVolume = chapters
            .Where(c => !string.IsNullOrEmpty(c.VolumeId))
            .GroupBy(c => c.VolumeId!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var v in volumes.OrderBy(v => v.Order).Take(MaxVolumeLines))
        {
            var count = chapterCountByVolume.GetValueOrDefault(v.Id, 0);
            var sb = new StringBuilder();
            sb.Append("📦 第")
                .Append(v.Order)
                .Append("卷 ")
                .Append(v.Name)
                .Append("（")
                .Append(count)
                .Append("章）");
            if (!string.IsNullOrWhiteSpace(v.Summary))
                sb.Append("— ").Append(Excerpt(v.Summary, VolumeBriefMaxChars));
            else if (!string.IsNullOrWhiteSpace(v.Goal))
                sb.Append("— 目标: ").Append(Excerpt(v.Goal, VolumeBriefMaxChars));
            lines.Add(sb.ToString());
        }

        if (volumes.Count > MaxVolumeLines)
            lines.Add($"… 共 {volumes.Count} 卷（其余卷纲用 get_outline 查看）");
        return lines;
    }

    /// <summary>截断摘要：空白归一 + 超长加省略号；空白输入给占位文案。</summary>
    private static string Excerpt(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(未填写)";

        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length > maxChars ? normalized[..maxChars] + "…" : normalized;
    }
}
