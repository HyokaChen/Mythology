# 大纲驱动的远期推演 + 作者随手记（Plot Forecast & Notes）

## Context

用户要求借鉴 T-Mem（EMNLP 2026，「写时预演」trigger 索引）在小说写作层结合**总纲 / 卷纲 / 细纲**做**远期发展预测**，并且必须把**作者随手记**一起做进去——随手记更随意、默认与本小说相关，也是一条预测线索；随手记不能只是 md 文件（md 可以 `@` 引用），要能通过 LLM 记录/查看，**并有 UI**。

现状（已核实）：

- T-Mem 的四象限 trigger（entity / bridge / scene / horizon）**已在云端实现**（`fiction.associative.link` + `AssociativeTriggerHook`），但只「向后」：索引已写内容，查询是刚写/将写的场景，**从不拿未来计划当查询**。
- 三层大纲：`Novel.Outline` / `Volume` / `Chapter.Summary+PlotlineIds+CharacterIds+Notes`。每轮只有 `NovelContextBlock` 注入总纲 200 字 + 卷纲 40 字/卷，**细纲不进上下文**；`TurnpointsBeatParser`（确定性正则，`src/Core/Cloud/TurnpointsBeatParser.cs:39-42`）能解析「第5章：真相揭露」，但本地无人消费。
- `Plotline.PlannedRevealChapterId` 的到期判定散落在云端诊断（写完之后）、`recall_foreshadowing`、`get_writing_stats`，彼此口径不同。
- **笔记功能不存在**：无笔记实体、无笔记目录、无笔记 UI；`Chapter.Notes` / `Volume.Notes` 是计划备注字段。`docs/memory-ops-loop-plan.md` 里的 `MemoryMinder` / `MemoryPromotionScanner` **只是计划文档，未实现**（全仓 grep 仅命中该文档）。

本计划交付一条闭合链路：**随手记（实体 + LLM 工具 + 会话自动捕获 + 双端窗口）→ 索引进同一混合检索内核（FTS/Bm25 + 向量 + RRF + Jev 判定重排）→ 远期推演把它作为线索注入写作/规划回合**；同时把大纲/细纲变成确定性的「到期义务 + 计划转折 + 播种缺口 + 风险」，并在写作/规划工具调用后以 `[远期推演]` 前缀静默注入 `Agent.InjectQueue`，另提供 `forecast_plot` 工具按需取证据。

完成后的可观测行为：写完第 N 章（或调 `get_outline` / `build_scene` / `edit_entities` 写细纲）后，下一轮 LLM 上下文出现 `[远期推演]` 块：下一章 + 未来 5 章细纲 + 计划转折 + 到期义务 + 播种缺口 + 风险 + **笔记线索**；作者说「记一下：…」或点「笔记」窗口新建后，笔记落 `notes/*.yaml`、进索引，并在确认后进入推演块；`/notes`（TUI）与命令面板「笔记」（Desktop）可查看/编辑/确认候选。

范围边界：不改云端仓库与 wire 契约；不加配置项；不加数据库表。随手记是新的**权威文件型实体**（YAML），索引仍是可重建派生数据。

## Approach

按 A→E 顺序实施；每步结束 `dotnet build src/Mythology.slnx` 通过、该步测试通过。

### A. 笔记数据面

#### 1. 实体与存储（`AuthorNote` 一等实体）

`src/Core/Data/Models.cs` 在 `TimelineEvent` 附近追加：

```csharp
/// <summary>笔记状态：收件箱 = 会话自动捕获的候选（不参与推演）；已确认/已落地/已废弃。</summary>
public enum NoteStatus { Inbox, Active, Done, Dropped }

/// <summary>作者随手记（书级自由文本；是远期推演的一条线索来源）。</summary>
public class AuthorNote : Entity
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public NoteStatus Status { get; set; } = NoteStatus.Active; // 手工/显式捕获默认已确认
    public int? CapturedChapterOrder { get; set; }              // 记下时的写作进度（无章则 null）
    public string SessionId { get; set; } = "";                 // 自动捕获来源会话；手工为空
    public AuditSource Source { get; set; } = AuditSource.User; // Ai = 工具/自动捕获
}
```

- `src/Core/Data/Enums.cs`：`EntityType` 追加 `Note`；`KebabCaseMap` 追加 `[EntityType.Note] = "note"`（`Enums.cs:630-645`）。
- `src/Core/Data/INovelStore.cs` 只加两个成员（笔记是软删除：废弃 = `Status=Dropped`，不删文件，便于追溯）：

```csharp
Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default);
Task SaveNote(AuthorNote note, CancellationToken ct = default);
```

- `src/Core/Data/FileNovelStore.cs`：仿 `SaveScene`（`FileNovelStore.cs:2749-2806`）实现——目录 `notes/`，落盘调用既有 `SaveEntityAsync(note.NovelId, "notes", $"{note.Title}-{note.Id}.yaml", note.Id, dict, EntityType.Note, note.Title)`（内部自动 `SanitizeFileName`，并触发 `FireEntityChanged`/`OnEntityChanged`/审计/本体 diff）。
  `GetNotes` **直接走 `GetEntitiesDirectAsync(novelId, "notes", ReadNoteFromDict)`，不要用 `GetEntitiesAsync`**：`GetEntitiesAsync` 在 Incremental/Shadow 模式会走 Tacos（`NovelSemanticService.GetFileNames`，`NovelSemanticService.cs:262-270`），而 Tacos 没有 "notes" 这个 kind，会恒返回空——按模式静默丢数据。读文件路径与 `GetStructure` 的 `ReadChapterHeadersAsync` 同源，永远正确。
  YAML 键：`id/novel-id/title/body/tags/status/captured-chapter-order/session-id/source/created-at/updated-at`；`tags` 用 `string.Join(",", Tags)`（与 `chapter.PlotlineIds` 同法），`status`/`source` 用 `YamlHelper.EnumToYaml`。
- 必须同步实现接口的 8 个测试替身（两个方法各自返回内存列表 / 写回内存）：`src/Host/Tests/MockNovelStore.cs`、`src/Tui/Tests/TuiSlashTestHarness.cs`（`FakeNovelStore`）、`src/Tui/Tests/SetUpPanelLayoutTests.cs`（`FakeStore`）、`src/Plugins/NovelTools/Tests/FakeNovelStore.cs`、`src/Plugins/SemanticRetrievalTool/Tests/FakeNovelStore.cs`、`src/Plugins/WritingTools/Tests/{EnhancementEngineCloudTests,WritingToolsPluginRegistrationTests,DiagnoseWritingToolCloudTests}.cs` 的 `StoreStub`。**`IPluginStore` / `PluginStore` 不改**（它是给外部插件的只读子集，笔记不出本机、也不对外部插件暴露）。
- 字段注册表：`src/Core/Data/EntityFieldRegistry.cs`

