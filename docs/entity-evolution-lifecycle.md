# 实体×章节演化与状态变更生命周期

> 合并自原 `entity-evolution-lifecycle`（实体出场/状态演化元信息）与「本体状态变更全生命周期」两份设计，
> 反映当前实现（E1–E4 本地已交付；E5 云端投影另行确认）。本文是**唯一**的演化/状态变更规范。

## 1. 问题与目标

小说是随章节演化的实体世界，但实体文件只保存「当前快照」。本设计补齐三条链路：

1. **出场与变化记录**：谁在哪一章出场、发生了什么变化（结构化、带证据、可审计）。
2. **状态变更事务**：草稿章中的变化待发布；发布时统一应用结构化效果与联动；撤回按变更集精确回滚。
3. **统一时间线**：所有实体的每次变化可回答「哪一章、哪一场、哪个地点、哪条情节线、什么时间、谁在场、是否公开」。
4. **批量编辑入口**：harness 通过 `edit_entities` 跨类型批量修改设定面板条目；字段白名单复用 `EntityFieldRegistry`，不另立第二份字段表。

人工在面板直接改状态字段也算「设定变更」，立即生效并落盘，可撤销；新会话通过游标摘要主动获得上次会话以来的变化。

## 2. 数据模型

### 2.1 出场记录 `AppearanceRecord`

| 字段 | 说明 |
|---|---|
| `EntityType` / `EntityId` | 目标实体（Character/Faction/Location/Item/Plotline/Scene…） |
| `ChapterId` | 所在章节 Id（与既有章节指针一致，存 Id 非 Order） |
| `Mode` | `Present`（在场/有戏份）/ `Mentioned`（被提及/闪回） |
| `SceneIds` / `EvidenceSpan` | 关联场景（可空）与首次命中引文 |
| `Source` | `System`（扫描）/ `Ai`（抽取）/ `User`（手工） |

按章分片：`appearances/ch{Order:D3}.yaml`（该章全部记录，整章替换）。

### 2.2 状态变化 `EntityStateChange`

在基础字段（`TargetEntityType/TargetId/ChapterId/Aspect/FromSummary/ToSummary/EvidenceQuote/Summary/Source`）之上：

```csharp
public List<StateEffect> Effects { get; set; } = [];   // 结构化效果（发布时逐条应用）
public int Sequence { get; set; }                       // 章内顺序（1 起；设定变更为 0）
public string? Cause { get; set; }
public ChangeVisibility Visibility { get; set; } = Public;
public List<string> WitnessIds { get; set; } = [];
public string? LocationId / SceneId / TimeDescription;
public List<string> PlotlineIds { get; set; } = [];
public ChangeLifecycle Lifecycle { get; set; } = Committed; // Pending | Committed
public DateTime? CommittedAt { get; set; }
public string? ChangeSetId { get; set; }
public bool IsCascade { get; set; }
public string? SourceChangeId { get; set; }
public string? SessionId { get; set; }                  // 跨会话摘要标注「上次会话」
```

`ChapterId == ""` 表示**设定变更**（无章节归属），落 `evolution/manual.yaml`，不进入章节分片。

### 2.3 状态效果 `StateEffect`

`TargetType / TargetId / Field（kebab；metrics 用 metrics:<key>） / Operation（Set/Add/Remove/Clear/Increment） / Value / From / Cascade / SourceChangeId`。

### 2.4 实体状态字段

- `Character.CurrentLocationId`、`Item.LocationId`
- `Faction.Status`（Active/Dormant/Dissolved）
- `Item.Status`（Active/Lost/Destroyed）
- `Location.Status`（Active/Destroyed/Sealed/Abandoned）
- `Plotline.Status`（Active/Resolved/Abandoned）

新枚举：`ChangeLifecycle`、`ChangeSetStatus`、`ChangeSetEntryKind`、`FactionStatus`、`ItemStatus`、`LocationStatus`、`PlotlineStatus`、`StateEffectOperation`、`ChangeVisibility`；`OntologyOperation` 增加 `Revert`。

### 2.5 变更集 `ChapterChangeSet` / `ChangeSetEntry`

一次发布/补记事务的快照集合（`changesets/{chapterId}/{setId}.json`）：条目类型 `StateChange` / `EntityUpdate` / `EntityCreate`，含 `BeforeJson`（完整快照）与 `AfterJson`（指纹，状态变化忽略 `updated-at`）。

### 2.6 本体动作日志

`ontology/actions.jsonl` 追加式记录实体/关系/规则/状态变更及来源（`SessionId`、`ToolName`、`Evidence`、`ChangeSetId`、地点/场景/情节线/`Sequence`/`IsCascade`）。游标 `ontology/cursor.yaml` 存字节偏移。

## 3. 存储布局

