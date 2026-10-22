using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Shared;

namespace Core.Data;

/// <summary>
/// JSONL 条目序列化选项（kebab-case 命名策略，全仓统一；历史 camelCase 数据由一次性迁移改写）。
/// 与 <see cref="JsonHelper.JsonOptions"/> 分离仅因为历史原因；两者现同为 kebab-case（全仓统一命名）。
/// </summary>
public static class JsonlSerializer
{
    /// <summary>
    /// JSONL 专用的序列化选项：kebab-case 属性命名 + kebab-case 枚举命名 + 忽略 null + UnsafeRelaxedJsonEscaping。
    /// 枚举使用 kebab-case（"branch-summary"）；历史 snake_case 值由一次性迁移改写。
    /// </summary>
    /// <summary>
    /// 遗留格式**只读**选项：历史会话文件为 camelCase + snake 枚举值。
    /// 仅用于一次性迁移器（001/002）与「旧格式兼容」读取路径——当前格式已是 kebab-case（见 <see cref="Options"/>）。
    /// </summary>
    public static readonly JsonSerializerOptions LegacyReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter<JsonlEntryType>(JsonNamingPolicy.SnakeCaseLower),
            new JsonStringEnumConverter<SessionEventType>(JsonNamingPolicy.SnakeCaseLower),
            new JsonStringEnumConverter(),
        },
    };

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter<JsonlEntryType>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<SessionEventType>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter(), // 其他枚举 fallback
        },
    };
}

// ═══════════════════════════════════════════════════════════
//  JSONL 写入侧 DTO（强类型替代 Dictionary&lt;string, object?&gt;）
// ═══════════════════════════════════════════════════════════

/// <summary>JSONL type=message 条目。</summary>
public sealed class JsonlMessageEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Message;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? ReasoningContent { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public bool? IsError { get; init; }
    public int Order { get; init; }

    /// <summary>toolCalls 的 JSON 字符串（已序列化的数组）。</summary>
    public string? ToolCalls { get; init; }

    /// <summary>
    /// 该 assistant 消息对应 API 调用时的完整上下文 token 数（Prompt+Completion）。
    /// 会话恢复时作为精确锚点：锚点之前的消息不再启发式估算，锚点之后的消息才估算。
    /// 仅最后一条 assistant 消息携带，旧数据缺失时自动降级为全量估算。
    /// </summary>
    public int? ContextTokens { get; init; }
}

/// <summary>JSONL type=compact 条目。</summary>
public sealed class JsonlCompactEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Compact;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? FirstKeptEntryId { get; init; }

    /// <summary>v4 字段：压缩前消息数。</summary>
    public int? MessagesBefore { get; init; }
    public int Order { get; init; }
    public string? Reason { get; init; }
    public bool? WillRetry { get; init; }
    public int? EstimatedTokensAfter { get; init; }

    /// <summary>被压缩的原始消息 entryId 列表（可逆：decompress 从 JSONL 恢复原文）。</summary>
    public List<string> CompressedMessageIds { get; init; } = [];

    /// <summary>
    /// 压缩块 ID（"b1", "b2"...），= 分片 seq（compact 时 newSeq = ActiveSeq+1）。
    /// 分片 seq 全局递增且不重用，blockId 天然唯一；缺失时（迁移前旧数据）decompress 返回"无法定位"。
    /// </summary>
    public string? BlockId { get; init; }
}

/// <summary>JSONL type=window 条目：一页上下文的起点（不携带任何历史摘要）。</summary>
public sealed class JsonlWindowEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Window;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";

    /// <summary>本页 ID，规则 w{分片 seq}（与 compact 的 b{seq} 同源）。首页 w0 为隐式页，不落条目。</summary>
    public string WindowId { get; init; } = "";

    /// <summary>上一页 ID；首页之后的第一页为 "w0"。</summary>
    public string? PrevWindowId { get; init; }

    /// <summary>会话首页 ID，恒为 "w0"。</summary>
    public string FirstWindowId { get; init; } = "w0";

    /// <summary>触发原因：model | overflow | threshold。</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 模型在 new_context 时写的交接说明（做到哪/下一步/未完成项）。
    /// 落盘以便重启或恢复会话后新页仍能看到交接（运行期由 ContextWindowState.HandoffNote 承载）。
    /// </summary>
    public string? Note { get; init; }

    public int Order { get; init; }
}