```csharp
public static readonly TabDef[] NoteTabs =
[
    new("笔记", [
        new("Title", "标题", FieldType.TextBox),
        new("Body", "正文", FieldType.TextArea),
        new("Tags", "标签", FieldType.FreeTag),
        new("Status", "状态", FieldType.Select, ["收件箱", "已确认", "已落地", "已废弃"], ["Inbox", "Active", "Done", "Dropped"]),
        new("CapturedChapterOrder", "记录于第N章", FieldType.ReadOnly),
    ]),
];
```

`GetTabs` 追加 `EntityType.Note => NoteTabs`；`Label` 追加 `EntityType.Note => "笔记"`（`EntityFieldRegistry.cs:389-425`）。桌面测试 `src/Desktop/Tests/EntityEditorFieldCoverageTests.cs:13-24` 的 `ModelTypes` 追加 `[EntityType.Note] = typeof(AuthorNote)`。

**双端编辑器类型派发（漏一个就会在新建/编辑笔记时抛）**：

- Desktop `src/Desktop/Controls/EntityEditWindow.axaml.cs`：`LoadEntityAsync`（`:498-510`）加 `EntityType.Note => (await _store.GetNotes(_novelId)).FirstOrDefault(n => n.Id == entityId)` 分支；`CreateEntity`（`:674-680`）加 `EntityType.Note => new AuthorNote { NovelId = _novelId }`；保存派发在 `src/Desktop/Controls/RelationWriteThrough.cs:215-229` `SaveEntityByTypeAsync` 加 `AuthorNote note => store.SaveNote(note)`。关联节对未知类型安全（`EditableSectionsFor` 的 `_ => []`、`EntityRelationIndex.GetRelations` 空集合），无需特判。
- TUI `src/Tui/Controls/EntityEditDialog.cs`：`Tabs`（`:1711-1730`）加 `EntityType.Note => NoteTabs`（在该文件内新建同样的 `TabDef[]`，与 Character/Plotline 等并列）；`LoadEntityAsync`（`:1759-1955`）加 Note 分支返回 `Title/Body/Tags/Status/CapturedChapterOrder`；`SaveEntityCoreAsync`（`:1960+`）加 Note 分支（`entityId` 非空则从 `store.GetNotes` 取回，否则新建；`Title/Body/Tags/Status` 覆盖后 `store.SaveNote`）。`Label`（`:1735-1749`）加 `EntityType.Note => "笔记"`。
`edit_entities` 工具**不接** Note（`src/Plugins/NovelTools/Tools/EditEntitiesTool.cs` 的类型 switch 保持不动）：笔记的写入口统一走第 4 步的 `manage_note`，避免两套写路径。

#### 2. 进检索索引（FTS/BM25 + 向量 + RRF + Jev 同一内核）

- `src/Core/Vector/RetrievalEnums.cs`：`DocumentKind.AuthorNote = 15`；`ToChineseLabel` 追加 `=> "笔记"`。
- `src/Core/Vector/DocumentBuilder.cs` 追加单块构建（仿 `BuildTimelineEvent`，`DocumentBuilder.cs:321-353`）：

```csharp
public (RetrievalDocument Doc, List<RetrievalChunk> Chunks) BuildAuthorNote(AuthorNote note)
```

文本：`笔记: {Title}\n{Body}`，非空时追加 `\n标签: {tags}`、`\n状态: {Status}`。
metadata：`["entity-status"] = note.Status.ToString()`（PascalCase，与 `EntityStatuses` 过滤值一致，见 `DocumentBuilder.cs:557` 的 Character 写法）、`["note-title"]`、`["note-tags"]`、`["captured-chapter-order"] = note.CapturedChapterOrder`、`["note-session-id"]`。
`sourceEntityType: "Note"`、`sourceEntityId: note.Id`、`sourceRevision: note.UpdatedAt.Ticks`、`kind: DocumentKind.AuthorNote`、`domain: RetrievalDomain.NovelKnowledge`。
- `src/Core/Data/VectorSyncService.cs`：仿 `SyncTimelineEventsAsync`（`:318-341`）加 `SyncAuthorNotesAsync`（`GetNotes` → `kernel.Builder.BuildAuthorNote` → `IsUpToDate` 跳过 → `IndexDocumentAsync`）；任务列表（`:76-90`）追加；孤儿清理登记（`:435-455`）追加 `currentIds["Note"] = notes.Select(n => n.Id)`（**必须登记，否则每次同步都会把笔记索引清掉**）。
- `src/Tui/TuiApp.Indexing.cs`：`CreateBuildFunc`（`:66-80`）加 `EntityType.Note` arm → `BuildAuthorNoteDocAsync`（仿 `BuildTimelineEventDocAsync` `:216-222`）；`EntityTypeToSourceType`（`:255-269`）加 `EntityType.Note => "Note"`。Desktop 本就没有实体变更接线，索引由工具/窗口显式写（见第 4、9 步）。

#### 3. 单元测试（Core.Tests）

`src/Core/Tests/AuthorNoteStoreTests.cs`：临时 `FileNovelStore` 下 Save→GetNotes 往返（含 Tags/Status/CapturedChapterOrder/Source）；改标题后旧文件消失、新文件存在（`SaveEntityAsync` 的 rename 语义）；`GetNotes` 按 `UpdatedAt` 倒序不需要强约束（测试只断言集合内容）。
`src/Core/Tests/AuthorNoteDocumentTests.cs`：`BuildAuthorNote` 的 text 含标题/正文/标签，metadata 的 `entity-status == "Active"`、`note-title`、`captured-chapter-order` 正确；`DocumentKind.AuthorNote` 的中文标签为「笔记」。

### B. 捕获面

#### 4. 两个工具（LLM 记录/查看）

新文件 `src/Plugins/NovelTools/Tools/NoteTools.cs`，两个工具类 + 两个参数/详情 record。`using System.ComponentModel; using Core.Agent; using Core.Data; using Core.Vector;`

**`capture_note`**（快速记一笔）：

```csharp
public sealed class CaptureNoteParams
{
    [Description("小说项目ID，留空则用当前打开的小说")] public string NovelId { get; init; } = "";
    [Description("标题（可空，空则取正文前 16 字）")] public string Title { get; init; } = "";
    [Description("笔记正文（作者原话，不要改写）")] public string Body { get; init; } = "";
    [Description("标签（可空）")] public string[] Tags { get; init; } = [];
}
public sealed record CaptureNoteDetails(string NoteId, int? CapturedChapterOrder);
```

