using System.Text;
using System.Text.Json;
using Core.Data;

namespace Core.Vector;

/// <summary>
/// 文档构建器：将实体转换为 RetrievalDocument + RetrievalChunk 列表。
/// 每个实体的有效文本、字段优先级和 metadata 不同，不使用反射/泛型自动化。
///
/// 索引文本原则：
/// - 用可读标签建立字段语义（"角色""动机""弱点"），避免仅用值拼接
/// - 在相关实体摘要中显示名称，而非只有 ID；ID 留在 metadata 过滤
/// - 不将未经验证的 AI 推断写入事实实体索引
/// - 空字段不拼入，避免噪声淹没实体语义
/// </summary>
public sealed class DocumentBuilder
{
    /// <summary>中文文本分块器，用于章节正文语义分块。</summary>
    private readonly ChineseTextChunker _chunker = new();

    /// <summary>
    /// 构建角色档案文档。
    /// 补齐当前漏掉的 Tags、Abilities、Weakness、Habits、Status、组织名称。
    /// </summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildCharacter(
        Character character,
        string? factionName = null
    )
    {
        var sb = AcquireStringBuilder(512);
        sb.Append("角色: ").AppendLine(character.Name);

        if (!string.IsNullOrEmpty(character.Gender) && character.Gender != "未设定")
            sb.Append("性别: ").AppendLine(character.Gender);
        if (!string.IsNullOrEmpty(character.Age) && character.Age != "未设定")
            sb.Append("年龄: ").AppendLine(character.Age);

        sb.Append("定位: ").AppendLine(ToDisplayString(character.Role));

        AppendIfNotEmpty(sb, "性格: ", character.Personality);
        AppendIfNotEmpty(sb, "外貌: ", character.Appearance);
        AppendIfNotEmpty(sb, "背景: ", character.Background);
        AppendIfNotEmpty(sb, "动机: ", character.Motivation);
        AppendIfNotEmpty(sb, "能力: ", character.Abilities);
        AppendIfNotEmpty(sb, "弱点: ", character.Weakness);
        AppendIfNotEmpty(sb, "习惯: ", character.Habits);
        if (character.Tags.Count > 0)
            sb.Append("标签: ").AppendLine(string.Join(", ", character.Tags));
        AppendIfNotEmpty(sb, "组织: ", factionName);

        sb.Append("状态: ").AppendLine(ToDisplayString(character.Status));

        var text = sb.ToString().TrimEnd();
        var metadata = BuildCharacterMetadata(character, factionName);
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.CharacterProfile,
            novelId: character.NovelId,
            sourceEntityType: "Character",
            sourceEntityId: character.Id,
            sourceRevision: character.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>
    /// 构建章节摘要文档（标题、序号、状态）。
    /// </summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildChapterSummary(Chapter chapter)
    {
        var text = $"第{chapter.Order}章: {chapter.Title}";
        if (!string.IsNullOrWhiteSpace(chapter.Summary))
            text += $"\n摘要: {chapter.Summary}";
        text += $"\n状态: {ToDisplayString(chapter.Status)}";
        var metadata = new Dictionary<string, object?>
        {
            ["chapter-order"] = chapter.Order,
            ["chapter-id"] = chapter.Id,
            ["chapter-title"] = chapter.Title,
            ["entity-status"] = ToDisplayString(chapter.Status),
            ["chapter-summary"] = chapter.Summary,
            ["chapter-plotline-ids"] = string.Join(",", chapter.PlotlineIds),
            ["chapter-character-ids"] = string.Join(",", chapter.CharacterIds),
        };

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.ChapterSummary,
            novelId: chapter.NovelId,
            sourceEntityType: "Chapter",
            sourceEntityId: chapter.Id,
            sourceRevision: chapter.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>
    /// 构建章节正文语义块文档（全文 300 字分块）。
    /// </summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildChapterBodyChunks(
        Chapter chapter
    )
    {
        if (string.IsNullOrWhiteSpace(chapter.Content))
        {
            // 空内容不建块
            return (
                CreateDocument(
                    domain: RetrievalDomain.NovelKnowledge,
                    kind: DocumentKind.ChapterBodyChunk,
                    novelId: chapter.NovelId,
                    sourceEntityType: "Chapter",
                    sourceEntityId: chapter.Id,
                    sourceRevision: chapter.UpdatedAt.Ticks,
                    text: "",
                    metadata: BuildChapterMetadata(chapter)
                ),
                []
            );
        }

        var textChunks = _chunker.Chunk(chapter.Content);
        var metadata = BuildChapterMetadata(chapter);

        var doc = CreateDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.ChapterBodyChunk,
            novelId: chapter.NovelId,
            sourceEntityType: "Chapter",
            sourceEntityId: chapter.Id,
            sourceRevision: chapter.UpdatedAt.Ticks,
            text: chapter.Content,
            metadata: metadata
        );

        var chunks = new List<RetrievalChunk>();
        for (var i = 0; i < textChunks.Count; i++)
        {
            var tc = textChunks[i];
            var chunkId = StableId.ChunkId(doc.DocumentId, i);
            tc.ChunkId = chunkId;

            chunks.Add(
                new RetrievalChunk
                {
                    ChunkId = chunkId,
                    DocumentId = doc.DocumentId,
                    ChunkOrdinal = i,
                    Text = tc.Text,
                    SourceOffsetStart = tc.SourceOffsetStart,
                    SourceOffsetEnd = tc.SourceOffsetEnd,
                    ContentHash = StableId.ContentHash(tc.Text),
                    BoundaryQuality = tc.BoundaryQuality,
                    PrevChunkId = i > 0 ? StableId.ChunkId(doc.DocumentId, i - 1) : null,
                    NextChunkId =
                        i < textChunks.Count - 1 ? StableId.ChunkId(doc.DocumentId, i + 1) : null,
                }
            );
        }

        // 更新相邻块 ID
        for (var i = 0; i < chunks.Count; i++)
        {
            textChunks[i].PrevChunkId = chunks[i].PrevChunkId;
            textChunks[i].NextChunkId = chunks[i].NextChunkId;
        }

        return (doc, chunks);
    }

    /// <summary>
    /// 构建场景定义文档。
    /// 包含冲突、目标、情绪弧、时间、场景类型、地点名称、参与角色名称、推进的情节线名称。
    /// </summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildSceneDefinition(
        SceneDefinition scene,
        string? locationName = null,
        List<string>? participantNames = null,
        List<string>? plotlineNames = null,
        int? chapterOrder = null,
        string? chapterTitle = null
    )
    {
        var sb = AcquireStringBuilder(512);
        sb.Append("场景: ").AppendLine(scene.Title);

        AppendIfNotEmpty(sb, "冲突: ", scene.Conflict);
        AppendIfNotEmpty(sb, "目标: ", scene.Goal);
        AppendIfNotEmpty(sb, "情绪弧: ", scene.EmotionalArc);
        AppendIfNotEmpty(sb, "时间: ", scene.TimeDescription);
        AppendIfNotEmpty(sb, "地点: ", locationName);
        if (participantNames is { Count: > 0 })
            sb.Append("参与角色: ").AppendLine(string.Join(", ", participantNames));
        if (plotlineNames is { Count: > 0 })
            sb.Append("情节线: ").AppendLine(string.Join(", ", plotlineNames));
        AppendIfNotEmpty(sb, "备注: ", scene.Notes);

        sb.Append("场景类型: ").AppendLine(ToDisplayString(scene.SceneType));

        var text = sb.ToString().TrimEnd();
        var metadata = BuildSceneMetadata(scene, locationName, chapterOrder, chapterTitle);
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.SceneDefinition,
            novelId: scene.NovelId,
            sourceEntityType: "SceneDefinition",
            sourceEntityId: scene.Id,
            sourceRevision: scene.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>构建情节线档案。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildPlotline(Plotline plotline)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"情节线: {plotline.Name}");
        sb.AppendLine($"类型: {ToDisplayString(plotline.Type)}");
        if (!string.IsNullOrEmpty(plotline.Description))
            sb.AppendLine($"描述: {plotline.Description}");
        sb.AppendLine($"紧张度: {ToDisplayString(plotline.Tension)}");
        if (!string.IsNullOrEmpty(plotline.Turnpoints))
            sb.AppendLine($"关键转折: {plotline.Turnpoints}");

        var text = sb.ToString().TrimEnd();
        var metadata = new Dictionary<string, object?> { ["entity-status"] = "active" };

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.PlotlineProfile,
            novelId: plotline.NovelId,
            sourceEntityType: "Plotline",
            sourceEntityId: plotline.Id,
            sourceRevision: plotline.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>构建地点档案。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildLocation(Location location)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"地点: {location.Name}");
        sb.AppendLine($"类型: {ToDisplayString(location.Type)}");
        if (!string.IsNullOrEmpty(location.Atmosphere))
            sb.AppendLine($"氛围: {location.Atmosphere}");
        if (!string.IsNullOrEmpty(location.Description))
            sb.AppendLine($"描述: {location.Description}");

        var text = sb.ToString().TrimEnd();
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.LocationProfile,
            novelId: location.NovelId,
            sourceEntityType: "Location",
            sourceEntityId: location.Id,
            sourceRevision: location.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    /// <summary>构建组织档案。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildFaction(Faction faction)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"组织: {faction.Name}");
        sb.AppendLine($"类型: {ToDisplayString(faction.Type)}");
        if (!string.IsNullOrEmpty(faction.Description))
            sb.AppendLine($"描述: {faction.Description}");
        if (!string.IsNullOrEmpty(faction.Leader))
            sb.AppendLine($"领袖: {faction.Leader}");
        if (!string.IsNullOrEmpty(faction.Goal))
            sb.AppendLine($"目标: {faction.Goal}");

        var text = sb.ToString().TrimEnd();
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.FactionProfile,
            novelId: faction.NovelId,
            sourceEntityType: "Faction",
            sourceEntityId: faction.Id,
            sourceRevision: faction.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    /// <summary>构建道具档案。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildItem(
        Item item,
        string? ownerName = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"道具: {item.Name}");
        sb.AppendLine($"类型: {ToDisplayString(item.Type)}");
        if (!string.IsNullOrEmpty(item.Description))
            sb.AppendLine($"描述: {item.Description}");
        if (!string.IsNullOrEmpty(ownerName))
            sb.AppendLine($"持有者: {ownerName}");

        var text = sb.ToString().TrimEnd();
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.ItemProfile,
            novelId: item.NovelId,
            sourceEntityType: "Item",
            sourceEntityId: item.Id,
            sourceRevision: item.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    /// <summary>构建时间线事件。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildTimelineEvent(
        TimelineEvent evt,
        int? chapterOrder = null
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"事件: {evt.Title}");
        if (!string.IsNullOrEmpty(evt.Description))
            sb.AppendLine($"描述: {evt.Description}");

        var text = sb.ToString().TrimEnd();
        var metadata = new Dictionary<string, object?>();
        if (evt.ChapterId is not null)
        {
            metadata["chapter-id"] = evt.ChapterId;
        }
        if (chapterOrder is not null)
        {
            metadata["chapter-order"] = chapterOrder;
        }

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.TimelineEvent,
            novelId: evt.NovelId,
            sourceEntityType: "TimelineEvent",
            sourceEntityId: evt.Id,
            sourceRevision: evt.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>构建作者随手记（单块：标题 + 正文 + 标签 + 状态）。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildAuthorNote(AuthorNote note)
    {
        var sb = new StringBuilder();
        sb.Append("笔记: ").AppendLine(note.Title);
        sb.AppendLine(note.Body);
        if (note.Tags.Count > 0)
            sb.Append("标签: ").AppendLine(string.Join("、", note.Tags));
        sb.Append("状态: ").AppendLine(note.Status.ToString());

        var text = sb.ToString().TrimEnd();
        var metadata = new Dictionary<string, object?>
        {
            // 过滤值与 EntityStatuses 口径一致：枚举的 PascalCase 名
            ["entity-status"] = note.Status.ToString(),
            ["note-title"] = note.Title,
        };
        if (note.Tags.Count > 0)
            metadata["note-tags"] = string.Join(",", note.Tags);
        if (note.CapturedChapterOrder is { } order)
            metadata["captured-chapter-order"] = order;
        if (note.SessionId.Length > 0)
            metadata["note-session-id"] = note.SessionId;

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.AuthorNote,
            novelId: note.NovelId,
            sourceEntityType: "Note",
            sourceEntityId: note.Id,
            sourceRevision: note.UpdatedAt.Ticks,
            text: text,
            metadata: metadata
        );
    }

    /// <summary>构建角色关系陈述。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildRelationship(
        Relationship rel,
        string? nameA = null,
        string? nameB = null
    )
    {
        var text =
            $"关系: {nameA ?? rel.CharacterAId} 与 {nameB ?? rel.CharacterBId} — {ToDisplayString(rel.Type)}（强度: {ToDisplayString(rel.Strength)}）";
        if (!string.IsNullOrEmpty(rel.Description))
            text += $"\n描述: {rel.Description}";

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.RelationshipStatement,
            novelId: rel.NovelId,
            sourceEntityType: "Relationship",
            sourceEntityId: rel.Id,
            sourceRevision: rel.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    /// <summary>构建组织关系陈述。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildFactionRelationship(
        FactionRelationship rel,
        string? nameA = null,
        string? nameB = null
    )
    {
        var text =
            $"组织关系: {nameA ?? rel.FactionAId} 与 {nameB ?? rel.FactionBId} — {ToDisplayString(rel.Type)}";
        if (!string.IsNullOrEmpty(rel.Description))
            text += $"\n描述: {rel.Description}";

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.FactionRelationshipStatement,
            novelId: rel.NovelId,
            sourceEntityType: "FactionRelationship",
            sourceEntityId: rel.Id,
            sourceRevision: rel.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    /// <summary>构建小说概要。</summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildNovelProfile(Novel novel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"小说: {novel.Title}");
        if (!string.IsNullOrEmpty(novel.Genre))
            sb.AppendLine($"类型: {novel.Genre}");
        if (!string.IsNullOrEmpty(novel.WritingStyle))
            sb.AppendLine($"写作风格: {novel.WritingStyle}");
        if (!string.IsNullOrEmpty(novel.Description))
            sb.AppendLine($"描述: {novel.Description}");
        if (!string.IsNullOrEmpty(novel.TargetAudience))
            sb.AppendLine($"目标读者: {novel.TargetAudience}");

        var text = sb.ToString().TrimEnd();
        return BuildSingleChunkDocument(
            domain: RetrievalDomain.NovelKnowledge,
            kind: DocumentKind.NovelProfile,
            novelId: novel.Id,
            sourceEntityType: "Novel",
            sourceEntityId: novel.Id,
            sourceRevision: novel.UpdatedAt.Ticks,
            text: text,
            metadata: new Dictionary<string, object?> { ["entity-status"] = "active" }
        );
    }

    // ── 内部辅助 ──

    /// <summary>
    /// 构建会话历史条目文档（接续换页的检索通道）。
    ///
    /// 粒度是「单条消息」而非「整页」：语义命中的 SourceEntityId 必须能直接映射回
    /// history_* 输出里的 [id]，模型才能用 history_read_item 读回原文。
    /// </summary>
    public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildSessionItem(
        string? novelId,
        string windowId,
        string itemId,
        string role,
        string? toolName,
        string text
    )
    {
        var header =
            $"会话历史条目（{role}{(string.IsNullOrEmpty(toolName) ? "" : $"/{toolName}")}）";
        var body =
            $"窗口: {windowId}\n角色: {role}"
            + (string.IsNullOrEmpty(toolName) ? "" : $"\n工具: {toolName}")
            + $"\n内容: {text}";
        var metadata = new Dictionary<string, object?>
        {
            ["window-id"] = windowId,
            ["item-id"] = itemId,
            ["role"] = role,
            ["tool-name"] = toolName,
            ["header"] = header,
        };

        return BuildSingleChunkDocument(
            domain: RetrievalDomain.ConversationHistory,
            kind: DocumentKind.SessionWindow,
            novelId: novelId,
            sourceEntityType: "session_item",
            sourceEntityId: itemId,
            sourceRevision: 0,
            text: body,
            metadata: metadata
        );
    }

    private (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildSingleChunkDocument(
        RetrievalDomain domain,
        DocumentKind kind,
        string? novelId,
        string sourceEntityType,
        string sourceEntityId,
        long sourceRevision,
        string text,
        Dictionary<string, object?> metadata
    )
    {
        var doc = CreateDocument(
            domain,
            kind,
            novelId,
            sourceEntityType,
            sourceEntityId,
            sourceRevision,
            text,
            metadata
        );

        if (string.IsNullOrWhiteSpace(text))
            return (doc, []);

        var chunkId = StableId.ChunkId(doc.DocumentId, 0);
        var chunk = new RetrievalChunk
        {
            ChunkId = chunkId,
            DocumentId = doc.DocumentId,
            ChunkOrdinal = 0,
            Text = text,
            SourceOffsetStart = 0,
            SourceOffsetEnd = text.Length,
            ContentHash = StableId.ContentHash(text),
            BoundaryQuality = "heading",
        };

        return (doc, [chunk]);
    }

    private RetrievalDocument CreateDocument(
        RetrievalDomain domain,
        DocumentKind kind,
        string? novelId,
        string sourceEntityType,
        string sourceEntityId,
        long sourceRevision,
        string text,
        Dictionary<string, object?> metadata
    )
    {
        var documentId = StableId.DocumentId(
            domain,
            kind,
            novelId,
            sourceEntityType,
            sourceEntityId
        );
        var contentHash = StableId.ContentHash(text);

        return new RetrievalDocument
        {
            DocumentId = documentId,
            Domain = domain,
            NovelId = novelId,
            DocumentKind = kind,
            SourceEntityType = sourceEntityType,
            SourceEntityId = sourceEntityId,
            SourceRevision = sourceRevision,
            ContentHash = contentHash,
            IndexStatus = IndexStatus.Pending,
            MetadataJson = JsonSerializer.Serialize(metadata),
        };
    }

    private static Dictionary<string, object?> BuildCharacterMetadata(
        Character character,
        string? factionName
    )
    {
        var metadata = new Dictionary<string, object?>
        {
            ["entity-status"] = ToDisplayString(character.Status),
        };
        if (character.FactionId is not null)
        {
            metadata["faction-id"] = character.FactionId;
            if (factionName is not null)
                metadata["faction-name"] = factionName;
        }
        if (character.Tags.Count > 0)
            metadata["tags"] = character.Tags;
        return metadata;
    }

    private static Dictionary<string, object?> BuildChapterMetadata(Chapter chapter)
    {
        return new Dictionary<string, object?>
        {
            ["chapter-order"] = chapter.Order,
            ["chapter-id"] = chapter.Id,
            ["chapter-title"] = chapter.Title,
            ["entity-status"] = ToDisplayString(chapter.Status),
        };
    }

    private static Dictionary<string, object?> BuildSceneMetadata(
        SceneDefinition scene,
        string? locationName,
        int? chapterOrder,
        string? chapterTitle
    )
    {
        var metadata = new Dictionary<string, object?>
        {
            ["scene-type"] = ToDisplayString(scene.SceneType),
            ["entity-status"] = "active",
        };
        if (scene.ChapterId is not null)
        {
            metadata["chapter-id"] = scene.ChapterId;
        }
        if (chapterOrder is not null)
        {
            metadata["chapter-order"] = chapterOrder;
        }
        if (chapterTitle is not null)
        {
            metadata["chapter-title"] = chapterTitle;
        }
        if (scene.LocationId is not null)
        {
            metadata["location-id"] = scene.LocationId;
            if (locationName is not null)
                metadata["location-name"] = locationName;
        }
        if (scene.ParticipantIds.Count > 0)
        {
            metadata["participant-ids"] = scene.ParticipantIds;
        }
        if (scene.PlotlineIds.Count > 0)
        {
            metadata["plotline-ids"] = scene.PlotlineIds;
        }
        return metadata;
    }

    /// <summary>枚举转可读字符串。</summary>
    private static string ToDisplayString<T>(T value)
        where T : struct, Enum => value.ToString();

    private static StringBuilder AcquireStringBuilder(int capacity = 256) => new(capacity);

    /// <summary>
    /// 如果值非空，将标签和值追加到 StringBuilder。
    /// 避免重复的 if (!string.IsNullOrEmpty) 模式，消除字符串插值分配。
    /// </summary>
    private static void AppendIfNotEmpty(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            sb.Append(label).AppendLine(value);
    }
}
