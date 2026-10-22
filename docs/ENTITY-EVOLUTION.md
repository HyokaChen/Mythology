# 实体×章节演化元信息设计（ENTITY-EVOLUTION）

> 状态：**E1–E4 本地实施完成（2026-09-07）。** 本地侧（模型+存储 / 三层生成器 /
> as-of 视图+UI / 云端契约镜像）已全部落地并通过全仓回归；E5（云端仓库
> 云端私有仓库 契约字段+算子 / 云端图谱投影器 图谱）仍未实施，需另行确认。
> 上游依赖：`docs/HARNESS-CLOUD.md`（云端诊断链路——演化信号已接入其漏斗的
> L2-2 首现判定与 I4 缺席候选）、`docs/VEC.md` §0.2（事实源边界：YAML/`INovelStore` 唯一权威）、
> 云端私有仓库 仓库 `CHANGE_10.md`（fiction.setup.check 设定阶段分析——E5 契约扩展目标）。
>
> 核心问题一句话：SetUpPanel 八个实体模块只有「当前态」没有「演化态」——无法检索
> 人物在不同章节的出现、性格、行为与状态变化；本体六要素中「状态 / 行动 / 计算」
> 在实体×章节维度整条断链，本地不记录、云端不接收、两条分析管线各自用启发式从正文猜。

## 目录