- `Name => "capture_note"`；`Aliases => ["记笔记", "记一下"]`；`Label => "记笔记"`；`Category => ToolCategory.EntityCrud`；`EntityType => "note"`；`ExecutionMode => ToolExecutionMode.Sequential`；`DeferLoading => false`（作者一句话就要能命中）。
- `Description`（逐字）：`"把作者随口说的想法/设定/桥段记成一条笔记（默认已确认，参与远期推演）；查看、确认候选、修改状态用 manage_note。"`
- 逻辑：novelId 回退 `agent.CurrentNovelId`，空 → `Err("当前没有打开的小说项目，请提供 novel_id。")`；`Body` 去空白后为空 → `Err("正文不能为空。")`；`Title` 空则取 `Body` 前 16 字；`CapturedChapterOrder` = `store.GetStructure(novelId)` 中 `CharCount > 0` 的最大 `Order`（无则 null）；`Status = Active`、`Source = AuditSource.Ai`；`SaveNote`；kernel 非空时 `IndexDocumentAsync(kernel.Builder.BuildAuthorNote(note))`。返回文本 `已记笔记 [{id[..8]}]：{Title}`。
- `Ok/Err` 用工具私有静态助手（仓库惯例，见 `RecallAssociativeTool.cs:157-175`）：`Ok(string, CaptureNoteDetails)` / `Err(string)`（`IsError = true`）。

**`manage_note`**（列表/查看/改/状态流转）：

```csharp
public sealed class ManageNoteParams
{
    [Description("动作：list | get | update | confirm | done | drop")] public string Action { get; init; } = "list";
    [Description("小说项目ID，留空则用当前打开的小说")] public string NovelId { get; init; } = "";
    [Description("笔记 ID（get/update/confirm/done/drop 必填）")] public string NoteId { get; init; } = "";
    [Description("新标题（update）")] public string? Title { get; init; }
    [Description("新正文（update）")] public string? Body { get; init; }
    [Description("新标签（update，传空数组=清空）")] public string[]? Tags { get; init; }
    [Description("列表状态过滤：all | inbox | active | done | dropped（默认 all）")] public string StatusFilter { get; init; } = "all";
}
public sealed record ManageNoteDetails(int Total, int Returned, string? NoteId);
```

- `Name => "manage_note"`；`Aliases => ["笔记", "笔记列表"]`；`Label => "笔记管理"`；`Category => ToolCategory.EntityCrud`；`EntityType => "note"`；`ExecutionMode => Sequential`；`DeferLoading => true`。
- `list`：排序 = `Inbox` 优先，其后 `UpdatedAt` 倒序；过滤 `StatusFilter`；最多 50 行；每行 `- [{状态中文}]{Title}｜{Body 截 40}`（状态中文：收件箱/已确认/已落地/已废弃）；末尾追加 `共 {Total} 条（候选 {InboxN} 条需确认）`。无笔记 → `Ok("暂无笔记。", new(0,0,null))`。
- `get`：返回全文 + 状态 + 记录章 + 标签；找不到 → `Err("未找到该笔记。")`。
- `update`：仅当参数非 null 才覆盖 Title/Body/Tags；`Body` 传空串 → `Err("正文不能为空。")`。
- `confirm`：`Inbox → Active`；`done`：`Active|Inbox → Done`；`drop`：任意 → `Dropped`（**软删除**）。
- 所有写动作：`SaveNote` + kernel 非空时重建索引（状态变了，索引文档文本/状态 metadata 也要变）。
- 注册：`src/Plugins/NovelTools/NovelToolsPlugin.cs` 在「情节线」区尾部追加 `yield return new CaptureNoteTool(_store, _kernel, _agent);` 与 `yield return new ManageNoteTool(_store, _kernel, _agent);`。

#### 5. 会话自动捕获（候选，不自动进推演）

新文件 `src/Core/Notes/NoteCaptureScanner.cs`（`namespace Core.Notes;`），一次扫描只产出**候选**（`Status = Inbox`），由 UI/`manage_note confirm` 确认后才参与推演：

```csharp
public sealed class NoteCaptureScanner(INovelStore store, RetrievalKernel? kernel = null)
{
    public async Task<int> ScanAsync(
        string novelId, string sessionId,
        IReadOnlyList<ChatMessage> messages, int startIndex,
        int? capturedChapterOrder,
        CancellationToken ct = default);
}
```

规则（逐条实现）：

1. 只看 `Role == ChatMessageRoles.User` 且 `!string.IsNullOrWhiteSpace(Content)` 的消息；`Content.TrimStart()` 以 `[` 开头的一律跳过（系统注入块 `[远期推演]`/`[联想记忆]`/`[云端诊断]`/`[相关记忆`/`[用户写作规则]` 等都以 `[` 开头）。
2. 按 `。！？；\n` 切句；句子长度 ∈ [8, 300]。
3. 触发词常量数组（命中即候选）：`["记一下","记一笔","记下来","记下","备注","备忘","突然想到","我想到","有个想法","有个点子","以后可以写","可以写","留个伏笔","别忘了","灵感"]`。
4. 每条消息最多取 2 句、拼成 Body（中间 `。`），上限 300 字；整个扫描最多新增 3 条。
5. 去重（captured-check）：候选与**全部已有笔记**（任意状态）比较——`AgentMemoryBm25.Tokenize`（`src/Core/Vector/AgentMemoryBm25.cs:87`）分词后 token 集合 Jaccard ≥ 0.6 → 跳过。
6. 入库：`new AuthorNote { NovelId, Title = Body[..Math.Min(16, Body.Length)], Body, Status = Inbox, Source = AuditSource.Ai, SessionId = sessionId, CapturedChapterOrder = capturedChapterOrder }` → `SaveNote`；kernel 非空时建索引。
7. 异常吞掉返回 0（调用方另记日志），取消上抛。

接线（两个宿主各一处，用「计数回退自愈」，不改动全部会话重置点）：

- **TUI** `src/Tui/TuiApp.Session.cs` `RunPromptAsync`：在 `await PersistNewMessagesAsync();`（当前 `:123`）之后插入：

```csharp
try
{
    var msgs = _agent.State.Messages;
    if (msgs.Count < _noteScanIndex) _noteScanIndex = 0; // 压缩/换页/切会话后自愈
    var structure = await _store.GetStructure(_agent.CurrentNovelId ?? "");
    int? capturedOrder = structure is null
        ? null
        : structure.Chapters.Where(c => c.CharCount > 0).Select(c => (int?)c.Order).Max();
    await new NoteCaptureScanner(_store, _retrievalKernel)
        .ScanAsync(_agent.CurrentNovelId ?? "", _sessionId, msgs, _noteScanIndex, capturedOrder, ct);
    _noteScanIndex = msgs.Count;
}
catch (Exception ex)
{
    Services.TuiLogger.OperationFailed(_logger, ex, "capture notes from session");
}
```

  新增字段 `private int _noteScanIndex;`（紧邻 `_persistedMessageCount`）。`_store`（`TuiApp.cs:46`）、`_sessionId`（`TuiApp.Session.cs:26`）、`_logger` 与 `Services.TuiLogger.OperationFailed` 均为该文件既有成员。