/// <summary>JSONL type=event 条目。</summary>
public sealed class JsonlEventEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Event;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public SessionEventType EventType { get; init; } = SessionEventType.Interrupt;
    public int Order { get; init; }
    public string? ErrorCode { get; init; }
    public string? Content { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public int? SelectedVariantIndex { get; init; }
    public string? SelectedLabel { get; init; }
}

/// <summary>JSONL type=leaf 条目。</summary>
public sealed class JsonlLeafEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Leaf;
    public string TargetId { get; init; } = "";
}

/// <summary>JSONL type=label 条目。</summary>
public sealed class JsonlLabelEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Label;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public string? TargetId { get; init; }
    public string? Label { get; init; }
    public int Order { get; init; }
}

/// <summary>JSONL type=branch_summary 条目。</summary>
public sealed class JsonlBranchSummaryEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.BranchSummary;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public string? FromId { get; init; }
    public string Summary { get; init; } = "";
    public int Order { get; init; }
}

// ═══════════════════════════════════════════════════════════
//  JSONL 读取侧 DTO（用于反序列化任意类型行）
// ═══════════════════════════════════════════════════════════

/// <summary>
/// JSONL 行的最小公共结构，仅用于判断 type 字段后分发到具体 DTO。
/// </summary>
public sealed class JsonlTypeProbe
{
    public JsonlEntryType? Type { get; init; }
}

/// <summary>输入历史条目（独立 JSONL 文件）。</summary>
public sealed class JsonlInputHistoryEntry
{
    public string Text { get; init; } = "";
    public string Timestamp { get; init; } = "";
}

