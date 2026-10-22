using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Shared;

/// <summary>
/// 全局共享的 JSON 序列化配置。
/// 用于整个应用程序的 JSON 序列化和反序列化操作。
/// </summary>
public static class JsonHelper
{
    /// <summary>
    /// 全局共享的 JSON 序列化选项。
    /// 配置说明：
    /// - PropertyNameCaseInsensitive: 属性名大小写不敏感匹配
    /// - PropertyNamingPolicy: 使用 kebab-case 命名风格（全仓统一命名约定）
    /// - WriteIndented: 不格式化缩进（节省空间）
    /// - DefaultIgnoreCondition: 忽略 null 值
    /// - Encoder: 允许不安全的 JSON 转义（性能优化）
    /// - Converters: 支持枚举的字符串序列化
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 允许从字符串读取数字（LLM 工具调用常把 int 值传为字符串 "5" 而非 5）
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(), new FlexibleStringListConverter() },
    };
}