```text
{novelDir}/
  appearances/ch{Order:D3}.yaml   # 出场记录（整章替换）
  evolution/ch{Order:D3}.yaml     # 章节状态变化（整章替换，含 Pending/Committed）
  evolution/manual.yaml           # 设定变更（ChapterId=""）
  changesets/{chapterId}/{setId}.json
  ontology/actions.jsonl
  ontology/cursor.yaml
```

- 章删除/重排级联维护两个演化分片；`ReorderAllChapters` 两阶段搬移分片。
- `SyncDocumentEnumerator` 将 `appearances/evolution` 映射为 `appearance`/`state-change` 同步文档；`changesets` 与 `ontology` 不同步。
- 演化元信息是**权威数据**，必须文件型；SQLite 只承载可重建索引/缓存。

## 4. 写入路径

### 4.1 出场：L0 扫描 + L2 工具

- `AppearanceEvolutionRecorder`（L0，零 token）在章内容写入后扫描已知实体名，写 `AppearanceRecord{Mode=Present, Source=System}`。
- `record_appearance` 工具供 AI 显式登记（`Source=Ai`）；UI 编辑走 `User`。
- `HasNewCharacterDebut` / 缺席配角检测优先读权威出场记录，无记录时回退正文扫描。

### 4.2 变化：`record_evolution`

- 参数支持 `effects`（结构化）、`to_value`（status 快捷）、`cause/visibility/witness_ids/location_id/scene_id/time_description/plotline_ids`。
- 逐条过 `StateEffectValidator`；非法 → `IsError`，不写库。
- `Sequence = 本章已有最大 Sequence + 1`（同一 `ChangeId` 覆盖时保留）。
- 上下文自动填充：参数 → 本章目标场景（`ParticipantIds` 命中）→ 本章第一个场景；地点/时间/情节线依次回落。
- 草稿章：写 `Pending`，返回「待发布」。
- 已发布章：写 `Pending` 后立即 `ApplyAmendmentAsync`；成功返回「已生效」，失败保留 pending。
- 已生效记录不可原地编辑；只能撤回章节或新增修正变化。

其它工具：`delete_evolution`（删待发布；草稿章残留的已生效记录可清理作为崩溃恢复出口）、`scan_ontology`（实体状态值闭集候选 + 证据闸门）、`set_chapter_status`（发布/撤回入口）。

### 4.3 未记录死亡检测

`DeathCandidateDetector`：仅草稿章；角色未死亡且本章无 status 变化；同段出现角色名 + 死亡关键词（排除「假死/没死」等）。候选进入发布预览，默认选中但可取消；harness 输出 `drift.unrecorded-death`。

### 4.4 人工设定变更

`FileNovelStore.SaveEntityAsync` 中心钩子：`Source==User`、非变更集写入、未抑制时，按状态字段白名单（status / faction-id / current-location-id / owner-id / location-id / leader / members / headquarters-id / parent-id / planned-reveal-chapter-id / resolved-at-chapter-id / role / tags / 场景列表等）生成 `EntityStateChange{ChapterId="", Lifecycle=Committed}` → `evolution/manual.yaml`，journal 记录 User 行。自由文本（性格/描述）只写 journal。

`undo_manual_state_change` 用 `Effects[0].From` 反向应用并保存，store 自动生成一条新的反向 manual 记录；journal 正常记录。

## 5. 状态效果应用器

`StateEffectApplier`：加载目标实体（单次缓存），逐条应用：

- `Set`：枚举/字符串/ID/metrics；`Add/Remove/Clear`：列表；`Increment`：metrics。
- 全部效果后执行一致性规则：
  - 角色换组织 → 旧组织移除成员、新组织加入成员；
  - 道具换主 → 关闭旧开放时窗、追加新时窗（需要章节上下文）；
  - 首领不在成员列表 → 加入；
  - 情节线 Resolved → `ResolvedAtChapterId ??= chapter.Id`，Active → 清空；
  - 组织 Dissolved → 清空 Leader/Members/成员归属/总部；
  - 地点 Destroyed → 清空该地点上的角色所在地、道具所在地、组织总部。
- 预检已拦截不支持字段；运行时跳过并记 warning，不半途崩溃。
- `onBeforeModify` 回调把「一致性规则顺带修改的实体」登记进变更集，保证撤回精确。

## 6. 发布/撤回事务

`ChapterLifecycleService` 是章节状态的唯一事务边界。

### 6.1 发布预检

- 章节必须 Draft；`Pending` 变化按章加载。
- `Sequence` 正整数且章内不重复；每条 effect 过白名单；目标实体与 ID 引用存在；`SceneId/LocationId/PlotlineIds` 存在。
- 人工已改目标字段（`From` 不匹配）→ 警告，不阻止。
- 正文检测死亡 + `CascadePlanner` 自动/可选联动建议。

