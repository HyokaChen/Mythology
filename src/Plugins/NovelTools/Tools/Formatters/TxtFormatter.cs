using System.Text;
using Core.Data;

namespace Plugins.NovelTools.Tools.Formatters;

/// <summary>
/// 纯文本格式生成器 — 将小说数据转换为纯文本格式。
/// </summary>
public sealed class TxtFormatter : IFormatter
{
    /// <summary>
    /// 生成纯文本格式的字符串。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <param name="settings">格式化设置。</param>
    /// <returns>纯文本字符串。</returns>
    public string Generate(Novel novel, List<Chapter> chapters, FormatterSettings settings)
    {
        var sb = new StringBuilder();

        sb.AppendLine(novel.Title);
        sb.AppendLine(new string('=', novel.Title.Length));
        sb.AppendLine();

        foreach (var chapter in chapters.OrderBy(c => c.Order))
        {
            sb.AppendLine($"第{chapter.Order}章 {chapter.Title}");
            if (settings.IncludeMetadata)
                sb.AppendLine($"状态: {chapter.Status} | 字数: {chapter.Content.Length}");
            sb.AppendLine();
            sb.AppendLine(chapter.Content);
            sb.AppendLine();
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