- **Desktop** `src/Desktop/Services/SessionEngine.cs` `RunPromptAsync`：在 `await PersistNewMessagesAsync();`（当前 `:430`）之后插入同样逻辑，成员用该类的 `novelStore`、`retrievalKernel`、`agent.State.Messages`、`_sessionId`；异常用 `log.LogDebug(ex, "capture notes from session")`（该类字段名就是 `log`）。新增字段 `private int _noteScanIndex;`。**不要改 `DesktopAgentBridge`**（它不持有 store/kernel，且 DI 单例构造不宜加可选依赖）。

### C. 推演面

#### 6. `PlotForecast` 纯计算（`src/Core/Forecast/`）

记录类型（新文件 `PlotForecast.cs`，`namespace Core.Forecast;`，`using Core.Data; using Core.Vector;`）：

```csharp
public enum ForecastRiskKind { Overdue, DueWithoutAdvance, NoPlanOnWindowLine, BeatAfterReveal, UnwrittenHole, NoAppearanceRecords }

public sealed record ForecastChapterBrief(int Order, string Title, string Summary);
public sealed record ForecastBeat(int ChapterOrder, string PlotlineId, string PlotlineName, string Description, string Type, double Tension);
public sealed record ForecastObligation(string PlotlineId, string Name, PlotlineType Type, int PlannedOrder,
    int RemainingChapters, bool Overdue, IReadOnlyList<int> AdvanceOrders, string Turnpoints);
public sealed record ForecastSeedGap(string Kind, string EntityId, string Name, int ReferencedOrder);
public sealed record ForecastRisk(ForecastRiskKind Kind, string Message, int? ChapterOrder);
public sealed record ForecastEvidence(string Source, string? ChapterId, int? ChapterOrder, string? ChapterTitle,
    DocumentKind? Kind, string Text, string? Family, string? Channel, double Score);
public sealed record ForecastNote(string NoteId, string Title, string Excerpt, int? AnchorOrder, string Source); // Source: anchor|near|far|past
public sealed record NoteSelection(IReadOnlyList<ForecastNote> Notes, int InboxCount);
public sealed record PlotForecast(
    int CurrentOrder, int TargetOrder, string? TargetChapterId, string? TargetChapterTitle, string? VolumeGoal,
    IReadOnlyList<ForecastChapterBrief> Window, IReadOnlyList<ForecastBeat> Beats,
    IReadOnlyList<ForecastObligation> Obligations, IReadOnlyList<ForecastSeedGap> SeedGaps,
    IReadOnlyList<ForecastRisk> Risks, string QueryText, string LongRangeQueryText, bool HasPlanData);
```

`PlotForecastBuilder.Build(Novel novel, IReadOnlyList<Chapter> chapters, IReadOnlyList<Volume> volumes, IReadOnlyList<Plotline> plotlines, IReadOnlyList<Character> characters, IReadOnlyList<AppearanceRecord> appearances, int window = 5)`，常量 `DefaultWindow=5 / MaxWindow=10 / MaxBeats=8 / MaxObligations=8 / MaxSeedGaps=6 / MaxRisks=6 / MaxQueryChars=800`。规则：

1. `window = Math.Clamp(window, 1, MaxWindow)`；`written = chapters.Where(c => c.CharCount > 0)`；`current` = 最大已写 Order（无则 0）。**只用 `CharCount`，不读正文。**
2. `target` = 首个 `Order > current` 的章；`targetOrder = target?.Order ?? current + 1`；`windowChapters` = `[targetOrder, targetOrder+window)` 内的章。
3. `volumeGoal` = target 所属卷的 `Goal`，空则 `Summary`；`windowVolumeGoals` = 窗口涉及的卷去重（按 `Volume.Order`），每卷 `第{Order}卷《{Name}》目标：{Goal ?? Summary}`。
4. `open` = 未解决情节线（`ResolvedAtChapterId` 空），按 Name 排序。
5. `beats`：`TurnpointsBeatParser.Parse(p.Turnpoints, chapters.Select(c=>c.Order).ToHashSet())` 取 `ChapterOrder > current`，排序 `(ChapterOrder, PlotlineName)` 取 8；保留未截断的 `allFutureBeats` 供风险用。
6. `obligations`：`orderById = chapters.ToDictionary(c=>c.Id, c=>c.Order)`；`PlannedRevealChapterId` 解析不到 → 跳过该线（不猜）；`remaining = plannedOrder - current`；`overdue = remaining <= 0`；仅 `overdue || plannedOrder < targetOrder + window` 纳入；`advanceOrders` = 已写章中 `PlotlineIds` 含该 Id 的 Order（去重升序）；排序 `(overdue?0:1, plannedOrder, Name)` 取 8。
7. `seedGaps`：窗口章 `CharacterIds` 去重 → 若无任何 `AppearanceRecord{EntityType.Character, EntityId}` 落在已写章 Id 集合（`appearances.Count > 0` 才检查）→ `("Character", id, 角色名或 id, 该窗口章 Order)`；窗口章 `PlotlineIds` 去重 → `seeded = 已写章 PlotlineIds 含 || allFutureBeats 有 order ≤ current || appearances 含 EntityType.Plotline`，未 seeded → `("Plotline", …)`；排序 `(ReferencedOrder, Kind, Name)` 取 6。
8. `risks`（排序 `(ChapterOrder ?? int.MaxValue, Message)` 去重取 6），消息模板逐字：
   - `《{Name}》计划第{PlannedOrder}章揭示，已逾期 {current-PlannedOrder} 章`
   - `《{Name}》计划第{PlannedOrder}章揭示（还有 {PlannedOrder-current} 章），但已写章节中从未推进过该线`
   - `《{Name}》出现在第{Order}章细纲中，但没有计划揭示章 → 无法判断何时收束`
   - `《{Name}》的转折「第{BeatOrder}章：{Description}」排在其计划揭示章（{PlannedOrder}）之后`
   - `第{Order}章尚未写（跳章）`
   - `本书尚无出场记录，角色播种缺口未核对（写章后自动生成）`
9. `hasPlanData = 窗口有细纲 || obligations.Count > 0 || beats.Count > 0`。
10. `queryText`（近期，截 800）：`target.Summary` → 窗口每章 `第{Order}章：{Summary}` → 每条义务 `{Name}：{Turnpoints}` → `windowVolumeGoals`。
11. `longRangeQueryText`（远期，截 800）：`novel.Outline`（截 200）+ 每条 open 情节线 `{Name}：{Turnpoints}` + `windowVolumeGoals`。**总纲只进查询，不进注入块**（`NovelContextBlock` 每轮已给）。

空书（无章节）返回 `CurrentOrder=0, TargetOrder=1, Window=[]`，`HasPlanData=false`。

#### 7. 服务：证据 + 笔记选择（同一混合检索内核）