### 6.2 联动

- 自动：关系结束（已有更早结束章保留）、移出组织、首领空缺→Dormant/Dissolved、道具无主/遗失、地点清空引用、情节线 `resolved-at`。
- 可选：纪念地创建、新首领、情节线放弃、组织道具遗失、未来场景地点清空。
- 自动建议不可取消；可选建议按 `DefaultSelected` 默认勾选。

### 6.3 应用与回滚

- 变更集：状态变化 Before 快照、受影响实体 Before JSON、纪念地 Create 条目。
- 应用：`Pending→Committed` 一次写回分片；按 `Sequence` 应用效果；保存联动；`SuppressChapterLifecycle` 下章节置 Published。
- 回填 After 指纹；`Status=Applied`。
- 失败：逆序恢复实体/删除创建/状态恢复 Pending/章节回 Draft/变更集 Failed；事务内新建的检测记录按 Id 删除。
- 补记：仅该 change + 默认联动，不改章节状态。

### 6.4 撤回

- 预检：后续已发布章阻止；变更集按 `AppliedAt` 降序检查冲突（同一实体只看最新变更集）。
- 回滚：EntityCreate 删除、EntityUpdate 恢复 Before、StateChange 恢复 Before；全部变化回到 Pending；章节 Draft。
- 追加 `Operation=Revert` 动作；变更集 `Reverted`。
- 无变更集的旧章：只翻回 Draft + warning（无法回滚实体）。
- 删除章节：已发布先撤回，失败直接返回。

### 6.5 守卫

- `SaveChapter` 状态变化必须经 service（抑制位仅事务内部使用）。
- `PublishedReferenceGuard`：被已发布章节/场景/叙事状态变化引用的实体禁止删除，先撤回章节。

## 7. 派生视图

### 7.1 `EntityTimelineView`（as-of）

内存构建，按章序重放 `Committed` 变化；`GetStateAsOf` 取每个 Aspect 最新值。设定变更在叙事重放之后，仅当 `CommittedAt` 更晚时覆盖。派生视图不落盘。

### 7.2 `EntityLogView`（全实体时间线）

合并章节状态变化 + journal 动作 + 旧审计（只读去重）：

- 状态行只渲染 `ChapterId != ""`；manual 由 journal 的 User 行表示，避免重复。
- 动作行排除 `Field=="state-change"`（已由状态行渲染）。
- 排序：章序 → 章内 Sequence → 时间；pending 同章末尾；设定组按时间。
- `EntityLogFormatter.ToMarkdown`：章 / 时间地点 / 情节线 / 变化 / 原因与在场 / 来源 / 状态（待发布、已生效、已回撤、🔗联动、🔒非公开）。

## 8. 跨会话摘要

`OntologyDigestBuilder` 从游标读取增量 action，附待发布变化（最多 20+5 条），由 `OntologyHarness.RunAsync` 每个 Agent 实例注入一次，与云端开关无关；无新 action 不注入。注入成功才推进游标；文件截断/偏移落半行均安全处理。

## 9. 一致性与云端投影

- 规则事实：角色当前地点/道具地点 → `located-at`；组织/道具/地点/情节线状态 token（枚举名 + 中文标签）；组织首领/总部关系。
- harness 确定性信号：`dissolved-faction-member`、`lost-item-used`、`destroyed-location`、`resolved-plotline`、`unrecorded-death`。只消费 `Committed` 变化。
- 云端投影（私有仓库）：角色当前地点、组织/道具/地点/情节线状态、道具地点；跳过 `lifecycle=pending`，避免草稿事实进入图谱。

## 10. UI

- **TUI**：实体详情时间线（图标/pending/联动/非公开）、`EntityLogDialog`、世界规则历史、大纲「时间线」与章发布/撤回、发布/撤回预览对话框、章节编辑先保存旧状态再走 service、阅读器待发布标记。
- **Desktop**：同上（`ChapterPublishWindow`/`ViewModel`、`ChapterUnpublishWindow`、`SetUpPanel` 时间线与菜单、实体编辑窗、阅读器、世界规则历史）。

## 11. 不变量与反模式

1. 章节状态只能经 `ChapterLifecycleService` 变化。
2. 只有 `Committed` 变化进入权威视图/规则/harness/云端。
3. 变更集必须覆盖所有被修改实体（含一致性规则顺带修改的实体）。
4. 状态变化指纹忽略 `updated-at`，否则整章写回会造成撤回误判。
5. 不做 per-chapter 全量实体快照（变化事件 + 有效区间）。
6. 演化元信息不进 SQLite（权威数据文件型）。
7. 契约字段必须与生成器成对交付，验收含字段非空率。
8. 云端图谱仅作同步文件的派生投影，不承担权威真源。

## 12. 设定面板批量编辑（`edit_entities`）

