using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Data;

namespace Plugins.NovelTools.Tools.Formatters;

/// <summary>
/// JSON 格式生成器 — 将小说数据序列化为结构化的 JSON 字符串。
/// </summary>
public sealed class JsonFormatter : IFormatter
{
    /// <summary>JSON 序列化选项（静态缓存，避免重复创建）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 生成 JSON 格式的字符串。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <param name="settings">格式化设置。</param>
    /// <returns>JSON 字符串。</returns>
    public string Generate(Novel novel, List<Chapter> chapters, FormatterSettings settings)
    {
        var export = new
        {
            title = novel.Title,
            genre = novel.Genre,
            description = novel.Description,
            writing_style = novel.WritingStyle,
            total_words = chapters.Sum(c => c.Content.Length),
            chapters = chapters
                .OrderBy(c => c.Order)
                .Select(c => new
                {
                    order = c.Order,
                    title = c.Title,
                    status = c.Status.ToString(),
                    word_count = c.Content.Length,
                    content = c.Content,
                }),
            characters = settings.IncludeAppendix
                ? settings.Characters.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    gender = c.Gender,
                    age = c.Age,
                    role = c.Role.ToString().ToLowerInvariant(),
                    personality = c.Personality,
                    appearance = c.Appearance,
                    background = c.Background,
                    motivation = c.Motivation,
                    status = c.Status.ToString(),
                })
                : null,
            locations = settings.IncludeAppendix
                ? settings.Locations.Select(l => new
                {
                    id = l.Id,
                    name = l.Name,
                    description = l.Description,
                    parent_id = l.ParentId,
                })
                : null,
            factions = settings.IncludeAppendix
                ? settings.Factions.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    leader = f.Leader,
                    description = f.Description,
                })
                : null,
        };

        return JsonSerializer.Serialize(export, JsonOptions);
    }
}