新文件 `src/Core/Forecast/PlotForecastService.cs`：

```csharp
public sealed class PlotForecastService(INovelStore store, RetrievalKernel? kernel = null, IFictionHarnessClient? harness = null)
{
    public Task<PlotForecast?> BuildAsync(string novelId, int window = PlotForecastBuilder.DefaultWindow, CancellationToken ct = default);
    public Task<IReadOnlyList<ForecastEvidence>> RecallAsync(string novelId, PlotForecast forecast, int topK = 6, bool includeCloud = true, CancellationToken ct = default);
    public Task<NoteSelection> SelectNotesAsync(string novelId, PlotForecast forecast, int topK = 4, CancellationToken ct = default);
}
```

- `BuildAsync`：`store.GetStructure(novelId)`（章头缓存；**不得**用 `GetChapters`）+ `GetPlotlines` + `GetCharacters` + `GetAppearances` → builder。
- `RecallAsync` 本地：`RetrievalSearchRequest { Domain=NovelKnowledge, OwnerId="local", NovelId, DocumentKinds=[ChapterSummary, ChapterBodyChunk, SceneDefinition, PlotlineProfile], ChapterOrderRange=(1, forecast.CurrentOrder), Query=forecast.QueryText, RankingProfile=WriteContext, DenseTopK=20, LexicalTopK=20, FinalTopK=clamp(topK*3,topK,30) }`；`ChapterOrderRange` 是「只检索已写章节」的硬保证；同章最多 2 条，总 `topK`；映射 `new ForecastEvidence("local", r.ChapterId, r.ChapterOrder, r.ChapterTitle, r.DocumentKind, r.Text, null, null, r.Score)`。
- `RecallAsync` 云端（可选）：`AssociativeLinkClient.LinkAsync(harness, novelId, [], forecast.QueryText, $"forecast-{novelId}-{Hash8(QueryText)}", depth:"quick", ct)`；`Status != "pass"` 跳过；保留 `SourceChapterOrder is null or <= CurrentOrder`；`new ForecastEvidence("cloud", null, hit.SourceChapterOrder, null, null, 文本, hit.Family, hit.Channel, hit.Cosine)`，`文本 = Bridge 空 ? Concept : $"{Concept}（{Bridge}）"`；与本地同章去重后补足 `topK`。
- `SelectNotesAsync`（**「jev + llm + bm25 + 向量」统一口径**）：
  1. `notes = await store.GetNotes(novelId)`；`active = notes.Where(Status==Active)`；`inbox = notes.Count(Status==Inbox)`；`selection` 结果按下方顺序产出。
  2. **硬锚点**（无模型）：正则 `第\s*(\d+)\s*章` 取正文首个章号；落入 `[targetOrder, targetOrder+window-1]` → `ForecastNote(..., AnchorOrder: n, Source:"anchor")`，按章号升序，最多 `topK`。
  3. **已写章锚点**：章号 `<= CurrentOrder` 的笔记里取 `UpdatedAt` 最新 1 条 → `Source:"past"`（用于「这条灵感是不是已经用掉了」）。
  4. **混合检索**（`kernel != null` 且两条查询非空）：两次 `SearchAsync`，`DocumentKinds=[AuthorNote]`、`EntityStatuses=["Active"]`、`Query` 分别为 `QueryText`（Source "near"）与 `LongRangeQueryText`（Source "far"），`FinalTopK=topK*2`。RRF 与 `JudgeRerank`（Jev/SystemOne 启用时自动生效）由内核统一完成，**不要自己写融合公式**。命中按 `SourceEntityId` 去重并跳过已选中的；near 先于 far；补足 `topK`。
  5. 回退：`kernel == null` 时只返回锚点（离线可用）。
  6. `Excerpt` = `Body` 空白归一的 80 字截断。
- 异常：ct 取消上抛；其余记录并返回已得结果。

#### 8. 文本块 / 钩子 / 工具

**格式化**：新文件 `src/Core/Forecast/PlotForecastFormatter.cs`（`using Core.Cloud; using Core.Data; using Core.Vector;`），`Prefix = "[远期推演]"`，`MaxChars = 1400`，
`Format(PlotForecast forecast, IReadOnlyList<ForecastEvidence> evidence, NoteSelection? notes = null)`：

- `!HasPlanData` → `[远期推演] 纲要数据不足：请先填写下一章细纲（edit_entities）或情节线的计划揭示章（manage_plotline）。`
- 节序（空节省略，行前缀 `- `，逐行预算 `MaxChars`，首次超限停止并追加 `…（已截断）`）：
  1. 首行 `[远期推演] 已写至第{CurrentOrder}章，下一章：第{TargetOrder}章《{Title}》`（无已写章 → `[远期推演] 尚未写章，…`；Title 空 → `（未在细纲中登记）`；`VolumeGoal` 非空 → 行尾 `（本卷目标：{goal}）`）。
  2. `到期义务：` `- 《{Name}》（{类型中文}）计划第{PlannedOrder}章揭示{，已逾期 k 章|，还有 k 章}｜已推进 {n} 次{（第a、b章）|（尚未推进）}`；类型中文 `Main/主线 Side/支线 Hidden/暗线 Foreshadowing/伏笔`。
  3. `风险：` `- {Message}`。
  4. `计划转折：` `- 第{ChapterOrder}章 ·《{PlotlineName}》[{Type} {Tension:0.0}] {Description}`。
  5. `计划远期（第{first}-{last}章）：` `- 第{Order}章《{Title}》：{Summary 截 60}`。
  6. `规划缺口：` `- 第{ReferencedOrder}章细纲提到的{Kind=="Character" ? "角色" : "情节线"}「{Name}」在前文无任何出场/推进记录 → 需先铺垫`。
  7. `笔记线索：`（`notes.Notes` 非空）
     - `anchor`：`- 《{Title}》第{AnchorOrder}章 · {Excerpt}`
     - `past`：`- 《{Title}》第{AnchorOrder}章（已写）· {Excerpt} → 确认是否已落地`
     - `near`/`far`：`- 《{Title}》· {Excerpt}{（远期提醒，far 时）}`
     - 末尾若 `InboxCount > 0`：`笔记待确认：{InboxCount} 条（确认后才参与推演）`
  8. `前文证据：` `- 第{ChapterOrder}章《{ChapterTitle}》·{Kind?.ToChineseLabel() ?? "联想"}：{Text 截 80}`；cloud 追加 `联想[{FamilyLabel}·{ChannelLabel}]`；ChapterOrder 空则省略「第N章」、标题空用 `—`。
- 行内空白归一（`\r\n`→`\n`、连续空白折叠、Trim）。

**钩子**：新文件 `src/Core/Forecast/PlotForecastHook.cs`（`using System.Collections.Concurrent; using System.Security.Cryptography; using System.Text; using Core.Data; using Core.Tools; using Microsoft.Extensions.Logging; using LlmTornado.Code;` + `using LlmChatMessage = LlmTornado.Chat.ChatMessage;`）：

