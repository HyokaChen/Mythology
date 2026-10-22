# Tool Search：按需检索工具注入方案

> 状态：已实现（P0~P3 全部落地；2026-08-23 起默认延迟名单收窄为 Semantic Retrieval Tool / Web Search Tools + 第三方插件，核心写作/分析工具常驻注入）。
> embedding 经 `Core.Vector.EmbeddingService`（本地 LlmTornado，复用聊天 Provider 或 config.yaml 独立 embedding 密钥）提供；未配置模型名时 `DummyEmbeddingService` 降级纯 BM25（`ToolSearchEngine` 显式跳过 dense 通道）。云端 `CloudEmbeddingClient` 未实现。
> 关联问题（立项背景）：84 个工具全量注入 → 上下文膨胀、工具选择准确率下降、幻觉调用（如 LLM 用 bash 内省 jsonargparse）。当前注册表 116 个工具。
> 关联组件：`ConversationLoop.BuildChatRequest`、`ToolRegistry`、`ToolSearchEngine`、`ToolInjector`、`ToolSearchTool`、`Core.Vector.EmbeddingService`/`DummyEmbeddingService`（降级纯 BM25）

---

## 目录

1. [背景与问题](#1-背景与问题)
2. [借鉴：Claude Code / Anthropic Tool Search](#2-借鉴claude-code--anthropic-tool-search)
3. [方案总览](#3-方案总览)
4. [索引构建](#4-索引构建)
5. [检索算法：jieba + BM25 + Embedding 混合](#5-检索算法jieba--bm25--embedding-混合)
6. [本地 Embedding 模型选型](#6-本地-embedding-模型选型)
7. [与 LlmTornado 的集成](#7-与-llmtornado-的集成)
8. [Tool Search 工具本身的定义](#8-tool-search-工具本身的定义)
9. [动态注入流程](#9-动态注入流程)
10. [缓存与降级](#10-缓存与降级)
11. [风险与缓解](#11-风险与缓解)
12. [实施计划](#12-实施计划)
13. [验证与评估](#13-验证与评估)
14. [参考](#14-参考)

---

## 1. 背景与问题

### 1.1 现状

Mythology 当前把**所有已注册工具全量注入** LLM 上下文：

```csharp
// src/Core/Agent/ConversationLoop.cs BuildChatRequest()
var tools = _toolRegistry.GetAll();   // 当时 84 个工具（现 116）
foreach (var tool in tools) { ... llmTools.Add(new Tool(...)); }
request.Tools = llmTools;
```

工具规模（`src/Plugins/*/xxxPlugin.cs`）：

| 插件 | 工具数 | 说明 |
|---|---|---|
| NovelTools | 64 | 角色/场景/伏笔 CRUD + 20+ 分析引擎 |
| FileSystem | 7 | read/write/edit/bash/todo_write/ask_user/get_pending_questions |
| WebSearchTools | 6 | 搜索/查词/风格推荐/评论 |
| SemanticRetrievalTool | 5 | 记忆/一致性检索 |
| WritingTools | 2 | 诊断/增强 |
| **合计** | **84** | 每个工具携带完整 JSON Schema |

> **现状（2026-08-30 校准）**：上表为立项时快照。当前注册表 116 个唯一工具名——NovelTools 67（含 23 个 `CloudAnalysisTool` 云端分析器）、WritingTools 10（1 诊断 + 9 增强）、SemanticRetrievalTool 8（新增 recall_scene/recall_timeline/recall_related）、WebSearchTools 6、FileSystem 6（`BashTool` 已实现但未注册）、Core 组 20（tool_search + decompress + 13 AgentMemory + 4 偏好 + tool_analyze_style）；NovelTools 与 WebSearchTools 的 `web_search` 同名，注册表按名覆盖去重。

### 1.2 问题

1. **上下文膨胀**：84 个工具（现 116）的 schema 注入消耗大量 token，挤占对话历史与创作内容的空间。
2. **工具选择准确率下降**：Anthropic 实测，工具数超过 30~50 时模型选错工具的比率显著上升（Context Rot 效应）。
3. **幻觉调用**：工具列表过大时，模型可能生成与任务无关的调用——例如真实日志中，LLM 在"检查角色完整性"任务下用 `bash` 执行 `python3 -c "from jsonargparse import Action_validation, ..."`（与任务完全无关的库内省），且因 `BashParams.Env` 类型不符而失败。根源之一即全量注入导致的选择噪声。
4. **bash 成为"万能出口"**：bash 描述宽泛，模型一旦产生"探索/验证"念头便倾向使用它。

### 1.3 目标

- 让 LLM 每次请求前只看到**当前任务相关的工具子集**（目标 3~8 个）。
- 保留完整工具库可达性：模型可通过 `tool_search` 工具按需发现更多工具。
- 显著减少每次请求的 token 消耗，提升工具选择准确率，抑制幻觉调用。
- 全部逻辑本地化：**jieba 分词 + BM25（内存倒排索引）+ Embedding 混合**，不依赖云端 embedding API。

---

## 2. 借鉴：Claude Code / Anthropic Tool Search

### 2.1 Anthropic 官方 Tool Search Tool（2025-11 发布）

Anthropic 在 [Advanced Tool Use](https://www.anthropic.com/engineering/advanced-tool-use) 中引入 **Tool Search Tool**，核心机制：

- **`defer_loading: true`** 标记的工具**不预载入**上下文，只保留轻量搜索索引。
- 模型先看到 **Tool Search Tool 本身 + 少量核心工具**（3~5 个，`defer_loading: false`）。
- 模型需要能力时**主动调用 Tool Search Tool** 搜索（官方提供 regex 与 BM25 两种内置变体），命中的工具引用**展开为完整定义**注入上下文。
- 效果：上下文占用从 ~77K tokens 降至 ~8.7K（**~85% 削减**）；MCP 场景从 55K 降至 ~500 tokens（**~95%**）。
- 准确率提升：Opus 4 工具选择准确率 49% → 74%，Opus 4.5 79.5% → 88.1%。
- 触发建议：工具定义 >10K tokens、工具数 >10、存在相似名称工具、出现选择准确率问题。

官方实现示意：

```json
{
  "tools": [
    {"type": "tool_search_tool_regex_20251119", "name": "tool_search_tool_regex"},
    {"name": "github.createPullRequest", "description": "Create a pull request", "input_schema": {...}, "defer_loading": true}
  ]
}
```

### 2.2 Claude Code 的 MCP Tool Search（v2.1.7+）

Claude Code 将同一思想落地为 MCP 服务器的**懒加载**：

- **自动触发**：当 MCP 工具描述占用上下文 >10% 时自动切换为搜索模式（`auto:N` 可自定义阈值）。
- **注册表替代全量加载**：启动时只加载轻量工具注册表（~5K tokens），工具按需加载。
- **智能缓存**：已加载工具在会话内保持可用，不重复展开。
- **server instructions** 字段：描述服务器能力与触发关键词，帮助模型判断何时搜索。
- 实测：7+ MCP 服务器场景下，初始上下文 92K → 195K tokens 可用（**可用率 46% → 97.5%**）。

### 2.3 Codex（OpenAI）的差异

OpenAI Codex CLI **不采用检索式工具注入**：其内置工具集小（bash、文件编辑等），并以"最小工具面 + 明确权限"为设计哲学。**对 Mythology 的参考价值有限**——我们的场景是 116 个领域工具（NovelTools 就 67 个），与 Codex 的"少量通用工具"完全不同，因此主要借鉴 Anthropic 的 Tool Search 模式。

### 2.4 提炼到本项目的设计原则

| Anthropic/Claude Code 原则 | 本项目落地 |
|---|---|
| `defer_loading` 延迟加载 | `AgentTool.DeferLoading`（见 §7） |
| 3~5 个核心工具始终加载 | 常驻集合：`tool_search`、`ask_user`、`todo_write` |
| 搜索工具预载，命中后展开 | `tool_search` 工具本身始终注入 |
| 官方 BM25/regex 变体 | 自研 **jieba + BM25（内存倒排索引）+ embedding 混合**（§5） |
| 10% 上下文阈值自动触发 | 可配置阈值（默认：工具 schema 总长 > 8K 字符即启用） |
| 命中结果缓存 | 会话内工具展开缓存（§10） |
| 搜索匹配 name + description | 索引字段 = name + label + description + 参数名 + 示例（§4） |

---

## 3. 方案总览

```
┌──────────────────────────────────────────────────────────────────┐
│                      ConversationLoop                            │
│                        BuildChatRequest                          │
│   ┌─────────────┐   ┌────────────────────────────────────────┐   │
│   │ ToolRegistry │   │  ToolInjector（新增）                   │   │
│   │(116 tools)  │──▶│  · 全量工具注册（不注入）                 │   │
│   └─────────────┘   │  · 常驻工具子集注入（tool_search + 核心） │   │
│                     │  · 会话内已展开工具注入（缓存）           │   │
│                     └───────────────┬────────────────────────┘   │
└─────────────────────────────────────┼────────────────────────────┘
                                      │
                    ┌─────────────────▼─────────────────┐
                    │         ToolSearchEngine           │  (新增, Core)
                    │  ┌───────────┐  ┌───────────────┐  │
                    │  │ ToolIndex │  │ 检索器         │  │
                    │  │ (jieba 分词│  │ · BM25(内存  │  │
                    │  │  + 索引表) │  │ · Dense(本地  │  │
                    │  └───────────┘  │   embedding)  │  │
                    │                 │ · RRF 融合     │  │
                    │                 └───────────────┘  │
                    └─────────────────┬─────────────────┘
                                      │ 返回命中的工具名列表
                    ┌─────────────────▼─────────────────┐
                    │    ToolSearchTool（AgentTool）      │  (新增, Plugins)
                    │  query: 自然语言/关键词             │
                    │  → 返回 工具名 + 描述摘要（供 LLM 决定）│
                    └────────────────────────────────────┘
```

**两种接入模式（详见 §7）**：

- **模式 A（推荐，自研）**：`tool_search` 是普通 AgentTool，命中后由 `ToolInjector` 把完整 schema 追加到后续请求的 `request.Tools`。不依赖 LlmTornado 的 server 端 tool search（对 OpenAI/自定义 provider 兼容性好）。
- **模式 B（若使用 Anthropic provider）**：利用 LlmTornado 原生 `DeferLoading` + `ResponseToolSearchTool`（server 执行），由 Anthropic 侧完成展开。作为 A 的补充。

---

## 4. 索引构建

### 4.1 索引字段

为每个工具构建**检索文档**（索引构建于启动时，一次完成）：

| 字段 | 来源 | 权重（BM25 加权） |
|---|---|---|
| `name` | `AgentTool.Name` | 高（精确匹配优先） |
| `label` | `AgentTool.Label`（中文标签，如"管理任务"） | 高 |
| `description` | `AgentTool.Description` | 中 |
| `parameters` | schema 中的参数名 + `Description` | 低 |
| `aliases` | 手动补充的同义词（见 §4.3） | 中 |
| `examples` | 手动补充的调用示例（见 §4.3） | 低 |

> **为什么加 label/aliases**：Mythology 的工具名是英文（`check_character_completeness`），但用户与模型推理是中文（"检查角色完整性"）。**中文检索必须依赖 label + aliases 的中文关键词**，纯英文 description 无法召回。

### 4.2 文档示例

```
doc(name=check_character_completeness):
  name: check_character_completeness
  label: 检查角色完整性
  description: 对角色执行 20 项完整性检查（身世/动机/弧光/关系网等），返回缺失项清单
  aliases: 角色完整性, 角色检查, completeness, 二十问, 人物设定检查
  parameters: novel_id, character_id, [character_name]
  examples: 检查主角「林晚」的设定是否完整
```

### 4.3 别名与示例的来源

- **内置种子**：为 NovelTools 的高频工具在插件代码中声明 `Aliases` / `ExampleQueries`（新增 `AgentTool` 虚属性，插件可覆盖）。
- **可扩展**：未来可支持 `tools.aliases.json` 用户自定义映射（本期不做，避免过度设计）。

### 4.4 索引存储（已实现：纯内存，非 SqliteRetrievalStore）

**实际实现为 `ToolSearchEngine` 内纯内存索引**（工具文档量极小 ~116 条，不需要重型 DB 管线）：

- 工具文档 → `IndexSnapshot.Documents`（Dictionary<string, ToolDocument>）。
- 倒排索引 → `IndexSnapshot.InvertedIndex`（token → postings 列表），BM25 直接内存计算。
- dense 向量 → `IndexSnapshot.Embeddings`（含预计算范数），查询时只算点积。
- 索引构建采用 **swap-on-build**：构建到局部快照，完成后原子发布 `_snapshot`，读取方始终看到完整一致的索引；`InvalidateIndex()` 置空快照，下次搜索前由 `ToolInjector.EnsureIndexedAsync` 惰性重建。

> 原始设计（复用 `SqliteRetrievalStore` + FTS5 + vec0）在实现时被更轻量的内存方案取代——RRF 融合算法（k=60 可配）仍与 `SqliteRetrievalStore.RrfFusion` 一致。

---

## 5. 检索算法：jieba + BM25 + Embedding 混合

### 5.1 算法

```text
    ├─ 1. jieba 分词（共享 Vector.AgentMemoryBm25.Segmenter 的 jieba.NET 单例）
    │      → 词序列 + 去停用词
    │
    ├─ 2. BM25 词法检索（内存倒排索引，jieba 分词）
    │      → 候选工具（top 20）
    │
    ├─ 3. Dense 向量检索（EmbeddingService 外部接口；不可达 30s 冷却降级纯 BM25）
    │      → query embedding vs 工具文档 embedding 余弦相似度
    │      → 候选工具（top 20）
    │
    └─ 4. RRF 融合（Reciprocal Rank Fusion, k=60）
            → 最终排序，取 top K（默认 5）
```

### 5.2 为什么混合而非单一

| 方式 | 优点 | 缺点 |
|---|---|---|
| 纯 BM25 | 精确关键词匹配，工具名/别名命中率高，零延迟 | 同义改写（"人物设定" vs "角色背景"）召回差 |
| 纯 Dense | 语义召回强，能处理改写 | 对精确工具名（`check_character_completeness`）不敏感；需要本地模型；有量化损失 |
| **混合 + RRF** | 两者互补：精确名靠 BM25，语义靠 Dense | 实现略复杂（RRF 已有现成实现） |

### 5.3 中文分词复用

`ToolSearchEngine.Tokenize` 直接共享 `Vector.AgentMemoryBm25.Segmenter` 的 jieba.NET 单例（`Lazy<JiebaSegmenter>`，线程安全），停用词过滤与 `AgentMemoryConflict.Stopwords` 一致（原设计引用的 `ChineseTokenizer` 已在 CHANGE_07 移除，位置并入 Core.Vector）。

---

## 6. Embedding 接口（本地 LlmTornado，显式 opt-in）

### 6.1 需求

- 中文语义检索，维度与外部模型一致（bge-m3 为 1024、qwen-embedding 等按服务方定义）。
- **本地零配置即可运行**：未配置 embedding 时不发任何远程 embedding 请求，检索退回纯词法（BM25/FTS5），功能不中断。

### 6.2 本地配置（`~/.mythology/config.yaml` 的 `Embedding` 段）

```yaml
embedding:
  default: openai
  default_model: text-embedding-3-small
  api_keys:
    - provider: openai
      api_key: sk-...
    - provider: custom
      name: my-bge
      api_key: ...
      base_url: https://api.example.com/v1   # OpenAI 兼容端点（bge-m3 / qwen-embedding / vLLM / Ollama 等）
```

TUI 设置对话框「嵌入」标签页（`EmbeddingTab`）与 Desktop 设置页写同一份配置；`RetrievalKernel.UpdateEmbeddingService` 支持冷重配置（仅在 ConversationLoop 停止时），维度不一致时拒绝热替换并提示重启重建索引。

### 6.3 实际实现与选择优先级

`CoreServiceExtensions.RegisterRetrievalAsync` 按优先级选择 `IEmbeddingService`：

1. 入口注入的 `embeddingFactory`（测试覆盖用；Desktop 当前传 `null`）
2. `config.yaml` `Embedding` 段独立密钥 → `EmbeddingService(embeddingApi, model)`
3. 回退复用聊天 TornadoApi → `EmbeddingService(tornadoApi, model)`
4. 以上都不满足 → `DummyEmbeddingService`（降级）

- 实现类：`Core.Vector.EmbeddingService`（LlmTornado，`IEmbeddingService` + `IBatchEmbeddingService`，维度由首次成功调用缓存）。
- 向量维度固定 `VectorDefaults.Dimension = 1024`——**不做启动探测**（探测是启动链路上的远程调用，远端不可达会拖住入口；维度不一致在真实索引时显式失败）。
- `CloudEmbeddingClient`（ClientPlatform 调云端 `/v1/embedding`）**未实现**：`src/ClientPlatform/` 中不存在该客户端，`/v1/embedding` 端点在本仓库无消费方。

### 6.4 降级链

```
embedding 已配置且可达 → jieba + BM25 + dense + RRF（完整）
DummyEmbeddingService  → ToolSearchEngine 显式跳过 dense 构建（纯 BM25）
embedding 调用失败     → 该次降级纯 BM25，dense 失败后 30s 冷却
零向量查询             → RetrievalKernel 设 DenseTopK=0，纯词法（FTS5）
```

> 工具文档仅 ~116 条，embedding 一次构建后常驻内存缓存（`ToolSearchEngine` 持有），
> 运行时查询只需 embed query 一次（每轮 1 次网络往返）；未配置/不可达自动降级纯 BM25。

---

## 7. 与 LlmTornado 的集成

### 7.1 现状

`ConversationLoop.BuildChatRequest` 全量注入：

```csharp
var tools = _toolRegistry.GetAll();
foreach (var tool in tools) { if (tool.Parameters.HasValue) llmTools.Add(new Tool(...)); }
request.Tools = llmTools;
```

### 7.2 改造点

新增 **`ToolInjector`**（Core 层），职责：

```csharp
    // 实际为 sealed class ToolInjector，成员摘录
    IReadOnlyDictionary<string, AgentTool> ExpandedTools { get; }

    // 注入面签名：registry 版本 + 展开集版本计数（O(1)，Volatile.Read；ConversationLoop 据此热刷新）
    string ToolsSignature { get; }
    IReadOnlyDictionary<string, AgentTool> ExpandedTools { get; }

    // 注入面签名：registry 版本 + 展开集指纹（ConversationLoop 据此热刷新）
    string ToolsSignature { get; }

    // 根据 query 检索并展开（ToolSearchTool 先检索再调 ExpandHits，避免双重检索）
    Task<IReadOnlyList<string>> ExpandAsync(string query, int topK, CancellationToken ct);
    IReadOnlyList<string> ExpandHits(List<ToolSearchHit> hits);
    Task EnsureIndexedAsync(CancellationToken ct);

    // 构建每次请求的工具列表 = 常驻 + 已展开 + 非延迟工具（含签名缓存）
    IReadOnlyList<AgentTool> BuildRequestTools();
}
```

`BuildChatRequest` 改为：

```csharp
var tools = toolInjector.BuildRequestTools();   // 常驻 + 已展开
foreach (var tool in tools) { ... llmTools.Add(new Tool(...)); }
request.Tools = llmTools;
```

### 7.3 常驻工具集合（defer_loading: false 等价物）

| 工具 | 理由 |
|---|---|
| `tool_search` | 发现机制本身，必须可见 |
| `ask_user` | 人机交互核心，任意任务都可能需要 |
| `todo_write` | 计划/进度追踪（PlanningLoopPrompt 强制要求） |

> 实际常驻集合为 3 个（`ToolInjector.DefaultAlwaysLoaded`）。`bash` 未进常驻——且 `FileSystemPlugin.GetTools()` 当前根本不注册 `BashTool`（类已实现，注册缺失）。

### 7.4 模式 A：自研工具展开（推荐，默认）

- `tool_search` 是**普通 AgentTool**（§8），对所有 provider 生效。
- 模型调用 `tool_search(query)` → `ToolSearchEngine` 检索 → 命中工具名列表返回给模型（含描述摘要）。
- 同时 `ToolInjector.ExpandHits(hits)` 将命中工具的**完整 schema** 加入 `ExpandedTools`，**下一轮请求自动注入**。
- 模型看到搜索结果后，直接以完整 schema 调用目标工具（无需再等一轮——因为下一轮请求已带上）。

### 7.5 模式 B：LlmTornado 原生 defer_loading（Anthropic provider 时可选）

LlmTornado 已支持（`LlmTornado/Common/Tool.cs` 的 `DeferLoading`、`Responses/ResponseToolSearch.cs` 的 server/client 两种执行模式）：

```csharp
llmTools.Add(new Tool(new ToolFunction(...)) { DeferLoading = true });
```

- 若 `LLM:Provider = anthropic` 且模型支持 tool search，可直接标记 defer 并让 **server 端执行展开**（模式 B 零本地检索）。
- 若使用 OpenAI/自定义 provider，退回模式 A。
- **本期以模式 A 为主实现，模式 B 作为 provider 适配增强**（因为用户指定 jieba+BM25+本地 embedding 的自研方案）。

---

## 8. Tool Search 工具本身的定义

```csharp
public sealed class ToolSearchTool(ToolSearchEngine engine, ToolInjector injector, int? rateLimit = null)
    : AgentTool<ToolSearchParams, ToolSearchDetails>
{
    public override string Name => "tool_search";
    public override string Label => "搜索工具";
    public override string Description =>
        "从工具库中按需检索工具。当需要某个能力但当前工具列表中找不到时调用，"
        + "传入自然语言描述你需要的操作（如“检查角色完整性”“生成伏笔”），"
        + "返回匹配的工具名、用途和调用提示。命中后其完整定义会自动加入可用工具。";
}

public sealed record ToolSearchParams
{
    [Description("自然语言描述你需要的操作或能力，支持关键词。如：“检查角色完整性”“创建场景”“搜索记忆”")]
    public string Query { get; init; } = "";

    [Description("返回结果数（默认 5，最大 10）")]
    public int? TopK { get; init; }
}


public sealed record ToolSearchDetails
{
    public string[] ToolNames { get; init; } = [];
}
```

**返回给模型的内容**（非完整 schema，控制 token）：

```
找到 3 个相关工具：
1. check_character_completeness — 检查角色完整性
   [参数] novel_id, character_id
2. analyze_genius_character — 分析高智商角色行为逻辑
   [参数] novel_id, character_id
3. detect_inconsistencies — 检测设定矛盾
   [参数] novel_id
调用任意一个后其完整参数定义会立即加载到可用工具列表。
```

---

## 9. 动态注入流程

```
用户: "检查一下主角林晚的设定完整吗？"
  │
  ▼
BuildChatRequest（本轮）
  tools = [tool_search, ask_user, todo_write]   ← 只注入常驻 3 个
  │
  ▼
LLM 推理：需要"角色完整性检查"能力 → 当前列表没有 → 调用 tool_search
  tool_search(query="检查角色完整性", top_k=5)
  │
  ▼
ToolSearchEngine：jieba 分词 → BM25 + dense → RRF → top5
  命中: check_character_completeness(0.91), analyze_genius_character(0.83), ...
  │
  ▼
ToolInjector.ExpandHits(hits)  ← 完整 schema 加入 ExpandedTools（会话内缓存，_toolsVersion++ 触发热刷新）
  返回搜索结果文本给模型
  │
  ▼
下一轮 BuildChatRequest
  tools = [tool_search, ask_user, todo_write, check_character_completeness, ...]
  │
  ▼
LLM 直接调用 check_character_completeness(novel_id=..., character_id=...)  ← 正常执行
```

**关键点**：`tool_search` 命中后**下一轮自动带上完整 schema**，模型无需手工拼参数——这复刻了 Claude Code"展开工具定义"的语义，且对 LlmTornado 的 `ChatRequest.Tools` 机制零侵入。

---

## 10. 缓存与降级

### 10.1 工具展开缓存

- `ExpandedTools` 在**会话内持久**：已展开的工具后续请求持续注入，模型不再重复搜索（Claude Code 同款"智能缓存"）。
- 会话切换（`OpenNovelAsync`）时清空——不同小说的工具需求不同。
- 可选上限（如 20 个展开工具）：超限按 LRU 淘汰最久未用的。

### 10.2 索引构建缓存

- 工具文档 embedding 一次构建，进程内缓存（`ToolIndex` 单例）。
- 插件热加载/卸载（`PluginLoader`）时重建索引。

### 10.3 降级链

```
本地 embedding 可用 → jieba + BM25 + dense + RRF（完整）
本地 embedding 失败 → jieba + BM25（FTS5 词法）    ← 功能不中断
jieba 失败          → unicode61 tokenizer + BM25
最坏情况            → 返回常驻工具（功能可用，只是发现能力减弱）
```

---

## 11. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| **漏召回**：模型需要的工具没被检索到 | 模型误以为能力不存在 | ① 别名表人工维护高频工具；② 返回 top 之外附带"共 N 个匹配"提示；③ 检索空结果时自动注入全部 EntityCrud 类工具兜底 |
| **多一轮延迟**：先 search 再调用 | 首次使用某工具多 1 轮 | 常驻工具覆盖高频操作；命中缓存避免重复搜索；可配置"首轮全量注入，后续轮次切检索"的混合启动（灰度用） |
| **工具 schema 展开后上下文反弹** | 上下文节省被抵消 | 展开上限 + LRU 淘汰；只展开命中工具的 schema，不展开同插件全量 |
| **bash 仍可能被幻觉调用** | 安全风险 | tool_search 模式下 bash 默认不进常驻；若需要，靠检索召回 + 描述强化"仅用于项目文件操作/构建" |
| **本地模型下载/首次加载慢** | 启动延迟 | 模型文件缓存到 `~/.mythology/models/`；异步预热；未就绪时走 BM25-only |
| **tool_search 本身被滥用**（模型每轮都搜） | 反而增加 token | prompt 明确"仅当目标工具不在当前列表时搜索"；统计搜索频率做熔断提示 |
| **多 provider 兼容** | 模式 A 依赖自身逻辑 | 模式 A 与 provider 无关；模式 B 仅作 Anthropic 增强 |

---

## 12. 实施计划

| 阶段 | 内容 | 涉及文件 | 验证 |
|---|---|---|---|
| **P0 基建 ✅** | `AgentTool` 增加 `Aliases`/`ExampleQueries`/`DeferLoading` 虚属性；`ToolSearchEngine`（jieba 分词 + BM25 + dense + RRF）；embedding 走本地 `EmbeddingService`（云端 CloudEmbeddingClient 未实现，不在本链路） | `src/Core/Tools/AgentTool.cs`、`src/Core/Tools/ToolSearchEngine.cs`（新）、`云端私有仓库: 云端 API/Modules/EmbeddingModule.cs`（部分） | 单元测试：索引/检索/RRF/降级 |
| **P1 工具 ✅** | `ToolSearchTool`（AgentTool）；`ToolInjector`（常驻 + 展开 + LRU + 延迟插件名单）；`ToolSearchParams`/`ToolSearchDetails` | `src/Core/Tools/ToolSearchTool.cs`（新）、`src/Core/Tools/ToolInjector.cs`（新） | 单元测试：search 返回正确工具 |
| **P2 集成 ✅** | `BuildChatRequest` 改用 `ToolInjector.BuildRequestTools()`（含 Agent 透传）；会话切换（CurrentNovelId 变化）清空 ExpandedTools + 重置熔断；prompt 增加 tool_search 使用规则；默认延迟 4 插件（2026-08-23 起收窄为 Semantic Retrieval Tool / Web Search Tools + 第三方） | `src/Core/Agent/ConversationLoop.cs`、`src/Core/Agent/Agent.cs`、`src/Core/Prompts/ToolCallingPrompt.cs`、`src/Host/HostBootstrapper.cs` | 单元测试：注入列表正确；tool_search 展开后下一轮注入 |
| **P3 打磨 ✅** | 25 个高频 NovelTools 工具补中文 Aliases/ExampleQueries；jieba 分词替换单字切分（jieba.NET 0.42.2，词典复制 Target）；搜索频率熔断（>8 次/会话提示收敛，会话切换重置）；灰度开关 `Agent:ToolSearchMode`（dynamic/all） | NovelTools 各工具类、`src/Core/Tools/ToolSearchEngine.cs`、`src/Core/Tools/ToolSearchTool.cs`、`src/Core/Tools/ToolInjector.cs`、`src/Host/HostBootstrapper.cs` | 单元测试：别名召回/熔断/灰度开关；全量回归 |

> embedding 经 `Core.Vector.EmbeddingService`（本地 LlmTornado，复用聊天 Provider key 或 config.yaml 独立 embedding 密钥）提供；未配置模型名时 `DummyEmbeddingService` 降级纯 BM25/FTS（`ToolSearchEngine` 显式跳过 dense 构建，`RetrievalKernel` 零向量 → DenseTopK=0）；dense 失败 30s 冷却。云端 `/v1/embedding` 客户端（CloudEmbeddingClient）未实现。

---

## 13. 验证与评估

### 13.1 单元测试

- **索引**：注册表全部工具（现 116）入索引，无遗漏；字段拼接正确。
- **检索**：中文 query（"检查角色完整性"）命中 `check_character_completeness`；英文工具名 query（"batch_create"）命中 `batch_create_characters`；别名 query（"人物设定"）命中对应工具。
- **RRF 融合**：BM25 与 dense 结果融合排序正确。
- **降级**：embedding 失败 → BM25-only 仍能召回。

### 13.2 集成测试

- mock 会话：用户消息 → LLM（mock）调用 `tool_search` → `ExpandedTools` 增加 → 下一轮 `BuildRequestTools()` 包含命中工具。
- 断言注入的工具数量 < 全量注册数（默认动态模式下延迟插件的工具不在注入面内）。

### 13.3 效果指标（手工/A-B 观察）

| 指标 | 现状 | 目标 |
|---|---|---|
| 每轮工具 schema token | ~84 工具全量 | 常驻 3 + 展开 ≤20 + 非延迟工具（默认名单下约 84 个；核心工具常驻是 2026-08-23 的有意取舍，见 §15） |
| 工具选择准确率 | 偶发幻觉（如 bash 跑 python） | 无幻觉调用 |
| 首轮上下文占用 | 全量 | 削减 ≥ 80% |
| 创作主流程延迟 | 无 search 开销 | 首次多 ≤1 轮，命中后无额外开销 |

---

## 14. 参考

- [Anthropic: Introducing advanced tool use](https://www.anthropic.com/engineering/advanced-tool-use) — Tool Search Tool / `defer_loading` / BM25 与 regex 变体 / 实测数据
- [Claude Platform Docs: Tool search tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)
- [Claude Cookbook: Tool search with embeddings](https://github.com/anthropics/claude-cookbooks/blob/main/tool_use/tool_search_with_embeddings.ipynb)
- [Claude Code: MCP Tool Search 机制解析](https://kuanhaohuang.com/mcp-tool-search-claude-code/) — v2.1.7 自动触发 / 10% 阈值 / 95% 减负
- [Claude Fast: MCP Tool Search 配置](https://claudefa.st/blog/tools/mcp-extensions/mcp-tool-search) — enable_tool_search / 前后对比数据
- [Instruction-Tool Retrieval (ITR)](https://arxiv.org/html/2602.17046v1) — 动态 system prompt 片段 + 工具子集的学术方案
- [RAG-MCP](https://arxiv.org/html/2505.03275v1) — 语义检索做 MCP 工具发现的学术方案
- [VoltAgent: Tool Routing](https://voltagent.dev/blog/tool-routing/) — 小搜索面 + 大隐藏工具池的工程实践
- 项目内：`src/Core/Agent/ConversationLoop.cs`、`src/Core/Vector/SqliteRetrievalStore.cs`、`src/Core/Vector/AgentMemoryBm25.cs`（共享 jieba 单例）、`LlmTornado/src/LlmTornado/Common/Tool.cs`

---

> 本文档为设计提案（P0~P3 已全部落地）。任何阶段发现检索质量不达标，优先扩充别名表而非更换算法。

---

## 15. 落地后的演进（2026-08-23 校准）

- **默认延迟名单收窄**：从立项时的 4 插件（Novel Writing Tools / RAG Tools / Writing Enhancement Tools / Web Search Tools）收窄为 `Semantic Retrieval Tool` + `Web Search Tools`（`HostBootstrapper` 内置默认，`Agent:ToolSearchDeferredPlugins` 可覆盖；Desktop/Host `appsettings.json` 同值）。原因：核心写作/分析工具（write_chapter、diagnose_writing、evaluate_* 等）被延迟后，模型偶尔跳过 tool_search 直接文字收尾，造成"写完不诊断就中断"的体验问题；主链路工具改为常驻。
- **代价与现状**：默认动态模式每轮注入约 84 个工具 schema（不再是本文最初设想的 ~8 个）。tool_search 的价值集中在检索/网络/第三方插件与 18 个 DeferLoading 记忆/偏好工具上。
- **注入面热刷新**：`ToolInjector.ToolsSignature = registry.Version:toolsVersion`（O(1) 版本计数），`ConversationLoop` 据此重建 Conversation，使新工具在本 run 内可用。
- **熔断可配**：`tool_search` 熔断阈值默认 8 次/会话，可经 `Agent:ToolSearchRateLimit` 覆盖；`CurrentNovelId` 变化时 `ClearExpanded()` + `ResetSearchTrip()`。
