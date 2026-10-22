using System.Text;

namespace Core.Shared;

/// <summary>
/// Store 层共享工具方法，供 FileNovelStore / FileSessionStore 等文件存储实现复用。
/// </summary>
public static class StoreHelper
{
    /// <summary>
    /// 从字典中安全读取字符串值。键不存在或值为 null 时返回 null。
    /// </summary>
    public static string? GetString(Dictionary<string, object?> dict, string key) =>
        dict.TryGetValue(key, out var val) ? val?.ToString() : null;

    /// <summary>
    /// 解析日期时间字符串。解析失败返回 <see cref="DateTime.UtcNow"/>。
    /// </summary>
    public static DateTime ParseDateTime(string? str) =>
        str is not null && DateTime.TryParse(str, out var dt) ? dt : DateTime.UtcNow;

    /// <summary>
    /// 从字典中读取整数值。键不存在 / 解析失败 / 负数返回 0。
    /// </summary>
    public static int GetInt(Dictionary<string, object?> dict, string key) =>
        int.TryParse(GetString(dict, key), out var i) && i > 0 ? i : 0;

    /// <summary>
    /// 从字典中读取日期时间值。键不存在或解析失败返回 <see cref="DateTime.UtcNow"/>。
    /// </summary>
    public static DateTime GetDateTime(Dictionary<string, object?> dict, string key) =>
        ParseDateTime(GetString(dict, key));

    /// <summary>
    /// 将文件名中的非法字符替换为下划线。
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name);
        for (var i = 0; i < sb.Length; i++)
        {
            if (invalid.Contains(sb[i]))
                sb[i] = '_';
        }
        return sb.ToString();
    }
}
