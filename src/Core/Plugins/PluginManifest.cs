using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Shared;

namespace Core.Plugins;

/// <summary>
/// 插件清单 — 描述插件的元数据信息。
/// 对应插件目录中的 plugin.json 文件。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>插件显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>插件唯一标识符。</summary>
    public string Id { get; set; } = "";

    /// <summary>插件版本号，默认 "1.0.0"。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>插件简短描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>插件作者。</summary>
    public string Author { get; set; } = "";

    /// <summary>插件主页 URL（可选）。</summary>
    public string? Homepage { get; set; }

    /// <summary>最低核心版本要求（可选）。</summary>
    public string? MinCoreVersion { get; set; }

    /// <summary>插件权限列表。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>JSON 序列化选项，使用 kebab-case（全仓统一命名约定）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 从指定目录加载插件清单。
    /// </summary>
    /// <param name="dir">插件目录路径。</param>
    /// <returns>解析后的插件清单，如果文件不存在或解析失败则返回 null。</returns>
    public static PluginManifest? LoadFromDirectory(string dir)
    {
        var path = Path.Combine(dir, "plugin.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = AtomicFile.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将插件清单保存到指定目录。
    /// </summary>
    /// <param name="dir">目标目录路径。</param>
    public void SaveToDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        var json = JsonSerializer.Serialize(this, JsonOpts);
        AtomicFile.WriteAllText(path, json);
    }
}