```csharp
public sealed class PlotForecastHook(
    Core.Agent.Agent agent, INovelStore store,
    RetrievalKernel? kernel = null, ILogger<PlotForecastHook>? logger = null) : IToolHook
```

- `Prefix = PlotForecastFormatter.Prefix`；`TriggerTools` = `write_chapter, rewrite_selection, build_scene, manage_scene, batch_create_scenes, get_outline, edit_entities, manage_plotline, advance_plotline, suggest_continuation, manage_note, capture_note`。
- 字段：`private readonly PlotForecastService _service = new(store, kernel);` 与 `private readonly ConcurrentDictionary<string, string> _lastSentByNovel = new(StringComparer.Ordinal);`（按小说去重，天然有界）。
- `AfterToolCallAsync`：跳过 `Result is null or { IsError: true }`、`CurrentNovelId` 空；`_service.BuildAsync(novelId, ct)` → null 或 `!HasPlanData` 返回；`var notes = await _service.SelectNotesAsync(novelId, forecast, 4, ct)`；`text = PlotForecastFormatter.Format(forecast, [], notes)`；指纹 `Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..8]).ToLowerInvariant()`，与 `_lastSentByNovel.TryGetValue(novelId, out var last) && last == hash` 相同则返回；否则写入并 `agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, text))`。异常 `logger?.LogDebug(ex, "远期推演钩子失败（已忽略）: {Tool}", context.ToolName)`；ct 取消上抛。
- 接线（4 处，末尾追加）：`src/Host/HostBootstrapper.cs:1113` 与 `:1138` → `new PlotForecastHook(agent, store, retrievalKernel, provider.GetService<ILogger<PlotForecastHook>>())`；`src/Desktop/Program.cs:463` 与 `:477` → `new PlotForecastHook(agent, store, retrievalKernel, loggerFactory.CreateLogger<PlotForecastHook>())`（两处作用域内已有 `retrievalKernel`）。

**工具**：新文件 `src/Plugins/NovelTools/Tools/ForecastPlotTool.cs`（`using System.ComponentModel; using Core.Agent; using Core.Cloud; using Core.Data; using Core.Forecast; using Core.Tools; using Core.Vector;`）：

```csharp
public sealed class ForecastPlotParams
{
    [Description("小说项目ID，留空则自动使用当前项目")] public string NovelId { get; init; } = "";
    [Description("前瞻窗口：未来几章（1-10，默认5）")] public int Window { get; init; } = 5;
    [Description("是否附带前文证据与笔记检索")] public bool IncludeEvidence { get; init; } = true;
}
public sealed record ForecastPlotDetails(int TargetOrder, int DueCount, int OverdueCount, int SeedGapCount, int NoteCount, int EvidenceCount);
public sealed class ForecastPlotTool(INovelStore store, RetrievalKernel? kernel, IFictionHarnessClient? harness, Agent? agent)
    : AgentTool<ForecastPlotParams, ForecastPlotDetails>
{
    private readonly PlotForecastService _service = new(store, kernel, harness);
}
```

`Name => "forecast_plot"`、`Aliases => ["远期推演","远期预测","大纲推演"]`、`Label => "远期推演"`、`Category => ChapterManagement`、`DeferLoading => true`，`Description`（逐字）：`"结合总纲/卷纲/章节细纲、情节线转折点与作者笔记，推算未来 N 章的到期义务、计划转折、播种缺口与前文证据；开写前用它对齐全书走向。"`
执行：novelId 空 → `Err("当前没有打开的小说项目，请提供 novel_id。")`；`BuildAsync(novelId, args.Window, ct)` null → `Err("找不到该小说。")`；`!HasPlanData` → `Ok(Format(forecast, []), forecast, 0, 0)`；否则 `evidence = IncludeEvidence ? await RecallAsync(novelId, forecast, 6, true, ct) : []`、`notes = IncludeEvidence ? await SelectNotesAsync(novelId, forecast, 4, ct) : new NoteSelection([], 0)`，`Ok(Format(forecast, evidence, notes), forecast, notes.Notes.Count, evidence.Count)`。工具私有静态助手（仓库惯例，见 `RecallAssociativeTool.cs:157-175`）：

```csharp
private static AgentToolResult<ForecastPlotDetails> Ok(string text, PlotForecast forecast, int noteCount, int evidenceCount) =>
    new()
    {
        Content = [new TextContent(text)],
        Details = new ForecastPlotDetails(forecast.TargetOrder, forecast.Obligations.Count,
            forecast.Obligations.Count(o => o.Overdue), forecast.SeedGaps.Count, noteCount, evidenceCount),
    };

private static AgentToolResult<ForecastPlotDetails> Err(string message) =>
    new() { Content = [new TextContent(message)], IsError = true };
```

注册：`NovelToolsPlugin` 情节线区，`yield return new ForecastPlotTool(_store, _kernel, _cloudHarness, _agent);`。

### D. UI 面（独立窗口/对话框，双端）

#### 9. Desktop `NotesWindow`

新文件 `src/Desktop/Controls/NotesWindow.axaml` + `.axaml.cs`（code-behind 风格，范本 `SessionManagerWindow`；外观只走 `DialogTheme.ApplyWindowStyle(this, "笔记", 820, 620)` + 全局 Classes，禁止本地 Background/CornerRadius 裸值）。

