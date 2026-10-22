using System.Text.Json;

namespace Core.Cloud;

/// <summary>
/// 联想记忆云端调用：组 <c>fiction.associative.link</c> 请求 → 调用 → 反序列化结果。
/// 工具（index/recall）与触发钩子共用本收口，避免三处各写一遍 wire 细节。
/// </summary>
public static class AssociativeLinkClient
{
    /// <summary>能力 ID（云端 CapabilityCatalog 单一事实源）。</summary>
    public const string Capability = "fiction.associative.link";

    /// <summary>入库结果反序列化选项（字段名已由契约上的 JsonPropertyName 钉死）。</summary>
    private static readonly JsonSerializerOptions s_resultJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 调用一次联想能力。<paramref name="depth"/>：<c>standard</c> = 召回 + 索引；<c>quick</c> = 仅召回。
    /// 返回 <c>null</c> 表示调用失败或结果不可解析（调用方静默降级，不伪造内容）。
    /// </summary>
    public static async Task<AssociativeLinkResultDto?> LinkAsync(
        IFictionHarnessClient harness,
        string novelId,
        IReadOnlyList<AssociativeSourceInput> sources,
        string? query,
        string idempotencyKey,
        string depth = "standard",
        CancellationToken ct = default
    )
    {
        var request = new HarnessRequest
        {
            Capability = Capability,
            Depth = depth,
            Locale = "zh-CN",
            NovelId = novelId,
            SceneGoal = query,
            Sources = sources,
        };

        var result = await harness.ExecuteAsync(request, idempotencyKey, ct);
        if (result.Result is not JsonElement element)
            return null;

        try
        {
            return element.Deserialize<AssociativeLinkResultDto>(s_resultJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>家族 wire 字面量 → 中文标签。</summary>
    public static string FamilyLabel(string family) =>
        family switch
        {
            "entity" => "实体锚",
            "bridge" => "强关联",
            "scene" => "场景属性",
            "horizon" => "前瞻桥",
            _ => family,
        };

    /// <summary>前瞻通道 wire 字面量 → 中文标签。</summary>
    public static string ChannelLabel(string channel) =>
        channel switch
        {
            "personal-arc" => "角色弧光",
            "avoidance-habit" => "回避/警戒",
            "goal-arc" => "目标弧",
            "belief-in-action" => "信念在行动",
            "legacy-anchor" => "遗留锚（契诃夫之枪）",
            _ => channel,
        };
}