/// <summary>
/// toolCalls 数组中的单项 DTO，供 RestoreToolCallsFromJson 强类型反序列化。
/// 显式标注 [JsonPropertyName] 以保证在 kebab-case (JsonlSerializer.Options) 和
/// 其它序列化配置下都能正确反序列化。
/// 单字段属性名在两种命名策略下一致，显式标注增加了明确性和防御性。
/// </summary>
public sealed class ToolCallItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    /// <summary>
    /// 从 ToolCallsJson JSON 字符串反序列化为 <see cref="ToolCallItemDto"/> 列表。
    /// 统一序列化选项（JsonlSerializer.Options），消除散落在两处的重复实现。
    /// 返回 null 表示序列化失败或输入为空。
    /// </summary>
    public static List<ToolCallItemDto>? DeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<ToolCallItemDto>>(json, JsonlSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 审计日志 JSONL 条目 DTO（强类型替代 Dictionary&lt;string, object?&gt;）。
/// 写入侧（WriteAuditLogs）和读取侧（GetEntityAuditLog）统一使用此 DTO + JsonHelper.JsonOptions。
/// 属性使用 kebab-case 命名（与 JsonHelper.JsonOptions 的 PropertyNamingPolicy 一致），
/// 显式标注 [JsonPropertyName] 以保证在两种序列化配置下都能正确工作。
/// </summary>
public sealed class JsonlAuditEntry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "entity_change";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "";

    [JsonPropertyName("novel-id")]
    public string NovelId { get; init; } = "";

    [JsonPropertyName("entity-type")]
    public string EntityType { get; init; } = "";

    [JsonPropertyName("entity-id")]
    public string EntityId { get; init; } = "";

    [JsonPropertyName("action")]
    public string Action { get; init; } = "create";

    [JsonPropertyName("field-name")]
    public string? FieldName { get; init; }

    [JsonPropertyName("old-value")]
    public string? OldValue { get; init; }

    [JsonPropertyName("new-value")]
    public string? NewValue { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "ai";

    [JsonPropertyName("tool-name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("session-id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// 本体动作日志 JSONL 条目 DTO（追加式 ontology/actions.jsonl）。
/// 属性使用 kebab-case（显式标注，避免序列化配置漂移）；枚举值小写。
/// </summary>
public sealed class JsonlOntologyAction
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ontology_action";

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "";

    [JsonPropertyName("novel-id")]
    public string NovelId { get; init; } = "";

    [JsonPropertyName("element")]
    public string Element { get; init; } = "object";

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "update";

    [JsonPropertyName("entity-type")]
    public string EntityType { get; init; } = "";

    [JsonPropertyName("entity-id")]
    public string EntityId { get; init; } = "";

    [JsonPropertyName("entity-name")]
    public string EntityName { get; init; } = "";

    [JsonPropertyName("field")]
    public string? Field { get; init; }

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonPropertyName("chapter-id")]
    public string? ChapterId { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }

    [JsonPropertyName("proposal-id")]
    public string? ProposalId { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "system";

    [JsonPropertyName("tool-name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("session-id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("change-set-id")]
    public string? ChangeSetId { get; init; }

    [JsonPropertyName("location-id")]
    public string? LocationId { get; init; }

    [JsonPropertyName("scene-id")]
    public string? SceneId { get; init; }

    [JsonPropertyName("plotline-ids")]
    public List<string> PlotlineIds { get; init; } = [];

    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("is-cascade")]
    public bool IsCascade { get; init; }

    [JsonPropertyName("source-change-id")]
    public string? SourceChangeId { get; init; }

    /// <summary>模型 → JSONL DTO。</summary>
    public static JsonlOntologyAction FromModel(OntologyAction action) =>
        new()
        {
            Id = action.Id,
            Timestamp = action.CreatedAt.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            NovelId = action.NovelId,
            Element = YamlHelper.EnumToYaml(action.Element),
            Operation = YamlHelper.EnumToYaml(action.Operation),
            EntityType = action.TargetEntityType.ToKebabCaseString(),
            EntityId = action.TargetId,
            EntityName = action.TargetName,
            Field = action.Field,
            From = action.From,
            To = action.To,
            Summary = action.Summary,
            ChapterId = action.ChapterId,
            Evidence = action.Evidence,
            ProposalId = action.ProposalId,
            Source = YamlHelper.EnumToYaml(action.Source),
            ToolName = action.ToolName,
            SessionId = action.SessionId,
            ChangeSetId = action.ChangeSetId,
            LocationId = action.LocationId,
            SceneId = action.SceneId,
            PlotlineIds = action.PlotlineIds,
            Sequence = action.Sequence,
            IsCascade = action.IsCascade,
            SourceChangeId = action.SourceChangeId,
        };

    /// <summary>JSONL DTO → 模型；未知 element/entity-type 或时间非法返回 null（该行跳过）。</summary>
    public OntologyAction? ToModel()
    {
        if (!Enum.TryParse<OntologyElement>(Element, ignoreCase: true, out var element))
            return null;
        if (!Enum.TryParse<OntologyOperation>(Operation, ignoreCase: true, out var operation))
            operation = OntologyOperation.Update;
        if (EntityTypeExtensions.ParseEntityType(EntityType) is not { } entityType)
            return null;
        if (!Enum.TryParse<AuditSource>(Source, ignoreCase: true, out var source))
            source = AuditSource.System;
        var createdAt = DateTime.TryParse(
            Timestamp,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed
        )
            ? parsed
            : DateTime.UtcNow;

        return new OntologyAction
        {
            Id = Id,
            NovelId = NovelId,
            Element = element,
            Operation = operation,
            TargetEntityType = entityType,
            TargetId = EntityId,
            TargetName = EntityName,
            Field = Field,
            From = From,
            To = To,
            Summary = Summary,
            ChapterId = ChapterId,
            Evidence = Evidence,
            ProposalId = ProposalId,
            Source = source,
            ToolName = ToolName,
            SessionId = SessionId,
            ChangeSetId = ChangeSetId,
            LocationId = LocationId,
            SceneId = SceneId,
            PlotlineIds = PlotlineIds,
            Sequence = Sequence,
            IsCascade = IsCascade,
            SourceChangeId = SourceChangeId,
            CreatedAt = createdAt,
        };
    }
}

// ═══════════════════════════════════════════════════════════
//  会话分片存储：MANIFEST DTO
// ═══════════════════════════════════════════════════════════

/// <summary>MANIFEST.json 中的单条分片记录。</summary>
public sealed record ManifestSegmentRecord
{
    public int Seq { get; init; }
    public string File { get; init; } = "";
    public int StartOrder { get; init; }
    public int EndOrder { get; init; }
    public int MessageCount { get; init; }
    public int EntryCount { get; init; }
    public long FileSize { get; init; }
    public string? CompactEntryId { get; init; }
    public bool HasCompact { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>该分片头部是否为 window 条目（接续换页）；WindowId 形如 "w3"。</summary>
    public string? WindowId { get; init; }
    public bool HasWindow { get; init; }
}

/// <summary>整个会话的 MANIFEST 元数据。</summary>
public sealed class SessionManifest
{
    public int Version { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public string NovelId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public List<ManifestSegmentRecord> Segments { get; init; } = [];
    public int ActiveSegment { get; init; }
    public int CompactCount { get; init; }

    /// <summary>会话累计换页次数（隐式首页 w0 不计入）。</summary>
    public int WindowCount { get; init; }
    public int TotalEntryCount { get; init; }
    public int TokenEstimate { get; init; }
}
