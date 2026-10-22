using System.Text;
using Core.Data;
using Core.Shared;

namespace Plugins.NovelTools.Tools.Formatters;

/// <summary>
/// Markdown 格式生成器 — 将小说数据转换为 Markdown 文本。
/// </summary>
public sealed class MarkdownFormatter : IFormatter
{
    /// <summary>
    /// 生成 Markdown 格式的字符串。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <param name="settings">格式化设置。</param>
    /// <returns>Markdown 字符串。</returns>
    public string Generate(Novel novel, List<Chapter> chapters, FormatterSettings settings)
    {
        var sb = new StringBuilder();

        // 标题
        sb.AppendLine($"# {novel.Title}");
        sb.AppendLine();
        if (settings.IncludeMetadata)
        {
            var totalWords = chapters.Sum(c => c.Content.Length);
            sb.AppendLine(
                $"> 类型: {novel.Genre} | 风格: {novel.WritingStyle} | 总字数: {totalWords}"
            );
            sb.AppendLine();
        }

        // 描述
        if (!string.IsNullOrEmpty(novel.Description))
        {
            sb.AppendLine(novel.Description);
            sb.AppendLine();
        }

        // 章节
        sb.AppendLine("---");
        sb.AppendLine();
        foreach (var chapter in chapters.OrderBy(c => c.Order))
        {
            sb.AppendLine($"## 第{chapter.Order}章: {chapter.Title}");
            if (settings.IncludeMetadata)
            {
                sb.AppendLine($"\n*状态: {chapter.Status} | 字数: {chapter.Content.Length}*\n");
            }
            sb.AppendLine();
            sb.AppendLine(chapter.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // 附录
        if (settings.IncludeAppendix)
        {
            AppendAppendix(sb, settings);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 追加附录内容（角色、地点、组织表格）。
    /// </summary>
    /// <param name="sb">StringBuilder 实例。</param>
    /// <param name="settings">格式化设置。</param>
    private static void AppendAppendix(StringBuilder sb, FormatterSettings settings)
    {
        sb.AppendLine("## 附录");
        sb.AppendLine();

        if (settings.Characters.Count > 0)
        {
            sb.AppendLine("### 角色");
            sb.AppendLine();
            sb.AppendLine(
                "| 姓名 | 性别 | 年龄 | 定位 | 组织 | 性格 | 习惯 | 动机 | 弱点 | 状态 |"
            );
            sb.AppendLine(
                "|------|------|------|------|------|------|------|------|------|------|"
            );
            foreach (var c in settings.Characters)
            {
                var status = c.Status switch
                {
                    CharacterStatus.Active => "活跃",
                    CharacterStatus.Deceased => "已故",
                    CharacterStatus.Missing => "失踪",
                    CharacterStatus.Transformed => "转化",
                    _ => c.Status.ToString(),
                };
                var role = c.Role switch
                {
                    CharacterRole.Protagonist => "主角",
                    CharacterRole.Supporting => "配角",
                    CharacterRole.Minor => "龙套",
                    CharacterRole.Antagonist => "反派",
                    _ => c.Role.ToString(),
                };
                var gender = string.IsNullOrEmpty(c.Gender) ? "-" : c.Gender;
                var age = string.IsNullOrEmpty(c.Age) ? "-" : c.Age;
                var faction = c.FactionId ?? "-";
                sb.AppendLine(
                    $"| {c.Name} | {gender} | {age} | {role} | {faction} | {Truncate(c.Personality, 20)} | {Truncate(c.Habits, 20)} | {Truncate(c.Motivation, 20)} | {Truncate(c.Weakness, 20)} | {status} |"
                );
            }
            sb.AppendLine();
        }
        if (settings.Locations.Count > 0)
        {
            sb.AppendLine("### 地点");
            sb.AppendLine();
            sb.AppendLine("| 名称 | 类型 | 氛围 | 描述 |");
            sb.AppendLine("|------|------|------|------|");
            foreach (var l in settings.Locations)
            {
                var typeLabel = l.Type switch
                {
                    LocationType.City => "城市",
                    LocationType.Rural => "乡村",
                    LocationType.Indoor => "室内",
                    LocationType.Wilderness => "荒野",
                    LocationType.Fictional => "虚构",
                    _ => "其他",
                };
                var atmosphere = string.IsNullOrEmpty(l.Atmosphere) ? "-" : l.Atmosphere;
                sb.AppendLine(
                    $"| {l.Name} | {typeLabel} | {atmosphere} | {Truncate(l.Description, 30)} |"
                );
            }
            sb.AppendLine();
        }

        if (settings.Factions.Count > 0)
        {
            sb.AppendLine("### 组织");
            sb.AppendLine();
            sb.AppendLine("| 名称 | 类型 | 首领 | 总部 | 目标 | 描述 |");
            sb.AppendLine("|------|------|------|------|------|------|");
            foreach (var f in settings.Factions)
            {
                var typeLabel = f.Type switch
                {
                    FactionType.Sect => "门派",
                    FactionType.Company => "公司",
                    FactionType.Family => "家族",
                    FactionType.Gang => "帮会",
                    FactionType.Government => "政府",
                    _ => "其他",
                };
                var headquarters = f.HeadquartersId ?? "-";
                var goal = string.IsNullOrEmpty(f.Goal) ? "-" : Truncate(f.Goal, 20);
                sb.AppendLine(
                    $"| {f.Name} | {typeLabel} | {f.Leader} | {headquarters} | {goal} | {Truncate(f.Description, 30)} |"
                );
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// 截断字符串到指定长度。
    /// </summary>
    /// <param name="s">原始字符串。</param>
    /// <param name="max">最大长度。</param>
    /// <returns>截断后的字符串。</returns>
    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "-" : s.Truncate(max);
}