- 构造 `NotesWindow(INovelStore store, RetrievalKernel? kernel, string novelId, string novelTitle)`。
- 布局 `Grid RowDefinitions="Auto,*,Auto"`：标题区（`SectionHeading` + 说明 + hairline）/ 主体 `Grid ColumnDefinitions="320,*"`（左 `Border Classes="Card"` 包 `ListBox`，右 `Border Classes="Card"` 包 `ScrollViewer` + `TextBlock TextWrapping="Wrap"`）/ 底部 `Grid ColumnDefinitions="*,Auto"`：左 mono 计数 `CountNote`（`共 N 条 · 候选 M`），右 `StackPanel`：`Ghost 新建` + `Ghost 关闭` + `Primary 编辑`（唯一 Primary）。
- 主体顶部工具栏（左列表上方一行）：过滤 `ComboBox`（全部/收件箱/已确认/已落地/已废弃，默认「全部」）；行排序始终 Inbox 优先、其后 `UpdatedAt` 倒序。
- 状态流转：详情区顶部放一个作用于当前选中项的 `ComboBox`（已确认/已落地/已废弃，绑定 `SelectedNote.Status` 的切换事件）——选中「收件箱」候选时切到「已确认」即确认，无需额外按钮；切换后 `SaveNote` + 重建索引 + 刷新列表。
- 行 `internal sealed record NoteRow(string Id, string Title, string Meta, string Body)`（顶级 internal，XAML `x:DataType` 需要）；`Title` 状态前缀：`【收件箱】/【已确认】/【已落地】/【已废弃】`；`Meta` = `记于第N章 · MM-dd HH:mm`（`ToLocalTime`）。
- `＋ 新建` → `new EntityEditWindow(store, EntityType.Note, novelId, null)`，`Closed += ReloadAsync`；双击/Enter/`编辑` → `new EntityEditWindow(store, EntityType.Note, novelId, row.Id)`，同样 `Closed += ReloadAsync`。
- `ReloadAsync`：`store.GetNotes` → 过滤 → `ItemsSource`；空态 `EmptyNote`；计数 `CountNote` 按上文。
- 写后重建索引：状态切换与编辑窗口关闭后，`var note = notes.FirstOrDefault(n => n.Id == id)`；`kernel is not null` 时 `await kernel.IndexDocumentAsync(kernel.Builder.BuildAuthorNote(note))`。**Desktop 没有实体变更→索引的接线，必须在此显式重建。**
- 入口：`src/Desktop/ViewModels/MainWindowViewModel.cs` `ShowCommandPalette()`（`:1079-1242` 的 list 内）加一条 `new PaletteCommand { Title = "笔记（记录 / 查看）", Description = "作者随手记：确认候选、查看与编辑", Execute = () => _ = ShowNotesWindowAsync() }`；新增 `ShowNotesWindowAsync()`（取 owner、取当前 `novelId`/书名、`new NotesWindow(...).ShowDialog(owner)`）。无当前小说 → 不加入条目（沿用现有命令面板按上下文构造的做法）。
- `src/Desktop/Tests/AdalineDesignSystemTests.cs` 的两个 `InlineData` 列表（`:89` 附近与 `:179` 附近）追加 `("Controls", "NotesWindow.axaml")`。

#### 10. TUI `NotesDialog` + `/notes`

新文件 `src/Tui/Controls/NotesDialog.cs`：

- 构造 `NotesDialog(INovelStore store, RetrievalKernel? kernel, string novelId)`；`public async Task ShowAsync()`。
- 列表：`OptionList<NoteItem>`（`ItemSearchText(Title)`、`ActivateOnClick(true)`、`AutoFocus = true`）；`NoteItem(string Id, string Title, string StatusText, string Body)`；排序：Inbox 优先，其后 `UpdatedAt` 倒序；状态过滤键 `Tab` 循环 `全部 → 收件箱 → 已确认 → 已落地 → 已废弃`（`BottomLeftText` 写清）。
- 详情：`FixedRatioPanel(list, CreateScrollableDialogContent(new Markup(Escape(body)).Wrap(true)))`；`Escape` 用 `ToolCallRenderer.Escape`（正文含 `[` 会被当标签）。
- `Dialog`：`IsResizable(true).IsModal(true).Title("📝 笔记").Width(96).MinHeight(24)`；关闭按钮 `PanelThemes.CreateDialogCloseButton(dialog.Close)`。
- 键位：`Enter` 编辑（`new EntityEditDialog(store, EntityType.Note, entityId: id, novelId: novelId).Show()`）、`n` 新建（`entityId: null`）、`c` 确认、`x` 已落地、`d` 废弃（`SaveNote` + 重建索引 + 刷新列表）、`Esc` 关闭；所有分支 `e.Handled = true`。
- 关闭时解绑：打开期间订阅 `store.OnEntityChanged`，事件 `EntityType.Note` → 刷新列表并重建该笔记索引；`dialog.Close` 前退订（避免泄漏）。
- slash 接线：`src/Tui/Services/SlashCommandParser.cs` 追加常量 `Notes = "/notes"`、别名 `Note = "/note"`，枚举 `SlashCommandKind.Notes`，`MapCommand` 两个 case；`src/Tui/TuiApp.Commands.cs` 的 `GetSlashCommands()`（`:36-67`）追加 `Notes`，`HandleSlashCommand` switch（`:313-321`）追加 `case SlashCommandKind.Notes: ShowNotesDialog(); break;`；新增 `private void ShowNotesDialog()`：无当前小说 → `ToastService.Info("请先打开小说项目")`，否则 `_ = new NotesDialog(_store, _retrievalKernel, novelId).ShowAsync();`。

### E. 测试

Core.Tests：
- `PlotForecastBuilderTests.cs`：空书；`CharCount` 判已写；target/窗口；overdue/due 与 `RemainingChapters`；`AdvanceOrders`；角色播种缺口（有/无记录、`appearances` 全空 → `NoAppearanceRecords`）；情节线未播种；beats 只取未来并排序/截断；`BeatAfterReveal`；`UnwrittenHole`；`NoPlanOnWindowLine`；`QueryText` 与 `LongRangeQueryText` 内容；`MaxQueryChars` 截断。
- `PlotForecastFormatterTests.cs`：`!HasPlanData` 引导行；各节逐字格式；空节省略；`MaxChars` 截断 + `…（已截断）`；证据本地/云端两态；`笔记线索` 四种 Source 渲染 + `笔记待确认` 行。
- `PlotForecastServiceTests.cs`：`FileNovelStore` 临时目录建 5 已写 + 3 计划章 + 情节线 + 出场记录；`BuildAsync` 断言；`RecallAsync` 用 `new RetrievalKernel(new InMemoryRetrievalStore(), new HashEmbedder())`（**不要用随机向量的 `DummyEmbeddingService`**；`InMemoryRetrievalStore` 无 FTS、`ChapterOrderRange` 依赖 metadata `chapter-order`，索引文档务必写入该键，否则候选被筛空）断言「已写章摘要」进、`chapter-order = 7` 的未写章不进；`includeCloud:false` 覆盖离线；`SelectNotesAsync` 断言：Inbox 笔记不进、Active 进、`第7章` 锚点强制入选、`InboxCount` 正确。
- `AuthorNoteStoreTests.cs` / `AuthorNoteDocumentTests.cs`（第 3 步）。
- `NoteCaptureScannerTests.cs`：含触发词的 user 句 → 生成 Inbox；assistant 消息不入；系统注入块（`[远期推演] …`）跳过；Jaccard 去重；单次上限 3；`CapturedChapterOrder` 正确。

NovelTools.Tests：
- `CaptureNoteToolTests.cs`：无 novel → 错误文案；空正文 → 错误；正常 → `SaveNote` 调用 + `Status=Active` + 标题取前 16 字 + 索引被写。
- `ManageNoteToolTests.cs`：list 排序（Inbox 先）+ 过滤 + 计数行；confirm/done/drop 状态流转；update 只覆盖非 null；get 未找到 → 错误。
- `ForecastPlotToolTests.cs`：缺 novel → 错误；无细纲 → 引导文案；正常 → 含 `[远期推演]` 与 `笔记线索`、`Details` 计数正确；`IncludeEvidence=false` 不触发检索。

