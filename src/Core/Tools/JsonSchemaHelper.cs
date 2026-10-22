using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Core.Shared;

namespace Core.Tools;

/// <summary>
/// JSON Schema 辅助工具类。
/// 根据 C# 类型自动生成 JSON Schema，用于 LLM 工具参数定义。
/// </summary>
public static class JsonSchemaHelper
{
    /// <summary>JSON 序列化选项，复用 JsonHelper 的配置。</summary>
    public static readonly JsonSerializerOptions JsonOptions = JsonHelper.JsonOptions;

    /// <summary>
    /// 为指定类型生成 JSON Schema。
    /// 扫描类型的公共属性，根据属性类型和特性生成对应的 Schema。
    /// </summary>
    /// <typeparam name="T">要生成 Schema 的类型。</typeparam>
    /// <returns>
    /// 生成的 JSON Schema 元素；无公共属性的类型生成空对象 Schema（<c>{"type":"object","properties":{}}</c>）——
    /// 空参数工具必须是可调用的（返回 null 会让 ConversationLoop 静默丢弃该工具，工具对模型不存在）。
    /// </returns>
    public static JsonElement? GenerateSchema<T>()
    {
        var type = typeof(T);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (properties.Length == 0)
            return JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");

        var schemaObj = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>(),
            ["required"] = new List<string>(),
        };

        var props = (Dictionary<string, object>)schemaObj["properties"];
        var required = (List<string>)schemaObj["required"];

        foreach (var prop in properties)
        {
            // 将属性名转换为 kebab-case 格式（与 JsonHelper 一致）
            var jsonName = JsonNamingPolicy.KebabCaseLower.ConvertName(prop.Name);
            var propSchema = GetPropertySchema(prop.PropertyType, prop);
            props[jsonName] = propSchema;

            // Nullable reference types 或值类型视为必填
            if (
                Nullable.GetUnderlyingType(prop.PropertyType) is null
                || IsNonNullableReference(prop)
            )
            {
                required.Add(jsonName);
            }
        }

        // 如果没有必填字段，移除 required 节点
        if (required.Count == 0)
            schemaObj.Remove("required");

        var json = JsonSerializer.Serialize(schemaObj, JsonOptions);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>
    /// 获取单个属性的 Schema 定义。
    /// </summary>
    /// <param name="propType">属性类型。</param>
    /// <param name="propInfo">属性信息（可选，用于读取 Description 特性）。</param>
    /// <returns>属性的 Schema 字典。</returns>
    private static Dictionary<string, object> GetPropertySchema(
        Type propType,
        PropertyInfo? propInfo = null
    )
    {
        var underlying = Nullable.GetUnderlyingType(propType) ?? propType;

        Dictionary<string, object> schema;

        if (underlying == typeof(string))
            schema = new Dictionary<string, object> { ["type"] = "string" };
        else if (
            underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(short)
        )
            schema = new Dictionary<string, object> { ["type"] = "integer" };
        else if (
            underlying == typeof(float)
            || underlying == typeof(double)
            || underlying == typeof(decimal)
        )
            schema = new Dictionary<string, object> { ["type"] = "number" };
        else if (underlying == typeof(bool))
            schema = new Dictionary<string, object> { ["type"] = "boolean" };
        else if (
            underlying.IsArray
            || (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        )
        {
            // 数组或列表类型：递归生成元素类型的 Schema
            var elementType = underlying.IsArray
                ? underlying.GetElementType()!
                : underlying.GetGenericArguments()[0];
            schema = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = GetPropertySchema(elementType),
            };
        }
        else if (underlying.IsClass && underlying != typeof(object))
        {
            // 嵌套对象类型 — 递归生成 schema
            schema = GenerateObjectSchema(underlying);
        }
        else
        {
            // 默认类型为字符串
            schema = new Dictionary<string, object> { ["type"] = "string" };
        }

        // 读取 [Description("...")] 特性并注入 schema
        var descAttr = propInfo?.GetCustomAttribute<DescriptionAttribute>();
        if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description))
        {
            schema["description"] = descAttr.Description;
        }

        return schema;
    }

    /// <summary>
    /// 为嵌套 object 类型生成完整的 JSON Schema。
    /// 递归扫描类型的所有公共属性。
    /// </summary>
    /// <param name="type">要生成 Schema 的嵌套对象类型。</param>
    /// <returns>嵌套对象的 Schema 字典。</returns>
    private static Dictionary<string, object> GenerateObjectSchema(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in properties)
        {
            var jsonName = JsonNamingPolicy.KebabCaseLower.ConvertName(prop.Name);
            props[jsonName] = GetPropertySchema(prop.PropertyType, prop);

            if (
                Nullable.GetUnderlyingType(prop.PropertyType) is null
                || IsNonNullableReference(prop)
            )
            {
                required.Add(jsonName);
            }
        }

        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    /// <summary>
    /// 判断属性是否为非空引用类型。
    /// 在 nullable-enabled 上下文中，string? 会标记为可空，但 string 不会。
    /// 简化处理: public string => required, public string? => optional。
    /// </summary>
    /// <param name="prop">属性信息。</param>
    /// <returns>是否为非空引用类型。</returns>
    private static bool IsNonNullableReference(PropertyInfo prop)
    {
        var nullableContext = new NullabilityInfoContext();
        var info = nullableContext.Create(prop);
        return info.WriteState == NullabilityState.NotNull;
    }
}
