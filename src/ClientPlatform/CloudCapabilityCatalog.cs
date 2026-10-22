using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// 能力描述符（GET /v1/capabilities 的 capabilities[] 元素，服务端 kebab-case 载荷）。
/// 只声明客户端**实际消费**的字段：supported-depths 决定可请求深度、
/// scope 决定可否发送跨章窗口、category/workflow-phase 供分类消费与诊断展示。
/// 未知键忽略、缺失键回退默认值（旧服务端零破坏，与 CloudContract 同一容忍约定）。
/// </summary>
/// <param name="Id">能力 ID（如 fiction.story.check）。</param>
/// <param name="Category">能力分类（context/planning/review/setup/grounding/memory）。</param>
/// <param name="WorkflowPhase">工作流阶段（pre-writing/writing/review/memory）。</param>
/// <param name="Scope">数据范围（target-chapter/chapter-window/setup-graph/book-corpus）。</param>
/// <param name="GraphBacked">该能力是否以服务端图谱（云端图谱表）为数据源。</param>
/// <param name="OutputSections">响应可出现的 section 键（客户端按 key 读时的白名单参考）。</param>
/// <param name="SupportedDepths">服务端接受的深度集合（quick/standard/deep）。</param>
/// <param name="Availability">可用性档位（当前统一 free）。</param>
/// <param name="MaxCharacters">单次正文字数上限。</param>
public sealed record CapabilityDescriptorDto(
    string Id,
    string? Category = null,
    string? WorkflowPhase = null,
    string? Scope = null,
    bool GraphBacked = false,
    IReadOnlyList<string>? OutputSections = null,
    IReadOnlyList<string>? SupportedDepths = null,
    string? Availability = null,
    int MaxCharacters = 0
);

/// <summary>
/// /v1/capabilities 快照 — 把服务端**声明的契约**（分类/阶段/范围/深度/图后端）变成客户端
/// 可查询的决策依据，替代「按硬编码假设发请求」。
///
/// 容忍约定（与 <see cref="CloudContract"/> 一致）：非 JSON / 根非对象 / capabilities 缺失 → <see cref="Parse"/>
/// 返回 null（= 无数据，调用方 fail-open 保持既有行为）；畸形条目（无 id）丢弃；
/// 字段缺失或为 null → 回退默认值。**空数组是有效声明**（明确「无任何能力」），与「无数据」含义不同。
/// </summary>
public sealed record CapabilityCatalogSnapshot(
    string? ContractVersion,
    IReadOnlyList<CapabilityDescriptorDto> Capabilities
)
{
    /// <summary>解析探活/发现响应体。无法判定（非 JSON、根非对象、capabilities 字段缺失）→ null。</summary>
    public static CapabilityCatalogSnapshot? Parse(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("capabilities", out var capabilities))
                return null;
            if (capabilities.ValueKind != JsonValueKind.Array)
                return null;

            var parsed = new List<CapabilityDescriptorDto>();
            foreach (var item in capabilities.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue; // null / 标量条目：跳过
                CapabilityDescriptorDto? dto;
                try
                {
                    dto = item.Deserialize<CapabilityDescriptorDto>(ClientJsonDefaults.Cloud);
                }
                catch (JsonException)
                {
                    continue; // 畸形条目（缺 id 等）丢弃，不影响其余能力
                }
                if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
                    continue;
                parsed.Add(dto);
            }

            var contractVersion =
                root.TryGetProperty("contract-version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
            return new CapabilityCatalogSnapshot(contractVersion, parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按 ID 取描述符（未声明 → null）。</summary>
    public CapabilityDescriptorDto? Find(string capabilityId) =>
        Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal)
        );

    /// <summary>服务端是否声明了该能力（未声明 = 调用必然被拒 → 调用方可跳过）。</summary>
    public bool IsAdvertised(string capabilityId) => Find(capabilityId) is not null;

    /// <summary>
    /// 服务端是否接受该深度。fail-open：能力未声明、supported-depths 缺失/为空 → true（无数据不降级）。
    /// </summary>
    public bool SupportsDepth(string capabilityId, string depth)
    {
        var descriptor = Find(capabilityId);
        if (descriptor?.SupportedDepths is not { Count: > 0 } depths)
            return true;
        return depths.Contains(depth, StringComparer.Ordinal);
    }

    /// <summary>
    /// 服务端对该能力声明的数据范围是否包含 <paramref name="scope"/>。
    /// fail-open：能力未声明、scope 字段缺失 → true；声明了则必须精确匹配。
    /// </summary>
    public bool AllowsScope(string capabilityId, string scope)
    {
        var descriptor = Find(capabilityId);
        if (string.IsNullOrEmpty(descriptor?.Scope))
            return true;
        return string.Equals(descriptor.Scope, scope, StringComparison.Ordinal);
    }
}

/// <summary>
/// 最近一次发现快照的持有者（DI 单例）——探活（Desktop CloudAuthService / TUI CloudConnectionService）
/// 成功后写入，消费方（DiagnosisScheduler）按需读取。
///
/// 语义：**last-known-good**。探活失败/离线不清空（旧快照仍是最可信的服务端声明）；
/// 未探活过时为 null → 消费方 fail-open（保持无目录时的既有行为）。
/// 线程安全：引用原子读写；快照本身不可变。
/// </summary>
public sealed class CloudCapabilityCatalogStore
{
    private CapabilityCatalogSnapshot? _current;

    /// <summary>当前快照（null = 尚无数据）。</summary>
    public CapabilityCatalogSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>写入新快照（null 忽略，保持 last-known-good）。</summary>
    public void Update(CapabilityCatalogSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        Volatile.Write(ref _current, snapshot);
    }
}