Desktop.Tests：`NotesWindowXamlTests.cs`（字符串断言 `CountNote`、`EmptyNote`、`Classes="Card"`、`Title` 无代理项）；`AdalineDesignSystemTests` 的 InlineData 已含新窗口。

Tui.Tests：`SlashCommandParserTests` 追加 `/notes` 与 `/note` 的 `MapCommand` 断言；`TuiAppTerminalSlashCommandIntegrationTests` 追加 `DispatchAsync("/notes")` → `WaitForOutputAsync("笔记")`（无小说时断言 toast 文案「请先打开小说项目」）。

## Critical files & anchors

- `src/Core/Data/FileNovelStore.cs:2749-2806`（`SaveScene` 的 per-file 包装：`SaveEntityAsync` + `FireEntityChanged`）与 `:3706-3805`（`SaveEntityAsync`：文件名 sanitize/rename/事件）——`SaveNote`/`GetNotes` 照抄这条路径，**不要**用 `timeline.yaml` 单文件模式（无删除、无 `EntityChangeEvent`）。
- `src/Core/Vector/DocumentBuilder.cs:321-353` + `:557`（单块实体模板与 `entity-status` 写法）——`BuildAuthorNote` 与状态过滤值必须一致（PascalCase）。
- `src/Core/Data/VectorSyncService.cs:76-90,318-341,435-455`（任务注册、单类型同步、孤儿清理登记）——漏登记 = 每次启动把笔记索引清空。
- `src/Tui/Services/SlashCommandParser.cs:22-52,60-110,213-240` + `src/Tui/TuiApp.Commands.cs:36-67,308-321`（slash 五段式接缝）。
- `src/Desktop/Controls/SessionManagerWindow.axaml(.cs)`（列表窗范本：loader 构造、`CountNote`/`EmptyNote`、`Closed` 刷新）+ `src/Desktop/Controls/EntityEditWindow.axaml.cs:58-70`（类型驱动编辑窗，`entityId=null` 即新建）。

## Verification

1. 构建：`dotnet build src/Mythology.slnx`（cwd = 仓库根），0 error。
2. Core：`dotnet build src/Core/Tests/Core.Tests.csproj && dotnet exec src/Core/Tests/bin/Debug/net10.0/Core.Tests.dll`（xunit v3 MTP；本仓 `dotnet test` 会报「零个测试」的假失败，不要用）。
3. NovelTools：`dotnet build src/Plugins/NovelTools/Tests/NovelTools.Tests.csproj && dotnet exec src/Plugins/NovelTools/Tests/bin/Debug/net10.0/NovelTools.Tests.dll`。
4. Desktop：`dotnet build src/Desktop/Tests/Desktop.Tests.csproj && dotnet exec src/Desktop/Tests/bin/Debug/net10.0/Desktop.Tests.dll`（若输出在 `osx-arm64` 子目录则用实际路径）。
5. TUI：`dotnet test src/Tui/Tests/Tui.Tests.csproj`（本仓 AGENTS.md 指定；不要加 `--nologo`）。
6. 行为烟测（必须真跑，非仅单测）：`PlotForecastServiceTests` 端到端用例固定输入并逐行断言——
   - 5 已写章（第1章正文含「青铜钥匙」）+ 第6-8 章计划章（`Summary` = 回收青铜钥匙 / 北境使者抵达 / 决战）；情节线「青铜钥匙」`PlannedRevealChapterId`=第6章、`Turnpoints`=`第2章：埋下青铜钥匙\n第7章：真相揭露`；角色「北境使者」只出现在第7章 `CharacterIds`；角色「主角」在第1章有 `AppearanceRecord`（使 appearances 非空）；笔记 A：`Title = "半块玉的递法"`、`Body = "第7章 让北境使者递出半块玉"`、`Status = Active`；笔记 B：`Title = "决战前夜下雨"`、`Status = Inbox`。
   - 期望块含：`[远期推演] 已写至第5章，下一章：第6章`、`到期义务：`、`《青铜钥匙》（伏笔）计划第6章揭示，还有 1 章｜已推进 0 次`、`风险：`、`但已写章节中从未推进过该线`、`计划转折：`（含 `第7章`）、`规划缺口：`、`角色「北境使者」`、`笔记线索：`、`《半块玉的递法》第7章`、`笔记待确认：1 条`。
7. 注入烟测：`PlotForecastHookTests`（Core.Tests，范本 `AssociativeTriggerHookTests`：`new Agent(new ToolRegistry(), new TornadoApi(LLMProviders.OpenAi, "test-key"))` + 临时 `FileNovelStore`）断言：触发工具且计划/笔记数据存在 → `InjectQueue` 多出一条以 `[远期推演]` 开头的消息；同状态重复调用不再注入；`manage_note` 触发也会注入（笔记变化 → 指纹变化）；错误结果/非触发工具/无小说不注入。
8. 笔记链路烟测：`capture_note` → `manage_note list`（出现该条）→ 会话里说「记一下：决战前夜下雨」走过 `NoteCaptureScanner` → 出现 `【收件箱】` 候选 → `manage_note confirm` → `forecast_plot` 的 `笔记线索` 从 0 变为 1 条。
9. 回归确认：`AssociativeTriggerHookTests`、`NovelContextBlockTests`、`SetUpPanelViewStructureTests`（分区仍为 8）、`EntityEditorFieldCoverageTests` 全绿。

## Assumptions & contingencies

- **笔记是软删除**（`Status=Dropped`），不物理删除 YAML；因此 `INovelStore` 只加 `GetNotes` / `SaveNote` 两个方法，8 个测试替身各补两段内存实现。若后续确需物理删除，再加 `DeleteNote` 并复用 `DeleteEntityAsync`（`FileNovelStore.cs:3807-3835`），不在本次范围。
- **自动捕获只产候选**（`Inbox`），确认前不进推演、不进任何提示词；`capture_note` 与 UI 手写默认 `Active`。扫描器遇到任何异常返回 0，绝不阻塞对话。
- **钩子成本**：自动注入现在包含笔记检索（混合检索 + 可选 Jev 判定），每章最多一次（指纹去重）。若实测无 embedding 配置或明显卡顿，降级为只做硬锚点：`SelectNotesAsync` 在 `kernel == null` 时本就只返回锚点，无需改代码；必要时把 `manage_note`/`capture_note` 从 `TriggerTools` 移除。
- **桌面端无实体变更→索引接线**：`NotesWindow` 与两个笔记工具在写后显式重建索引；若遗漏，重启时 `VectorSyncService` 会对账修复（笔记已登记孤儿清理，不会被误删）。
- **不把笔记送云端**：`AssociativeSourceBuilder` / `SetupDigestBuilder` 均不改，笔记不出本机。
- **既有测试若因注入变多而失败**：仅允许更新「消息条数/顺序」类接线断言；块文本断言只加在新测试里。