### 12.1 目标与边界

- 设定面板（TUI `SetUpPanel` / Desktop `SetUpPanelView`）八个 tab 的条目，harness 必须能改，且支持一次调用跨类型批量修改。
- 只补「修改」：create/delete 仍由 `manage_*` / `batch_create_*` 负责。
- 章节 `order` 与 `content` 不进 `edit_entities`：前者受 `ch{Order:D3}.md` 全局唯一文件名约束（用 `reorder_chapters`），后者已有 `write_chapter` / `edit_text`（带索引与演化钩子）；工具报错并指路。
- 地点邻接（`Location.AdjacentLocations`）不在本工具范围（不是面板编辑字段），需要时单独加结构化字段类型。

### 12.2 字段真源：`EntityFieldRegistry`

`src/Core/Data/EntityFieldRegistry.cs` 是唯一字段白名单（字段名/中文标签/FieldType/枚举选项）。`edit_entities` 用：

- `WritableFields(type)`：排除 `ReadOnly` 展示字段；
- `FindWritableField(type, field)`：接受模型属性名或 kebab 字段名（忽略大小写与 `-`/`_`）。

新增字段只改注册表，工具与两端编辑窗自动一致；不要再为工具复制字段表。

### 12.3 工具契约

`EditEntitiesTool(INovelStore store, RetrievalKernel? kernel = null)`：

```csharp
EditEntitiesParams { NovelId; List<EntityEditItem>? Items }
EntityEditItem   { EntityType; EntityId; List<EntityFieldPatch> Fields }
EntityFieldPatch { Field; string? Value; List<string>? Values }
EditEntitiesDetails { UpdatedCount; FailedCount; UpdatedIds }
```

- 支持类型：`novel/volume/chapter/character/faction/location/plotline/scene/item/relationship`。
- `entity-id` 对 character/faction/location/plotline/scene/item/volume 同时接受名称；novel 留空用 `novel_id`。
- 标量字段用 `value`（枚举可传中文标签或英文值）；列表字段用 `values`（空数组 = 清空）。
- 逐条独立事务：某条字段非法只让该条失败，其余继续；任一条字段非法时该条整条不写。
- 写入经 `INovelStore`：触发 `OnEntityChanged`、审计、本体 journal、向量索引，并遵守章节状态门禁与已发布引用守卫。

### 12.4 写入器

`EntityFieldWriter`（同文件 internal）：

- `Apply`：先校验全部字段（白名单、Chapter 禁 `Order`/`Content`、类型转换），再统一赋值；枚举走 `RelationshipTypeExtensions` / 枚举名归一；`bool` 接受 `true/false/1/0/是/否/on/off`；数字用 InvariantCulture。
- `SaveAsync`：角色 `faction-id` 位移与组织 `members` 变更双向对账（旧组织出列/新组织入列、成员旧组织移除）；其余类型直接走对应 `SaveX`。
- `IndexAsync`：Character/Faction/Location/Plotline/Item/Chapter（摘要）重建检索索引；与自动索引同源，避免面板刷新后索引漂移。

### 12.5 注册与测试

- 注册：`NovelToolsPlugin` 4.2.1 段 `EditEntitiesTool(_store, _kernel)`。
- 测试（`EditEntitiesToolTests`）：
  - 一次调用跨 6 类型覆盖历史缺口字段（角色 status/tags/abilities/notes；关系 since/until；场景 item-ids；章节 summary/plotline-ids/character-ids/notes/volume-id/title/status；小说 author/target-words/target-audience/enhancement-profile/auto-diagnose/auto-enhance/completion-status；分卷 notes）；
  - 未知字段只失败该条，其余成功；
  - 章节 `order`/`content` 被拒并指路；
  - 名称解析 + 组织成员双向对账；
  - 只读字段被拒；
  - 插件注册表包含 `edit_entities`。

## 13. 测试

- Core：`ChapterLifecycleServiceTests`、`StateEffectApplierTests`、`EntityEvolutionStoreTests`、EntityTimeline/as-of、manual/digest、引用守卫、死亡检测。
- 工具：`record_evolution`/`delete_evolution`/`undo_manual_state_change`/`set_chapter_status`/`scan_ontology`/`edit_entities`。
- UI：TUI 发布对话框与时间线、Desktop `ChapterPublishViewModel` 与章节时间线；`EntityEditorFieldCoverageTests` 保证注册表与模型字段一致。
- 回归口径：`dotnet build src/Mythology.slnx` + 各测试程序集（xunit v3 MTP）。

## 14. 开放问题

- 云端 E5 契约字段与演化算子（另行确认）。
- 乱序撤回需要章节依赖图（当前要求从后往前撤回）。
- 发布后实体被人工修改时整次撤回被阻止，不做部分回滚。