0. [总纲](#0-总纲)
1. [现状审计](#1-现状审计双侧缺口)
2. [业界先例与设计原则收敛](#2-业界先例与设计原则收敛)
3. [目标模型：本体六要素落地](#3-目标模型本体六要素落地)
4. [生成机制：混合三层](#4-生成机制混合三层)
5. [as-of 派生视图](#5-as-of-派生视图计算要素的本地实现)
6. [云端接入设计：双通道与知识图谱](#6-云端接入设计双通道与知识图谱)
7. [UI 接入](#7-ui-接入)
8. [实施阶段与验收](#8-实施阶段与验收)
9. [反模式红线](#9-反模式红线)
10. [缺口与开放问题](#10-缺口与开放问题)

---

## 0. 总纲

### 0.1 问题定义

小说是「随章节演化的实体世界」，但本地数据模型只保存每个实体的**当前快照**：

- 「林长歌第 20 章时是什么状态？」——无法回答。`Character.Status` 是单值枚举
  （`Active/Deceased/Missing/Transformed`，`src/Core/Data/Enums.cs:21-33`），不是时间序列。
- 「这个配角最近三章出场了吗？」——每次都靠 `WritingFlowHook.FindAbsentSupportingCandidates`
  （`src/CloudDiagnosis/WritingFlowHook.cs:489+`）对近 K 章 `Content` 做字符串扫描，算完即丢，从不持久化。
- 「A 和 B 的关系怎么从敌人变盟友的？」——`Relationship.SinceChapterId/UntilChapterId`
  （`Models.cs:311/314`）只能表达一段时窗，表达不了演化过程，要拆两条记录还丢失中间态。

本体六要素（对象 / 状态 / 关系 / 规则 / 行动 / 计算）是建模业务系统本体的成熟框架
（参考 [本体建模方法论](http://www.uml.org.cn/ai/202607064.asp)：对象有生命周期与独立属性集、
状态承载生命周期流转、关系带方向与属性、规则是不变式约束、行动是可审计的原子操作、
计算是从已知推导新事实）。小说域与它的映射天然成立——角色是对象、立场/状态随章演化、
人物关系/人物-地点关系是关系边、世界规则是约束（触发型规则如「升级副本条件」）、
章节中的行为是行动记录、as-of 查询与云端诊断算子是计算。本文按此框架补齐缺口。

### 0.2 五项设计决策（已拍板，本文全部遵循）

| # | 决策 | 内容 |
|---|---|---|
| A | **存储架构：文件型一等实体** | 演化记录是 `FileNovelStore` 新子目录下的 YAML 一等实体（非嵌入现有实体、非 SQLite、非纯派生缓存）；as-of 查询运行时内存重放派生 |
| B | **生成来源：混合三层** | L0 字符串扫描自动打底（System，零 token）→ L1 章节完成钩子 AI 富化（带证据引用）→ L2 AI 工具 / UI 修正覆盖 |
| C | **云端范围：全链设计、分界实施** | 本文档完整设计云端契约扩展与新算子；云端私有仓库 仓库的实际代码改动单列为 E5 阶段，**需用户另行确认后才动云端仓库** |
| D | **知识图谱双栈** | 本地不建图查询引擎（内存邻接表足够）、语义检索复用既有 `SqliteRetrievalStore`（vec0+FTS5+RRF）；云端图谱用 PostgreSQL（已部署 pg16）→ AGE 台阶演进 |
| E | **云端图谱构建源 = 同步文件；本地权威 / 云端投影** | 云端知识图谱**从 cloud-novel-sync v3 已同步的文件内容解析构建**（完备性契约：不同步的本地数据云端图谱里没有）；本地保留 SetUpPanel 实体管理与 YAML 权威真源；云端图谱是单向派生投影（本地→云端），通过既有 `:cloud sync` 通道告知，不做双向同步 |

### 0.3 文档边界

- 本文档是设计规范，本身**禁止伴随实施**（本会话产出仅为文档本身）；E1–E4 为本地实施
  阶段、E5 为云端实施阶段（另行确认）。
- 不改 `docs/VEC.md` 的记忆域与检索 schema；不改 `HARNESS-CLOUD.md` 的调度漏斗——
  E 阶段演化信号接入其漏斗时按其 §3 三层门禁走，不在本文重定义。

---

## 1. 现状审计（双侧缺口）

### 1.1 本地缺口（逐条带行号证据）

| # | 缺口 | 证据 |
|---|---|---|
| L1 | `Character` 零章节出场字段 | `src/Core/Data/Models.cs:98-145`：Name/Gender/Age/Role/Personality/Appearance/Background/Motivation/Tags/Abilities/Weakness/Habits/Notes/Status/FactionId——无任何章节引用字段 |
| L2 | `Status` 是单当前值非时间序列 | `src/Core/Data/Enums.cs:21-33`：Active/Deceased/Missing/Transformed；「第 N 章时的状态」无处存放 |
| L3 | 章节指针散落且多为可选 | `Plotline.PlannedRevealChapterId/ResolvedAtChapterId`（`Models.cs:162/165`）+ `Turnpoints`（自由文本「第5章：真相揭露」，`Models.cs:147-168`）；`Relationship.Since/UntilChapterId`（`Models.cs:311/314`，单段时窗）；`Item.OwnershipPeriods[].Since/UntilChapterId`（`Models.cs:248-258`）；`TimelineEvent.ChapterId?`（`Models.cs:289`）；`SceneDefinition.ChapterId?+ParticipantIds?`（`Models.cs:324/336`，可选常空） |
| L4 | 出场判定纯运行时扫描、从不持久化 | `src/CloudDiagnosis/DiagnosisScheduler.cs:421-437`：`HasNewCharacterDebut` 扫 `CurrentContent.Contains(character.Name)`；`src/CloudDiagnosis/WritingFlowHook.cs:489+`：`FindAbsentSupportingCandidates` 近 K 章内容扫描 |
| L5 | 审计日志无章节锚点 | `EntityAuditLog`（`Models.cs:612-648`）：字段级 old→new 字符串（FieldName/OldValue/NewValue/Source/ToolName），能答「谁改的」不能答「第 N 章时是什么样」 |
| L6 | 存储全文件型 | `FileNovelStore`（YAML/MD/JSONL）；SQLite 仅用于缓存/派生层（vectors.db、AgentMemoryStore、ChatHistory、CloudDiagnosis 三表），实体权威数据零 SQLite |
| L7 | 世界规则无 AI 工具 | WorldRule 仅 `WorldRuleManagerDialog`（UI）管理；grep 全仓仅 `CheckSetupTool` 只读消费 |

### 1.2 云端缺口（云端私有仓库，2026-09 核实）

| # | 缺口 | 证据 |
|---|---|---|
| C1 | setup.check 无实体×章网格 | `云端 setup 分析模型`（`云端 setup 分析模型`）：`SetupCharacter` 仅 `FirstAppearanceChapterId?/DeathChapterId?` 两个标量锚点；`SetupRelation` 单段 `SinceChapterId/UntilChapterId`；无任何 per-chapter 出现/状态快照接入点 |
| C2 | 唯一网格只有情节线维度 | OutlineMetrics 的 `M[chapter,plotline]∈{0=无,1=埋设,2=推进,3=揭示,4=回收}`（CHANGE_10 §2.7）——章×情节线事件矩阵，无角色/实体维度 |
| C3 | story.check 只看文本表面 | `characterPageRank`（`Features/ChapterFeatures.cs:780+`）从正文共现图算排名，只出排名不出演化；`Tracking/CharacterConsistencyTracker.cs` 抽「我绝不/我发誓」声明与行为直方图 JSD——不知道设定演化 |
| C4 | 同步数据有存无解 | cloud-novel-sync v3 已把小说全量文件存进 `同步文档_documents/chunks/versions/document_chunks` 5 张表（`CloudDbContext.cs:311+`），但**云端从不解析这些内容**——setup.check 是请求期 `MapToDigest→Analyze` 用完即弃，零落库。即：云端已有数据、无解读层 |
| C5 | 无图/向量基础设施 | 全仓 grep 无 AGE/图查询/pgvector/Neo4j；语义向量仅请求期内存计算（`云端 Harness 实现.BuildSetupSemanticVectorsAsync`）；`novel_features` 用 JSONB 数组存特征，非 pgvector |

### 1.3 本体六要素 × 小说域缺口矩阵（本文核心表）

| 要素 | 小说域映射 | 已有 | 缺口 |
|---|---|---|---|
| **对象** | 12 实体（Novel/Chapter/Character/Plotline/Faction/Location/Item/TimelineEvent/Relationship/SceneDefinition/FactionRelationship/WorldRule）齐全 | ✓ `Item.OwnershipPeriods` 是时窗化范本 | — |
| **状态**（血型、开启关闭等） | `Character.Status`、`Item.OwnerId`+时段链 | Item 是唯一范本 | 角色/组织/地点/情节线无 per-chapter 状态轨迹；死亡后状态靠云端 L0「死亡章≤最后在场」结构检查兜底 |
| **关系**（对象间：人物-人物/人物-地点） | Relationship(类型/强度/单段时窗)、FactionRelationship、LocationAdjacency、Scene.ParticipantIds | 类型丰富 | 单段时窗表达不了「敌→友」（拆两条丢中间态）；场景共现可选常空 |
| **规则**（触发条件与约束，如升级副本） | WorldRule 数值型（规范键见 Core.Data.WorldRuleTypes：travel-speed/location-distance 等） | 云端 L2 有 STP/LTLf 消费者 | 无触发型/约束型规则；本地无 AI 工具管理（纯 UI）——本文 §10 列为开放问题，不扩 |
| **行动**（能做什么 & 记录了做什么） | `Abilities`（静态能力）+ `TimelineEvent`（Title/可选 ChapterId）+ 正文本身 | 骨架在 | 无结构化行动记录（谁、在哪章、对谁、做了什么、导致什么状态变化）；行动→状态因果链缺失 |
| **计算**（推导新事实） | 云端 L0/L2 算子+章线矩阵+一致性追踪；本地字符串扫描 | `M[chapter,plotline]` 是成功先例 | 本地无 as-of 查询；演化维度云端零接入；L1 AssertedFacts 契约存在但客户端恒传 null 空跑至今（CHANGE_10 §9.7） |


### 1.4 CHANGE_10 的三条先例教训（直接约束本设计）

1. **L1 AssertedFacts 空跑**：契约字段设计了、客户端恒传 null → 整层分析烂掉。→ 本文 §4
   规定每个新契约字段必须与生成器成对交付，验收含字段非空率。
2. **v4 年龄链路整体撤销**：STP 时序负环/lifespan 推理过重被砍。→ 演化推理避免重时序
   逻辑，优先「变化事件+有效区间」的 L0 式结构可判定检查。
3. **M[chapter,plotline] 矩阵成功**：OutlineMetrics 已消费 5 值章×线矩阵。→ 实体×章出现
   矩阵是同构扩展，是云端侧成本最低的接入面。

---

## 2. 业界先例与设计原则收敛

对 2024–2026 年叙事状态追踪研究与工程实践（7 组检索）的收敛结论：

| 系统 | 核心机制 | 对本设计的启示 |
|---|---|---|
| [NWM — Narrative World Model](https://arxiv.org/html/2607.05577v1) | typed temporal-state graph：每条边带 source chapter + evidence + **validity interval** + confidence；「as of chapter n」查询取每关系最新有效边 | 演化用**变化事件+有效区间**而非每章全量快照——存储低一个数量级，且天然可审计 |
| [NKW — Narrative Knowledge Weaver](https://arxiv.org/html/2606.05724v1) | 图度≥2 的角色重访抽取 state-based atomic facts（states/goals/causal roles/mental states），去重合并为时间敏感 profile | as-of profile 是**派生视图**不落盘为真源 |
| [write_ai_agent](https://github.com/Leolai6-7/write_ai_agent) | NetworkX 图 + `appears_in/located_in/plants/hints/resolves/causes` 边类型词表；**章节完成→增量 YAML diff 更新图** | 记录写入时机=章节完成钩子；边类型词表可直接借用 |
| [Story Codex（ProseEngine）](https://proseengine.app/story-codex) | scene-level 出场追踪（哪些角色/地点/组织同场）；arc 摘要从 first appearance 到 final scene | `SceneDefinition.ParticipantIds` 补全为共现权威的方向 |
| [PlotLens](https://plotlens.ai/) | 每个 fact 带 chapter/page citation；矛盾警报指向确立该事实的原文段落 | `EvidenceQuote` 段落级溯源字段 |
| [narrative-canon](https://github.com/project-89/narrative-canon) | state changes over time + 统一时间线 + Git-like commits/diffs | 事件溯源与文件型存储天然契合（YAML diff 可读） |

**收敛原则**（六条，全部业界一致）：

1. 实体为节点；
2. **变化事件+有效区间**为演化载体（非每章全量快照）；
3. 章节完成时增量写入；
4. 每条记录带正文证据；
5. as-of 查询为派生视图；
6. 图查询引擎按规模分层（本地数千条内存即可，重查询放服务端）。

---

## 3. 目标模型：本体六要素落地

### 3.1 两个新实体（完整字段表）

**`AppearanceRecord`（出场记录——「对象×章节」出现矩阵的行，承载「对象」与「行动」的在场维度）**

| 字段 | 类型 | 语义 |
|---|---|---|
| `Id` / `NovelId` / `CreatedAt` / `UpdatedAt` | 继承 `Entity` 基类（`Models.cs:4-15`） | 同现有 12 实体 |
| `EntityType` | 复用现有枚举 `EntityType` | 目标实体类型（Character/Faction/Location/Item/Plotline/Scene…，即「谁出场」） |
| `EntityId` | `string` | 目标实体 Id |
| `ChapterId` | `string` | 所在章节 Id（非 Order——与现有章节指针族一致） |
| `Mode` | 新枚举 `AppearanceMode`：`Present`（在场/参与对话/有戏份） \| `Mentioned`（被提及/闪回引用） | 出场强度二档 |
| `SceneIds` | `List<string>?` | 关联场景（可空——场景未定义时仍可记录出场） |
| `EvidenceSpan` | `string?` | 首次出现段落引文（PlotLens 式溯源） |
| `Source` | 复用 `AuditSource`（`Enums.cs:259-273`）：`System`=扫描/Ai=抽取/User=手工 | 记录来源三源 |

**`EntityStateChange`（变化事件——「状态」维度载体）**

| 字段 | 类型 | 语义 |
|---|---|---|
| `Id` / `NovelId` / `CreatedAt` / `UpdatedAt` | 继承 `Entity` | 同上 |
| `TargetEntityType` | `EntityType` | 变化目标类型 |
| `TargetId` | `string` | 变化目标 Id |
| `ChapterId` | `string` | 变化发生章 |
| `Aspect` | 新枚举 `EvolutionAspect`：`Status` \| `Personality` \| `Motivation` \| `Relationship` \| `Ability` \| `Possession` \| `Location` \| `Knowledge` \| `Behavior` \| `Other` | 变化面（对应本体「状态」的各切面） |
| `FromSummary` / `ToSummary` | `string?` | 前后态一句话（如「敌视→信任」） |
| `EvidenceQuote` | `string?` | 正文证据引文 |
| `Summary` | `string` | 一句话变化描述（「第12章盟友背叛后性情大变」） |
| `Source` | `AuditSource` | 同上三源 |

设计依据：

- **变化事件而非每章快照**（NWM 原则 2）：每章全量快照 = 100 章 × 50 实体 × 全字段 ≈
  百万行级；变化事件 = 每章 5–20 条 ≈ 数千行，低两个数量级，且每条可独立审计/修正。
- **`Aspect` 覆盖本体「状态」全切面 + 「行动→状态」因果位**：`Possession` 承接道具易主
  （`Item.OwnershipPeriods` 的语义化伴生记录）、`Knowledge` 承接信息差、`Relationship` 承接
  关系演化（与 `Relationship` 实体的时窗互补：实体记「结构」，事件记「过程」）。
- **`Source` 三源复用 `AuditSource`**：与审计链同源枚举，UI 可用同一图标体系（👤/🤖/🔧）。

### 3.2 存储布局（决策 A：按章分片列表）

```
{novelDir}/
  appearances/ch{Order:D3}.yaml   # 该章全部 AppearanceRecord 列表
  evolution/ch{Order:D3}.yaml     # 该章全部 EntityStateChange 列表
```

- **为何按章分文件**：记录生命周期与章节绑定——章重写（`RewriteSelectionTool`）/ 章删除 /
  重排（`ReorderChaptersTool`）是常见操作，按章组织使「删一章记录」= 删一个文件 O(1)
  （级联删除），且单文件即单章 diff，git 审阅友好。跨实体轨迹查询不落盘、走 §5 内存视图。
- **与 FileNovelStore 两种既有先例的关系**：非「每实体一文件」（`SaveEntityAsync` 模板，
  `FileNovelStore.cs:2401-2478`——适合角色这类有唯一 Id 的长寿实体），也非「单文件全列表」
  （`timeline.yaml`/`relationships.yaml` 模式——适合几十条的小集合），而是**按章分片列表**：
  因记录生命周期锚定在章节上而非实体上，这是有意选择。
- **新枚举与 EntityType 扩展**：`Enums.cs` 新增 `AppearanceMode`、`EvolutionAspect` 两个枚举
  + `EntityType` 新成员 `Appearance`、`StateChange`（含 `SnakeCaseMap` 扩展
  `appearance`/`state-change`）。旧 JSONL 审计文件的历史值由一次性迁移改写（见 CHANGE_11 C12）
  （E1 实施时加一条单测验证）。
- **SQLite 红线**：演化元信息是**权威数据**（AI 写入、用户审计、可手工修正），必须文件型；
  仓库先例（vectors.db、AgentMemoryStore、CloudDiagnosis 三表）全部是缓存/派生，权威实体
  数据从未进 SQLite——保持一致。

### 3.3 INovelStore 新方法签名表

```csharp
// 出场记录
Task<List<AppearanceRecord>> GetAppearances(string novelId, CancellationToken ct = default);
Task<List<AppearanceRecord>> GetAppearancesByChapter(string novelId, string chapterId, CancellationToken ct = default);
Task SaveChapterAppearances(string novelId, string chapterId, IReadOnlyList<AppearanceRecord> records, CancellationToken ct = default); // 整章替换
Task DeleteChapterAppearances(string novelId, string chapterId, CancellationToken ct = default);

// 变化事件
Task<List<EntityStateChange>> GetEntityStateChanges(string novelId, CancellationToken ct = default);
Task<List<EntityStateChange>> GetEntityStateChangesByEntity(string novelId, EntityType entityType, string entityId, CancellationToken ct = default);
Task SaveEntityStateChanges(string novelId, string chapterId, IReadOnlyList<EntityStateChange> changes, CancellationToken ct = default); // 整章替换
Task DeleteEntityStateChanges(string novelId, string chapterId, CancellationToken ct = default);
```

方法族语义对齐既有 TimelineEvent/Relationship 方法组（整章替换 = 删旧写新，写审计）。

### 3.4 本地知识图谱轨（决策 D 第 1 轨）：不建图查询引擎

- **规模论证**：单部小说演化数据 = 100 章 ×（30–50 实体出场 + 5–20 变化事件）≈ 3–5k 条
  出场 + ≤2k 条事件。内存邻接表 + 字典索引（§5 `EntityTimelineView`）即可承载全图操作
  （多跳遍历/中心性/连通分量在这个量级是毫秒级）。把图查询引擎放本地是为不存在的规模
  问题付费。
- **本地语义检索复用既有设施**：「找性格相似角色」这类需求走 `SqliteRetrievalStore`
  （vectors.db，`src/Core/Vector/SqliteRetrievalStore.cs:14-21`：`vec_chunks` vec0 +
  `retrieval_chunks_fts` FTS5 + RRF 融合，已在产线运行，含维度漂移 DROP 重建、扩展加载
  失败降级全量扫描等工程化处理）。演化记录作为文档入检索库（域/种类划分 §10 待 E2 定），
  **不另建向量基础设施**。
- **sqlite-vec 能力边界（终结「本地 vec0 建图」设想）**：本地已用 sqlite-vec 0.1.7-alpha.2.1
  （`Core.csproj:33`），但它是纯 ANN 扩展——只做向量近邻（`vec0` 虚拟表 + KNN MATCH），
  无图遍历、无 图查询、无最短路径。SQLite 想做图查询要全表自建邻接表 + 递归 CTE，
  与直接内存字典无异。**SQLite 能存图数据 ≠ SQLite 是图数据库**。

---

## 4. 生成机制：混合三层（决策 B）

### 4.1 L0 扫描打底（System 源，零 token，无条件执行）

新增 Core 层组件 `AppearanceEvolutionRecorder`（工具与 Hook 均可引用）：

- 把 `DiagnosisScheduler.HasNewCharacterDebut`（`DiagnosisScheduler.cs:421-437`，扫
  `CurrentContent.Contains(character.Name)`）与 `WritingFlowHook.FindAbsentSupportingCandidates`
  （`WritingFlowHook.cs:489+`）的运行时扫描**固化为持久化出场记录**：
  章内容写入后扫描全部已知实体名（characters/locations/items/factions），命中 →
  `AppearanceRecord{Mode=Present, Source=System}`，EvidenceSpan 取首次命中上下文截断。
- 无条件执行（零 token 成本），保证「字段非空率」兜底——L1 空跑教训的保险层。

### 4.2 L1 AI 富化（章完成钩子，可选增强）

`WriteChapterTool` / `RewriteSelectionTool` 成功后触发：

1. L0 扫描层先写出场记录（同 4.1）；
2. LLM 对该章抽取变化事件（Aspect 判别 + FromSummary/ToSummary + EvidenceQuote），
   写 `EntityStateChange{Source=Ai}`。

对齐 write_ai_agent「章节完成→增量 diff」先例。成本控制：变化事件抽取是可选增强
（配置开关，默认开；关闭时 L0 出场记录不受影响）。

### 4.3 L2 修正（AI 工具 + UI）

- 新增 AI 工具 `record_appearance` / `record_evolution`（Plugins/NovelTools，风格对齐
  `Manage*Tool` 族：参数含 entityType/entityId/chapterId/mode/aspect/summary/evidenceQuote）。
  用途：AI 写作时显式登记（如「让林长歌在本章首次登场」→ 同时写出场记录 + Status 变化事件）。
- UI：SetUpPanel 演化段（§7）提供查看与编辑入口（复用实体编辑对话框模式）。
- L2 写入 `Source=User/Ai`，覆盖 L0/L1 同目标记录（整章替换语义，记录级以最新为准）。

### 4.4 L1 空跑教训对策（CHANGE_10 §9.7）

每个新契约字段必须与生成器成对交付——本设计 E4 契约字段（§6 通道①）与 E2 生成器
（本章）在同一阶段验收；验收标准显式含「字段非空率」（L0 无条件执行保证 appearances
非空；evolution 非空率 ≥ 每章 1 条或显式标记「本章无变化」）。

---

## 5. as-of 派生视图（「计算」要素的本地实现）

### 5.1 EntityTimelineView（内存构建，不落盘）

```
输入：GetAppearances(novelId) + GetEntityStateChanges(novelId) 全量
构建：按 (TargetEntityType, TargetId) 分组 → 组内按 ChapterOrder 排序重放
查询：
  GetStateAsOf(entityType, entityId, chapterOrder)   // 第 N 章时该实体的状态汇总
  GetAppearancesAcrossChapters(entityType, entityId) // 出场频率曲线（章序列+mode）
  GetEntityTimeline(entityType, entityId)           // 出场+变化事件合并时间线
```

- 重放语义：变化事件按章序应用（`FromSummary→ToSummary` 链），出场记录按章聚合。
  对齐 NWM「取每关系最新有效边」的 as-of 语义。
- 派生视图不落盘（NKW 原则）：每次构建走内存（数千条，毫秒级），无失效同步问题。

### 5.2 消费者与既有机制收敛

| 消费者 | 现状 | 改造 |
|---|---|---|
| SetUpPanel 演化段 | 不存在 | E3 新增（§7） |
| `GetWritingStats` / AI 查询工具 | 无实体维度统计 | E3 读视图 |
| `HasNewCharacterDebut` | 运行时字符串扫描 | E3 改读权威出场记录（扫描逻辑保留为 L0 生成器，判定不再重算） |
| `FindAbsentSupportingCandidates` | 近 K 章内容扫描 | E3 改读出场频率曲线 |

---

## 6. 云端接入设计：双通道与知识图谱

### 6.0 双通道总览

| 通道 | 数据内容 | 触发时机 | 云端用途 |
|---|---|---|---|
| ① setup.check 请求字段（原有 + E4 扩展） | 演化摘要（出现矩阵 + 变化事件，请求期字段） | `:cloud check` / CheckSetupTool 手动触发 | SetupAnalysis 请求期算子（新增演化算子族） |
| ② cloud-novel-sync 文件同步（既有通道，零改动承接） | 全量 YAML/MD 文件（含 E1 新增 appearances/evolution 子目录文件） | `:cloud sync` 手动/定期 | **云端知识图谱构建源**（G1+） |

两通道独立：①服务诊断请求的即时分析，②服务持久图谱的构建与展示。演化数据的
权威真源在本地文件，①是它的请求期投影、②是它的持久投影。

### 6.1 通道①：setup.check 契约扩展（E4 本地镜像 → E5a 云端）

本地镜像 `src/Core/Cloud/FictionHarnessContracts.cs` 先行（对齐云端 `CloudSetupInput`）：

```csharp
public sealed record CloudEntityAppearance(
    string EntityId,
    string EntityType,           // character/location/faction/item/plotline/scene
    IReadOnlyList<string> ChapterOrders,  // ch_{Order} 规范化（SetupDigestBuilder B2 先例）
    IReadOnlyList<string>? Modes           // 与 ChapterOrders 一一对应：present/mentioned
);

public sealed record CloudEvolutionEvent(
    string TargetId,
    string TargetType,
    string ChapterOrder,         // ch_{Order}
    string Aspect,               // status/personality/motivation/relationship/ability/possession/location/knowledge/behavior/other
    string? From,
    string? To,
    string Summary,
    string? Evidence
);
```

- `CloudSetupInput` 新增 `EntityAppearances?: IReadOnlyList<CloudEntityAppearance>` 与
  `EntityEvolutionEvents?: IReadOnlyList<CloudEvolutionEvent>`，空表不上报（NullIfEmpty
  先例）；章节引用一律 `ch_{Order}` 规范化。
- 上传开关默认关（防云端严格反序列化拒未知字段；云端 JSON 容错未核实，§10）。

云端新算子清单（设计，E5a 实施；消费通道①）：

| # | 算子 | 判定 | 层级 |
|---|---|---|---|
| 1 | 出现连续性 | `FirstAppearanceChapterId` vs 出场矩阵首行不一致；死亡章后仍出现 Present 出场 | L0（结构可判定） |
| 2 | 演化平滑度 | 相邻同实体 personality/motivation 事件无铺垫突变（缺中间过渡事件） | anomaly |
| 3 | 配角衰减精确版 | 出场 mode 频率曲线（权威记录）取代字符串扫描近似——直接喂已有休眠区间/衰减算子 | anomaly |
| 4 | 场景共现→关系强度校准 | Scene.ParticipantIds 共现频次 vs Relationship.Strength 声明值偏离 | anomaly |

### 6.2 通道②：云端知识图谱（决策 E——从同步文件构建）

**完备性契约（红线）**：云端知识图谱的数据边界 = cloud-novel-sync 已同步的文件集合。
未同步的本地文件（从未 `:cloud sync`、或冲突未合并）在云端图谱中**不存在**。这是设计
约束而非缺陷：图谱是同步数据的派生解读层，不是本地数据的镜像。**云端图谱「没有」≠
小说「没有」**——所有图谱结论必须在此边界内解读。

**台阶演进（G0→G1→G2，每级有明确触发门槛）**：

- **G0（已就绪，零新增）**：cloud-novel-sync v3 通道本身——云端
  `NovelSyncController`（`[Route("v1/sync")]`）+ 5 张 sync 表（chunk 内容寻址 FastCDC +
  版本 manifest + 乐观锁）；本地 `CloudNovelSyncService` 三路比较（本地文件/云端 meta/
  state 基线，本地永远是 working copy 权威，冲突落 `~/.mythology/cloud-conflicts/` 副本）、
  `SyncDocumentEnumerator` 按 (DocumentType, EntityId) 键推送 typed document。E1 新增的
  appearances/evolution YAML 落盘后自动被枚举为同步单元推送（分类是否需为演化目录加
  显式分支，E1 实施时验证，§10）。
- **G1（云端解析层，核心新增，E5b）**：新增「同步文件→实体图」解析器
  **`云端图谱投影器`**（云端侧组件）：从 同步文档 文件内容解析 YAML
  （characters/plotlines/relationships/scenes/timeline/appearances/evolution/...）→
  建实体节点与边（含时窗 since/until_chapter、evidence、source 列）→ 落 PG 关系表
  云端图谱表。要点：
  - 字段映射复用 setup.check 的 `MapToDigest` 语义（本地 `SetupDigestBuilder.cs:29-232`
    的云端镜像版，映射表对照 `FictionHarnessContracts.cs` 逐字段）；
  - 触发时机：sync push 后增量重建，或惰性首次查询时构建；
  - 查询用 `WITH RECURSIVE` 多跳/as-of——覆盖绝大多数图问题，不动 EF/Npgsql
    （PG16 事实：docker-compose 已部署 postgres:16-bookworm，CloudDbContext schema=mythology
    21 张表）。
- **G2（重图查询才触发，E5b 后期）**：PostgreSQL + Apache AGE 扩展，图查询 查询、
  路径/社区/中心性图算法。**触发门槛**：出现真实多跳语义查询需求且 G1 递归 SQL 可读性
  或性能不足。AGE 是 PG 扩展叠加（`LOAD 'age'` + `SELECT * FROM cypher(...)` 走 Npgsql
  原生 SQL），EF Core/Npgsql/21 张业务表零改动；不引第三方 ORM。AGE 1.5+ 与 PG16 兼容
  组合实施前核对（§10）。

**架构原则（本地权威 / 云端投影）**：

- 本地 YAML 永远是权威真源——40+ AI 工具（Manage*Tool/WriteChapterTool 等）全部经
  `INovelStore` 读写本地文件、SetUpPanel 渲染本地 store、Offline 状态栏证明离线创作
  场景存在（VEC.md 本地优先边界）。
- 云端图谱是同步数据的**派生投影**：单向（本地→云端）、不做双向同步、不承担权威真源。
- 云端图谱展示（图谱化查看 / 多跳查询 / 图算子诊断）是云端服务能力；本地通过既有
  `:cloud sync` 通道告知，**不新增专用上传接口**。

### 6.3 分界声明

- **E5（云端仓库实际改动）单列，需用户另行确认后才动 云端私有仓库**——本文对云端
  的全部设计（通道①字段+算子、通道② 云端图谱投影器、G1/G2 台阶）都是设计稿，
  不构成对云端仓库的本次改动授权。
- E4（本地）只做契约镜像 record + 上传开关（默认关）。

---

## 7. UI 接入

- SetUpPanel 各实体详情区（`BuildCharacterDetail` 等 8 个构建器）加**「演化轨迹」折叠段**：
  读 `EntityTimelineView` 渲染 per-entity 时间线（出场章序列 + mode 标记 + 变化事件列表）。
- 展示复用 `OnViewAuditLog` → `MarkdownDialog` 模式（`src/Tui/TuiApp.Events.cs:522-565`
  先例：源图标 👤/🤖/🔧 + 字段详情）；演化段的 Source 标记用同一图标体系。
- 实体头部操作区（📝/📋/📜）不扩——演化入口放详情内容区，不挤占头部空间。

---

## 8. 实施阶段与验收

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **E1** 模型+存储 | `Models.cs` 新实体 + `Enums.cs`（`AppearanceMode`/`EvolutionAspect` 枚举 + `EntityType.Appearance/StateChange` + snake_case 映射）+ `INovelStore` 方法族 + `FileNovelStore`（appearances/evolution 子目录） | 单测：YAML 往返序列化、章级联删除（删章 → 两子目录对应 ch{Order} 文件消失）、枚举 ReverseMap 兼容；`SyncDocumentEnumerator` 对新子目录分类验证 |
| **E2** 生成器 | `AppearanceEvolutionRecorder`（L0）+ `record_appearance`/`record_evolution` 工具（L2）+ 章完成钩子（L1） | 写一章 → appearances/evolution 文件出现且内容正确（含 EvidenceSpan/Quote）；字段非空率达标（§4.4） |
| **E3** 视图+UI | `EntityTimelineView` + SetUpPanel 演化段 + `HasNewCharacterDebut`/`FindAbsentSupportingCandidates` 改读 | UI 可见角色轨迹；as-of 查询正确（多实体多事件排序重放）；`HasNewCharacterDebut` 与扫描版结果对拍 |
| **E4** 云端契约镜像（本地） | `CloudEntityAppearance`/`CloudEvolutionEvent` record + `SetupDigestBuilder` 映射 + 上传开关（默认关） | 开关开时 payload 含新字段（本地构造测试验证，不实际调云端） |
| **E5a** 云端契约+算子（**另行确认**） | 云端 `CloudSetupInput` 字段 + `MapToDigest` + 4 个演化算子（通道①） | 云端测试：新字段反序列化 + 算子判定（fixture 用本地生成的 payload） |
| **E5b** 云端图谱（**另行确认**） | `云端图谱投影器` + 云端图谱表（G1）；AGE（G2 仅当门槛触发） | G1：同步一本测试小说 → 图表内容与本地 YAML 一致；多跳查询返回正确 |

依赖关系：E1→E2→E3→E4 串行（每级消费上一级产物）；E5a/E5b 依赖 E4 的契约定义但
相互独立（E5a 消费通道①、E5b 消费通道②）。

---

## 9. 反模式红线

1. **不做 per-chapter 全量实体快照**（NWM 教训：变化事件+有效区间比快照低两个数量级）。
2. **演化元信息不进 SQLite**（权威数据文件型哲学；SQLite 先例全部是缓存/派生）。
3. **新契约字段禁止空跑交付**（L1 AssertedFacts 教训：字段+生成器成对，验收含字段非空率）。
4. **演化推理避免重时序逻辑**（v4 年龄链路撤销教训：优先 L0 式结构可判定检查，
   如「死亡章后出现 Present 出场」是集合判定，不做 STP 推理）。
5. **不做 L3 常识推断**（CHANGE_10 红线沿用：云演化算子只用结构化字段，不猜「这样合理吗」）。
6. **本地不建图查询引擎、不引图数据库**（规模数千条，内存邻接表足够；语义检索复用
   `SqliteRetrievalStore` vec0+FTS5，不另建向量设施）。
7. **云端知识图谱仅作派生投影**（从 cloud-novel-sync 同步文件构建），不做双向同步、
   不承担权威真源；完备性边界=已同步文件集合（云端图谱「无」≠小说「无」）。
8. **不为云端图谱废弃本地实体管理**（40+ AI 工具/SetUpPanel/离线写作依赖本地 store 权威）。

---

## 10. 缺口与开放问题

| # | 问题 | 状态 |
|---|---|---|
| 1 | 触发型/约束型 WorldRule 扩展（升级副本类「规则」要素）未覆盖——现有 4 数值型不动，扩展待真实需求出现 | 开放（YAGNI） |
| 2 | 契约注释过期修正：`WorldRuleInput` 注释「客户端暂不上传，恒空」（`src/Core/Cloud/FictionHarnessContracts.cs:165`）已过期——`SetupDigestBuilder.cs:215` 实际已上传。E1/E4 实施时顺手修正 | 待修（一行注释） |
| 3 | `EntityType` 枚举扩展的反序列化兼容验证（旧 JSONL 无新值，ReverseMap 单向安全） | E1 加单测验证 |
| 4 | 云端 JSON 未知字段容错未确认（影响 E4 上传开关默认值——默认关的理由） | E5a 前核实 |
| 5 | AGE 版本与 PG16 兼容矩阵未验证（apachear/age 官方镜像支持 PG16；E5b-G2 前核对 AGE 1.5+ 与 postgres:16-bookworm 镜像组合） | G2 触发时核对 |
| 6 | 演化记录入检索库（SqliteRetrievalStore）的文档域/种类划分未设计 | E2 按 retrieval_documents 现有 metadata 结构定 |
| 7 | `SyncDocumentEnumerator` 分类分支：appearances/evolution 新子目录文件是否被既有 Classify 逻辑正确分类为 typed document（而非降级 file） | E1 读 `Classify` 函数验证，需加分支则加——本地侧唯一随图谱决策产生的实施项 |
| 8 | `云端图谱投影器` 的 YAML schema 漂移防护：本地字段演化（Models.cs 加字段）与云端解析器的版本对齐 | 建议 content-hash 缓存键 + 解析失败降级跳过该文件并上报 warning（沿用 setup.check「无法解析的引用不报，宁可漏」先例） |
| 9 | AssertedFacts L1 与演化事件的联动（变化事件可升格为断言源，补 L1 空跑） | 开放（远期） |
