# Mythology 架构设计

> 本文档整合了整体架构、Agent/ToolSearch、云端协作、会话架构、Provider 配置、快捷键/命令参考、实体引用系统等核心设计。
> 最后更新：2026-08-30（工具数/注册表对齐源码：116 注册；embedding 现状校准：本地 LlmTornado，CloudEmbeddingClient 未实现；Desktop 已入 .slnx + CloudDiagnosis；默认延迟名单收窄；LlmTornado 3.8.67）

---

## 目录

1. [整体架构](#1-整体架构)
2. [三层存储架构](#2-三层存储架构)
3. [核心组件](#3-核心组件)
4. [数据层](#4-数据层)
5. [插件系统](#5-插件系统)
6. [LLM 集成](#6-llm-集成)
7. [云端协作（云端私有仓库）](#7-云端协作)
8. [TUI 布局概览](#8-tui-布局概览)
9. [稳定性监控系统](#9-稳定性监控系统)
10. [Session 架构（JSONL + 分支树）](#10-session-架构jsonl--分支树)
11. [Provider 配置体系](#11-provider-配置体系)
12. [快捷键与命令参考](#12-快捷键与命令参考)
13. [实体引用系统（# 补全）](#13-实体引用系统-补全)
14. [数据迁移](#14-数据迁移)
15. [审计与完成度检查](#15-审计与完成度检查)

---

## 1. 整体架构

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Mythology (.NET Tool)                           │
│               Host + TUI/Desktop → dotnet tool install --global         │
└─────────────┬──────────────┬──────────────┬──────────────┬──────────────┘
              │              │              │              │
┌─────────────▼────┐  ┌──────▼──────┐  ┌──────────────┐  ┌───────────────┐
│   Mythology.Core │  │   Tui/      │  │  Plugins/    │  │  ClientPlatform│
│   (NuGet 包)     │  │   Desktop   │  │  (运行时 DLL)│  │ (云端客户端库) │
│   契约 + 数据层  │  │ XenoAtom /  │  │  NovelTools  │  │ CloudEmbedding │
│   INovelStore    │  │ Avalonia    │  │  SemanticRetrievalTool   │  │ RetrievalRest │
│   FileNovelStore │  │ 终端/GUI    │  │  WebSearch  │  │ FictionHarness │
│   FileSessionStore│ │ 云端登录    │  │  FileSystem │  │ DeviceAuth     │
│   RetrievalKernel│  │ 补全/分支   │  │  WritingTools│  │ OAuthLoopback  │
│   AgentMemoryStore│ │ compact    │  │              │  └───────┬───────┘
│   ToolSearchEngine│ │            │  │              │          │
└──────────────────┘  └────────────┘  └──────────────┘          │
              │                                                 │
              └─────────────── 云端私有仓库 ──────────────────┘
                    /v1/embedding · /v1/retrieval · /v1/agent/runs · /v1/device/*
```

**组件职责**：

| 组件 | 说明 |
|------|------|
| **Core** | 插件契约（`IToolPlugin`）、数据层（`INovelStore` / `FileNovelStore` / `FileSessionStore`）、Agent 引擎（`ConversationLoop` + `ToolInjector`）、**按需工具检索**（`ToolSearchEngine` + `ToolSearchTool`）、**共享检索内核**（`RetrievalKernel` + `IRetrievalStore`，向量 dense + FTS5 词法 + metadata 过滤 + RRF 融合）、**Agent 记忆子系统**（`AgentMemoryStore`：精确混合评分召回 + 反馈学习 + spaced repetition，13 个 `AgentMemoryTools` 记忆工具 + 4 个偏好工具 + `tool_analyze_style` + `AgentMemoryHook` 自动标记）、稳定性监控接口（`IStabilityMonitor`，具体实现在 StabilityHarness） |
| **Tui** | XenoAtom.Terminal.UI 终端界面，含对话面板、设定面板、Todo 面板、状态栏、输入编辑器、云端登录对话框（本地回调 OAuth → 设备流 → 手动 Key 三态）、设置对话框（连接器 / 嵌入 / MCP / 工具四标签页）、Desert 主题 |
| **Desktop** | Avalonia 12 + ReactiveUI + SukiUI GUI（`src/Desktop/`，**已入 .slnx**），复用 Core 共享注册（`CoreServiceExtensions`：`INovelStore` / Agent / plugins / `RetrievalKernel`）：MainWindow / ConversationView / SetUpPanelView / TodoPanelView / SettingsView，经 `DesktopAgentBridge` + `StreamEventHandler` 桥接 Agent，`NovelContextHook` 注入小说上下文，`CloudAuthService` 云端账号门面，装配 `CloudDiagnosis` 诊断链路。注意：Desktop 未接 ToolInjector / tool_search / decompress / AgentMemory 工具注册（仅 Host 装配） |
| **ClientPlatform** | 云端客户端库（`ClientPlatform`）：`RetrievalRestClient`（/v1/retrieval REST，扣费）、`HttpFictionHarnessClient`（/v1/agent/runs SSE）、`DeviceAuthorizationClient`（RFC 8628 设备码）、`OAuthLoopbackClient`（本地回调 OAuth + PKCE）、`CloudSessionStore`（DPAPI 加密凭据）、`CloudEndpoints`/`CloudTimings` 收口。纯 HttpClient，无 LlmTornado 依赖。**embedding 客户端（CloudEmbeddingClient）未实现**——embedding 走本地 `Core.Vector.EmbeddingService`（LlmTornado） |
| **StabilityHarness** | 独立 NuGet 库（`PackageId=StabilityHarness`，引用 Core，Host 编译时引用它）。提供 Lyapunov 能量监控、GrowthRatioGuard、CompositeStabilityMonitor（两者聚合）、RGDecimator 冗余裁剪、VSA 全息引擎。Host 启动时**自动注入** `CompositeStabilityMonitor` 到 `Agent.StabilityMonitor`（阈值从 `ModelContextProfile` 动态计算） |
| **Plugins** | 运行时 DLL：NovelTools（67 工具，含 23 个云端分析器）、SemanticRetrievalTool（8 工具）、WebSearchTools（6 工具）、FileSystem（6 工具；`BashTool` 已实现但未注册）、WritingTools（10 工具 — `diagnose_writing` + 9 个 `enhance_*`，增强工具需 Agent + harness 双非空）。另 Host 注册 Core 组 20 工具（tool_search + decompress + 13 个 AgentMemory + 4 个偏好工具 + tool_analyze_style）；`web_search` 同名注册表按名覆盖去重，总注册 116 |

> 云端仓库（Cloud.Api / 云端私有引擎 / Retrieval / 云端内部实现 / Web 前端）已迁移至独立仓库 **云端私有仓库**，本仓库仅保留本地客户端与云端客户端库。

### 1.1 .NET Tool 安装

```bash
# 安装
dotnet tool install --global Mythology

# 使用
mythology                          # 启动 TUI
mythology --novel abc123           # 指定小说
mythology --console                # Console 模式
mythology --debug                  # 调试模式
mythology build                    # 构建插件
mythology plugin list              # 列出已安装插件
mythology plugin install <name>    # 从云端安装插件（云端 API 未实现）
mythology plugin remove <name>     # 移除插件

# 云端地址（默认生产地址 https://api.mythology.work，TUI 内 /login 或 :cloud login 登录——本地回调 OAuth / 设备流）
export MYTHOLOGY_CLOUD_URL="https://cloud.mythology.example"

# 更新 / 卸载
dotnet tool update --global Mythology
dotnet tool uninstall --global Mythology
```

### 1.2 Core NuGet 包

Core 保持为一个 NuGet 包，第三方插件引用：

```xml
<PackageReference Include="Mythology.Core" Version="1.0.0" />
```

发布方式：

```bash
dotnet pack src/Core/Core.csproj -c Release
dotnet nuget push Mythology.Core.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
```

---

## 2. 三层存储架构

```
┌─────────────────────────────────────────────────────────┐
│                    存储架构总览                           │
├─────────────┬──────────────────┬────────────────────────┤
│   创作资产   │    运行时会话     │      语义索引          │
│  (项目目录)  │  (~/.mythology)  │    (vectors.db)       │
│             │                  │                        │
│ YAML/MD 文件 │  JSONL 文件      │   SQLite vec0         │
│ 人可读写     │  追加写入         │   纯计算产物           │
│ Git 版本控制 │  按项目/时间组织   │   可从文件重建         │
└─────────────┴──────────────────┴────────────────────────┘
```

### 2.1 创作资产 — 项目本地文件

一部小说 = 一个文件夹，**可以 Git 管理**：

```
novels/
└── 神话纪元/
    ├── novel.yaml                # 小说元数据
    ├── characters/
    │   ├── 李明.yaml
    │   └── 王芳.yaml
    ├── factions/
    │   └── 天机阁.yaml
    ├── locations/
    │   └── 长安城.yaml
    ├── items/
    │   └── 碧血剑.yaml
    ├── plotlines/
    │   ├── 主线-复仇.yaml
    │   └── 伏笔-身世之谜.yaml
    ├── relationships.yaml        # 角色关系网（集中）
    ├── scenes/                   # 场景定义
    │   ├── ch01.yaml
    │   └── ch02.yaml
    └── chapters/
        ├── ch01.md               # Markdown + YAML front matter
        └── ch02.md
```

**文件格式**：

| 实体 | 文件 | 格式 |
|------|------|------|
| Novel | `novel.yaml` | YAML |
| Character | `characters/{id}.yaml` | YAML |
| Faction | `factions/{id}.yaml` | YAML |
| Location | `locations/{id}.yaml` | YAML |
| Item | `items/{id}.yaml` | YAML |
| Plotline | `plotlines/{id}.yaml` | YAML |
| Relationship | `relationships.yaml` | YAML（集中） |
| SceneDefinition | `scenes/ch{N}.yaml` | YAML |
| Chapter | `chapters/ch{N}.md` | Markdown + YAML front matter |

**章节 Markdown 格式**：

```markdown
---
id: x1y2z3a4b5c6
order: 1
title: 序幕
status: draft
created-at: 2026-06-15T10:00:00Z
updated-at: 2026-07-01T12:00:00Z
---

长安城的黄昏，残阳如血...
（正文 Markdown，任何编辑器预览）
```

**原子写入**：使用 `AtomicFile` 工具类，先写 `.tmp`，再 `rename`，避免半写损坏。支持冲突检测（`FileConflictException`），TUI 人工编辑时通过 `SkipConflictCheck` 跳过。

### 2.2 运行时会话 — `~/.mythology/sessions/`

JSONL 格式（借鉴 pi agent 模式），每行一条事件，追加写入。由 `FileSessionStore` 管理。

**分片存储（v2，2026-07-29）**：每个会话从单文件 `{sessionId}.jsonl` 改造为**目录**，目录内含 `MANIFEST.json` 元数据和多个世代分片文件。每次 compact 操作创建新的分片，使单个文件大小有明确上界。

**目录命名**：`{yyyy-MM-ddTHH-mm-ss-fffZ}_{sessionId}/`，如 `2026-07-01T08-30-00-123Z_abc123def456/`。时间戳前缀来自会话创建时的 UTC 时间（自动迁移时来自旧文件的创建时间），使文件系统浏览时可按时间排序，清晰识别会话创建时间。

```
~/.mythology/
├── sessions/
│   └── --novels-神话纪元--/
│       ├── 2026-07-01T08-30-00-123Z_abc123def456/    ← 会话目录（带时间戳）
│       │   ├── MANIFEST.json       ← 元数据 + 分片索引
│       │   ├── 000000.jsonl        ← 第 0 代（原始消息，只读）
│       │   ├── 000001.jsonl        ← 第 1 代（compact 后，只读）
│       │   └── 000002.jsonl        ← 第 2 代（当前活跃，正在 append）
│       ├── 2026-06-28T14-15-30-456Z_fff9872aaa12/
│       │   └── MANIFEST.json
│       │   └── 000000.jsonl        ← 从未 compact 的唯一活跃分片
│       └── sessions.index.json     ← 可选：加速 GetSessions 的摘要索引
├── config.yaml                     # 全局配置
├── logs/                           # Serilog 日志
│   └── mythology-{date}.log
└── input-history/                  # 输入历史
    └── {session-id}.jsonl
```

**解析方式**：`EnsureSessionIndex` 扫描目录时，读取每个子目录下 `MANIFEST.json` 中的 `sessionId` 字段作为索引键（而非目录名）。因此目录名可以包含任意时间戳前缀，不影响会话查找。

**迁移兼容**：`MigrateOldFormatSync` 和 `001_SessionSegments` 均使用旧文件的创建时间作为时间戳前缀，确保迁移后目录名反映会话的实际创建时间。

**MANIFEST.json 结构**：

```jsonc
{
  "version": 1,
  "sessionId": "abc123def456",
  "novelId": "novel-xxx",
  "title": "第三章 风起云涌",
  "created-at": "2026-01-15T10:00:00.000Z",
  "updated-at": "2026-01-15T14:30:00.000Z",
  "segments": [
    { "seq": 0, "file": "000000.jsonl", "start-order": 0, "end-order": 499,
      "message-count": 240, "entry-count": 250, "file-size": 128000,
      "compact-entry-id": null, "has-compact": false, "created-at": "..." },
    { "seq": 1, "file": "000001.jsonl", "start-order": 500, "end-order": 999,
      "message-count": 245, "entry-count": 256, "file-size": 132000,
      "compact-entry-id": "a1b2c3d4e5f6", "has-compact": true, "created-at": "..." }
  ],
  "active-segment": 1,
  "compact-count": 1,
  "totalEntryCount": 506
}
```

**分片编号规则**：

| 编号 | 含义 | 可写 |
|------|------|------|
| `000000.jsonl` | 原始会话（从未 compact 过），或第 1 次 compact 后的旧分片 | ❌ 只读 |
| `000001.jsonl` | 第 1 次 compact 后生成的活跃分片 | ❌ 只读 |
| `000002.jsonl` | 当前活跃分片 | ✅ 写入中 |

每次 compact 创建下一个序号的 `.jsonl` 文件。每个分片文件内部是自包含的 JSONL，可以独立解析。`000000.jsonl` 第一行是 `type=message`（原始会话）；`000001+.jsonl` 第一行是 `type=compact` 条目（摘要），后续是保留消息。

**自动迁移**：旧格式的 `{sessionId}.jsonl` 文件在首次访问时自动迁移为分片目录格式（`{sessionId}/000000.jsonl + MANIFEST.json`），无需用户干预。

**JSONL 条目类型**（`JsonlEntryType` 枚举）：

| type | 说明 |
|------|------|
| `message` | 对话消息（user/assistant/tool/system） |
| `compact` | 上下文压缩摘要 |
| `event` | 事件条目（interrupt/error/model_change/variant_selected） |
| `leaf` | 活跃分支指针 |
| `label` | 用户书签/标记 |
| `branch_summary` | 被放弃分支的 AI 摘要 |

### 2.3 全局配置 — `~/.mythology/config.yaml`

```yaml
providers:
  default: openai
  default_model: gpt-4o-mini
  default_name: ""          # 自定义提供商名称
  thinking_level: medium
  api_keys:
    - provider: openai
      name: ""              # 自定义名称（provider=custom 时必填）
      api_key: ${OPENAI_API_KEY}
      base_url: ""          # 自定义 Base URL（provider=custom 时可选）
    - provider: anthropic
      api_key: ${ANTHROPIC_API_KEY}
    - provider: custom      # 自定义提供商
      name: my-provider
      api_key: sk-xxx
      base_url: https://api.example.com/v1

mcp:
  servers:
    - name: example-server
      command: npx
      args: [-y, @example/mcp-server]
      enabled: true

tools:
  - name: get_characters
    enabled: true
```

**优先级**：`~/.mythology/config.yaml` > `appsettings.json` > 环境变量

### 2.4 检索内核 — `vectors.db`（共享检索内核）

> 状态：**实现完成**。Vec 子系统已升级为「共享检索内核」（实现位于 `src/Core/Vector/`：`RetrievalKernel` + `IRetrievalStore`/`SqliteRetrievalStore` + `ChineseTextChunker` + FTS5 simple 分词 + RRF 融合）。逻辑上严格分为**用户长期记忆域**与**小说实体知识域**两个域；旧 `IVectorStore`/`VectorIndexer`/`VectorSearchEngine` 仍作为底层引擎保留，但上层调用已迁移到 `RetrievalKernel` + `IRetrievalStore`。

**唯一保留的 SQLite**（`vectors.db`），承载 dense + 词法 + metadata + 索引状态，全部为可重建派生数据：

```
vectors.db
├── retrieval_documents      # 检索文档身份 (domain / owner_id / novel-id / document_kind /
│                            #            source_entity_type/id / content_hash / index_status)
├── retrieval_chunks         # 块定位 (父章节 / offset / ordinal / chunk_hash)
├── dense 索引               # vec0 虚拟表（同一 chunk 的向量，维度固定 VectorDefaults.Dimension = 1024，不做启动探测）
├── retrieval_chunks_fts     # FTS5 词法索引（simple tokenizer，jieba 分词 + 拼音）
├── user_memory_*            # 用户长期记忆域（显式规则 / 已确认偏好 / 候选推断）
└── agent_memories           # Agent 记忆域：agent_memories / agent_journal / agent_snapshots
                             #（与检索域共享 vectors.db，见下方「Agent 记忆域」）
```

**双逻辑域 + Agent 记忆域**：
- `novel_knowledge` — 小说实体知识（实体摘要 + 章节 300 字语义块 + 场景/时间线，全可重建）
- `user_memory` — 用户长期记忆（显式规则 / 已确认推断 / 候选推断，**不可重建**，需可审查、可撤销）
- `agent_memories` — Agent 自身记忆（见下）

**混合检索主路径**（`RetrievalKernel`）：metadata/权限过滤先行 → dense `vec0` 与 `FTS5` 词法并行候选召回 → **RRF 融合** → 任务型 `RankingProfile` 重排 + 去重 + 多样性。

**Embedding 走本地 LlmTornado（显式 opt-in）**：`CoreServiceExtensions.RegisterRetrievalAsync` 按优先级选择实现——入口 `embeddingFactory`（测试覆盖）＞ `config.yaml` `Embedding` 段独立密钥的 `EmbeddingService`（OpenAI 兼容端点：bge-m3 / qwen-embedding / text-embedding-3-small 等）＞ 回退复用聊天 TornadoApi ＞ `DummyEmbeddingService`。向量维度固定 `VectorDefaults.Dimension = 1024`，不做启动探测（探测是启动链路上的远程调用，远端不可达会拖住入口）。未配置 embedding 模型名时不发任何远程 embedding 请求：`ToolSearchEngine` 检测到 `DummyEmbeddingService` 跳过 dense 构建（纯 BM25）；`RetrievalKernel.SearchAsync` 检测到全零向量时设 `DenseTopK=0` 降级纯词法。云端 `CloudEmbeddingClient`（/v1/embedding 消费方）**未实现**。

**实时索引 + 补偿同步**：

```
事件通道（实时）:
  FileNovelStore.OnEntityChanged → 后台索引任务队列 → 构建文档/块 → 写 metadata + vec0 + FTS5

补偿通道（启动/rebuild）:
  VectorSyncService 对账扫描 → 对齐事实源 updated-at/content_hash → 补齐缺失索引

全量重建:
  mythology index --rebuild
```

**FTS5 中文分词**：采用 [`simple`](https://github.com/wangfenjin/simple) tokenizer（jieba 分词 + 拼音搜索 + `simple_query`/`jieba_query`），扩展加载失败时降级为 `unicode61`（词法能力下降，但不影响 dense + metadata）。`sqlite-vec` 与 `simple` 作为独立 SQLite 扩展在同一个连接中共存。

**候选推断 / 函子记忆**：`UserMemoryRecord` + `SqliteUserMemoryStore` + `CandidateInferenceAnalyzer`（4 维度分析 + 确认工作流）。候选推断默认**不注入 prompt**，需用户确认后才升为已确认偏好。

**Agent 记忆域（agent_memories）**：`AgentMemoryStore`（`src/Core/Vector/AgentMemoryStore.cs`）管理 Agent 自身的工作记忆，三张表与检索域共享 `vectors.db`：

| 表 | 内容 |
|------|------|
| `agent_memories` | 单条记忆：文本 + `memory_kind` / `confidence` / `priority` / `expires_at` / `evidence_refs` + 向量 + BM25 索引 |
| `agent_journal` | 跨会话事件流水（决策/变更/教训），spaced repetition touch 驱动 |
| `agent_snapshots` | 状态快照（支持 `tool_snapshot` / `tool_as_of` / `tool_diff`） |

- **召回**：非 ANN 精确暴力评分（移植自 remembrane 设计），评分 = `w_sim·similarity + w_rec·recency + w_imp·importance + w_use·usefulness`，feedback（`mark_useful`/`mark_useless`）调整 `usefulness`。
- **注入**：`AgentMemoryTools` 共 13 个记忆工具（`tool_store` / `tool_recall` / `tool_reinforce` / `tool_forget` / `tool_mark_useful` / `tool_mark_useless` / `tool_find_conflicts` / `tool_resolve_conflict` / `tool_snapshot` / `tool_as_of` / `tool_diff` / `tool_pack` / `tool_consolidate`），另有 4 个偏好工具（`recall_preference` / `save_preference` / `list_preferences` / `delete_preference`）与 1 个候选推断工具（`tool_analyze_style`），共 18 个，全部 `DeferLoading=true`（经 `tool_search` 按需发现，不预注入上下文）；`AgentMemoryHook` 在 `tool_recall`/`tool_find_conflicts` 成功后对返回记忆自动 `MarkUsefulAsync`（`autoWeight=0.3`）。
- **共享 embedding**：`HostBootstrapper` 为 `AgentMemoryStore` 注入与 `SqliteRetrievalStore` **同源**的 embeddingService（避免各自默认 `HashEmbedder` 造成向量通道分叉）。


### 2.5 数据归属映射

| 数据 | 存储位置 | 格式 | 理由 |
|------|----------|------|------|
| Novel 元数据 | `novels/{name}/novel.yaml` | YAML | 项目入口，手工可编辑 |
| Character | `characters/{name}.yaml` | YAML | 核心创作资产，必须可预览/编辑 |
| Faction | `factions/{name}.yaml` | YAML | 同上 |
| Location | `locations/{name}.yaml` | YAML | 同上 |
| Item | `items/{name}.yaml` | YAML | 同上 |
| Plotline | `plotlines/{name}.yaml` | YAML | 同上 |
| Relationship | `relationships.yaml` | YAML | 网状关系，集中管理 |
| SceneDefinition | `scenes/ch{N}.yaml` | YAML | 场景编排，手工调整节奏 |
| Chapter 内容 | `chapters/ch{N}.md` | MD+front matter | **最大痛点** — Markdown 可预览 |
| Provider 配置 | `~/.mythology/config.yaml` | YAML | API Key 等配置，手工编辑 |
| Plugin 配置 | `~/.mythology/config.yaml` | YAML | 同上 |
| AgentSession | `~/.mythology/sessions/` | JSONL | 会话是追加写入，JSONL 天然适合 |
| ChatMessage | `~/.mythology/sessions/` | JSONL | 同上 |
| ChatInputHistory | `~/.mythology/input-history/` | JSONL | 同上 |
| EntityAuditLog | `~/.mythology/sessions/` | JSONL | entity_change 事件 |
| 向量索引 / 检索文档 / FTS5 | `vectors.db` | SQLite vec0 + FTS5 | 检索内核（dense + 词法 + metadata），可从文件重建；用户记忆域除外 |
| Agent 记忆（memories/journal/snapshots） | `vectors.db` | SQLite（`agent_memories` 等三表） | Agent 工作记忆，**不可重建**，需可审查、可撤销；与检索域共享 embedding 通道 |

---

## 3. 核心组件

### 3.1 Agent（状态机）

`Agent` 类是核心状态机，管理对话历史、事件流和 Steering/FollowUp 队列。

**状态枚举**：`Idle` / `Running`

**关键 API**：
| 方法/属性 | 说明 |
|------|------|
| `Prompt(text, ct)` | 启动新对话 |
| `Continue(ct)` | 继续循环 |
| `Abort()` | 中断当前运行 |
| `Reset()` | 重置所有状态 |
| `Steer(msg)` | 注入中断消息（入队 SteeringQueue） |
| `FollowUp(msg)` | 注入跟进消息 |
| `WaitForIdle(ct)` | 等待空闲 |
| `IsRunning` | 是否正在运行 |
| `CurrentNovelId` | 当前小说 ID |
| `AgentStateEnum` | Agent 状态枚举值 |
| `TornadoApi` | LlmTornado API 实例（可设置，用于运行时切换 Provider） |
| `SetHttpHandler(handler)` | 设置 CancellationHttpHandler（用于中断流式请求） |
| `SetSystemPrompt(text)` | 设置系统提示词 |
| `SetModelName(name)` | 设置模型名称 |
| `SetThinkingLevel(level)` | 设置思考级别 |
| `SessionId` | 会话 ID |
| `ToolInjector` | 按需工具注入器（Host 注入）。`CurrentNovelId` setter 在值变化时调用 `ClearExpanded()` + `ToolSearchTool.ResetSearchTrip()` |
| `AfterToolCall` | 工具调用后钩子（`LoadNovelHook` + `WritingFlowHook`，经 `CompositeToolHook` 组合） |
| `ShouldStopAfterTurn` | 回合结束审计回调（`AgentAuditHook`） |
| `SteeringQueue` | 引导队列（公开，TUI 可直接入队） |
| `OnEvent` | Agent 事件订阅 |

### 3.2 ConversationLoop（ReAct 循环引擎）

基于 LlmTornado 3.8.67 Conversation API 的 ReAct 循环实现。

**核心设计原则**：
1. Conversation 是一等公民 — 一个 session 对应一个 Conversation
2. Tool 执行走 FunctionCallHandler — 在回调中执行 AgentTool
3. Agent Loop = 多次 StreamResponseRich
4. 直接使用 ChatMessage，无需转换
5. **按需工具注入** — `BuildChatRequest()` 优先走 `_toolInjector?.BuildRequestTools()`（常驻 + 已展开 + 非延迟），null 时回退全量注入 `_toolRegistry.GetAll()`

**按需工具注入（Tool Search，2026-08）**：
- 每轮请求不再全量注入 116 个工具的 schema，而是由 `ToolInjector` 注入子集（常驻 `tool_search`/`ask_user`/`todo_write` + 已展开 LRU 20 + 非延迟工具；默认名单下约 84 个——核心写作/分析工具常驻是 2026-08-23 起的有意取舍）
- LLM 需要更多工具时调用 `tool_search`，命中后 `ToolInjector.ExpandAsync()` 展开完整定义注入后续请求
- 延迟插件名单（默认 `Semantic Retrieval Tool` / `Web Search Tools` + 所有第三方插件，按 `PluginManifest.Name` 匹配；`Agent:ToolSearchDeferredPlugins` 可覆盖）下工具不预注入
- 灰度开关 `Agent:ToolSearchMode`：`dynamic`（默认，按需注入）| `all`（全量注入，仅 DeferLoading=true 延迟）
- 搜索频率熔断：`tool_search` 每会话最多 8 次（`Agent:ToolSearchRateLimit` 可配），超限提示 LLM 收敛；会话切换（`CurrentNovelId` 变化）时重置
- Tool 热刷新：注入面签名变化时重建 Conversation，使新工具在本 run 内可用

**断路器**：
- 连续 3 次相同工具+参数（相同 TurnId）→ 触发
- 同一工具在最近 5 次调用中出现 ≥4 次（跨不同 TurnId）→ 触发
- **TurnId 去重**：同一 Assistant message 的并行 burst 共享同一 TurnId，高频检测按 (ToolName + TurnId) 去重计算，避免误伤
- 迭代无固定上限；循环由自然收尾、稳定性监控、断路器与 pre-flight 预算（compact + retry，上限 3 次）终止

**ToolUsageStats**：
- `ConversationLoop.UsageStats` 字段，线程安全
- 记录每个工具调用次数（`RecordCall`）、batch 工具占比（名称前缀 `batch_` 自动识别）、断路器触发次数（`RecordBreakerTrigger`）
- Agent 类公开 `UsageStats` 属性，TUI 可通过 `/stats` 查看

**beforeToolCall 阻止**：
- `IToolHook.BeforeToolCallAsync` 返回 `ToolCallBeforeResult`
- `.Block` → 跳过工具执行，返回错误消息给 LLM
- `.Allow` → 正常执行

**ShouldStopAfterTurn**：
- 回调函数，每个 turn 结束后调用
- 返回 `true` → 循环优雅终止（用于上下文压缩、预算限制）
- 当前实现：`AgentAuditHook` 检查草稿章节、孤立角色、未回收伏笔，通过 `SteeringQueue` 注入审计消息

**reasoning/thinking 支持**：
- ReasoningTokenHandler 收集推理内容
- 自动补回 ReasoningContent 到 ChatMessage（修复 LlmTornado bug）
- 支持持久化后 thinking 内容不丢失

**novel-id 自动注入**：
- 工具参数 novel-id 为空时自动填充 Agent.CurrentNovelId

**`[mN]` ref 标签注入（B3）**：
- 运行时向 user 消息 Content 注入 `[mN]` 标签（`InjectRefTags`），发送后还原，不污染 JSONL
- `EntryIdProvider` 委托提供稳定身份（`LlmChatMessage.NativeObject` 存 EntryId），使 ref 编号跨 compact/恢复稳定；无 provider 时退化按序编号

**上下文溢出处理**：
- 抛出 `ContextOverflowException`，TUI 捕获后触发 **auto-compact + retry**（`CompactForOverflowAsync`）；compact 后验证 token 估算是否降低，无效则不再重试（防死循环）
- **Pre-flight budget check**：每轮开始前，token 使用达到 `PreflightThresholdPercent`（8K→85%, 128K→96.5%, 1M→99.25%）时提前抛 overflow 触发 compact，避免在 `StreamOneTurnAsync` 中途耗尽预算
- **分级使用率提醒（nudge，P0）**：上下文使用率超过 `NudgeInfo/Warn/CriticalPercent`（从 pre-flight 阈值向下 −15%/−8%/−3%，保证先于拦截触发）时向 Conversation 注入提醒，发送后移除，不持久化
- **工具结果截断（P1）**：`PrepareToolResultForContextAsync` 对超长工具结果做头尾截断（阈值 `ScaledToolResultMaxChars` 随窗口动态化：8K→8K、128K→20K、1M→60K，头尾各 2/5），超大结果写内容寻址缓存文件（`~/.cache/mythology/tool-results/`）；内存/落库/重建三路径同源，保证 provider 前缀缓存字节一致

**HTTP 请求中断**：
- `CancellationHttpHandler` 追踪活跃的流式 HTTP 响应
- `Abort()` 时调用 `CancelActiveRequests()` 强制关闭所有活跃连接
- 通过 `EndpointBase.OnHttpClientRequested` 钩子注入自定义 HttpClient

**稳定性监控**（Host 默认启用，详见 §9）：
- `Agent.StabilityMonitor` 是可空 `IStabilityMonitor?`，`ConversationLoop.RunAsync` 接收并传到循环体
- 每个 turn 后若非 null 则调用 `RecordStep(incrementalTokens, thinkingTokens)`（**增量口径**，`ComputeTurnIncrement`：增量 = prompt 差 + completion，避免全量 prompt 每轮重复累计），`Tripped`/`Frozen` 时优雅终止
- Host 启动时通过 `StabilityHarness.CreateGuard()` 注入 `CompositeStabilityMonitor`（GRG + Lyapunov 聚合），同时注入 VSA HolographicEngine 与 RGDecimator MessageScorer；阈值全部从 `ModelContextProfile` 动态计算

### 3.3 事件系统

10 个生命周期事件 + 1 个断路器事件：

```
AgentStart → TurnStart → MessageStart → MessageUpdate* → MessageEnd
         → ToolExecutionStart → ToolExecutionUpdate* → ToolExecutionEnd
         → TurnEnd → ... → AgentEnd
```

### 3.4 消息类型

ConversationLoop 直接使用 LlmTornado 的 `ChatMessage`：
- `ChatMessageRole` — User/Assistant/System/Tool
- `ChatMessagePart` — Text/Reasoning/ImageUrl 等多态
- `ToolCalls` — 工具调用列表
- `ReasoningContent` — 推理/思考内容（reasoning/thinking 块）

### 3.5 CancellationToken 传递

```
Main → cts (Ctrl+C)
  └─ Host.RunAsync(ct)
      └─ Agent.Prompt(text, ct)
          └─ ConversationLoop.RunAsync(ct)
              ├─ Conversation.StreamResponseRich(ct)
              ├─ tool.ExecuteAsync(linkedCts.Token)
              └─ emit(event, linkedCts.Token)
```

| 取消源 | 触发 | 范围 |
|--------|------|------|
| 程序级 cts | Ctrl+C (SIGINT) | 关闭整个程序 |
| Agent internalCts | `agent.Abort()` | 只中断当前 prompt |
| CancellationHttpHandler | `Abort()` | 强制关闭活跃 HTTP 流式响应 |

**Ctrl+C 路由**：
- macOS/Unix 上，XenoAtom CBreak 模式默认 ISIG=0，Ctrl+C 作为 `\x03` 字节到达应用
- 首帧时调用 `stty isig` 启用 ISIG，让 Ctrl+C 产生 SIGINT → `Console.CancelKeyPress` → `Agent.Abort()`
- 全局命令注册作为备用路径（Raw 模式终端）

### 3.6 Prompt 系统

**9 段 Prompt 骨架**（`PromptAssembly`）自动注入到 system prompt，按固定顺序拼接：

| 序 | Key | 类 | 说明 |
|----|-----|------|------|
| 1 | `role` | `RoleDefinitionPrompt` | 角色定义：身份、核心能力、工作原则。锚定模型全局行为 |
| 2 | `conversation` | `ConversationStylePrompt` | 对话格式引导（Full ~500 字 / Compact ~100 字）+ 审阅评价规则（发现先行、按严重度、附位置与残余风险） |
| 3 | `tooling` | `ToolCallingPrompt` | 工具调用规范：并行规则 / 依赖规则 / 批量与写回规则 / 调用前后规则 / **ToolSearchRules** / **TodoRules** / 禁止规则 |
| 4 | `context` | `ContextUnderstandingPrompt` | 上下文理解（小说状态、实体引用）+ 创作反向思维（识别目标/不变量/改动边界，最小必要改动） |
| 5 | `planning` | `PlanningLoopPrompt` | 规划循环（多步任务的分解与收敛）；验证步骤逐项核验产出，工具返回成功 ≠ 内容达标 |
| 6 | `errors` | `ErrorHandlingPrompt` | 错误处理（工具失败、重试、降级） |
| 7 | `safety` | `SafetyPolicyPrompt` | 安全策略 |
| 8 | `writing` | `WritingProcessPrompt` | 写作流程规范（Full ~300 字 / Compact ~80 字）；根源优先（先定位根源再选增强工具）+ 阶段边界压缩（上下文 >70% 主动建议 /compact） |
| 9 | `humanizer` | `HumanizerPrompt` | AI 痕迹消除规则（预防层） |

此外，`EnhancementPrompt`（增强维度指南，修复层）和 `OutputZonesPrompt` 存在但未纳入骨架，由具体工具/场景动态注入。`PromptAssembly` 支持 `excludeKeys` 按需裁剪段。

在 `ConversationLoop.CreateConversation` 中自动拼接到 system prompt。

> **2026-08-12 提示词策略优化**：`ContextUnderstandingPrompt` 增加「创作反向思维」（识别真正目标/叙事不变量/改动边界，最小必要改动）；`ConversationStylePrompt` 增加「审阅与评价」；`PlanningLoopPrompt` 强化验证步骤（逐项核验 + 工具成功 ≠ 内容达标）与改方向先复述确认；`WritingProcessPrompt` 增加根源优先 + 阶段边界压缩；`ToolCallingPrompt` 强化调用前数据一致性自检（修改前先读文件最新内容）；`EnhancementPrompt` 默认推荐保守版最小改动。

---

## 4. 数据层

### 4.1 FileNovelStore — YAML/MD 文件读写

`FileNovelStore` 实现 `INovelStore` 接口，底层读写 YAML/MD 文件。**Session 管理已分离到 `FileSessionStore`**。

```csharp
public sealed class FileNovelStore : INovelStore
{
    private readonly string _novelsRoot;  // novels/ 目录
    private readonly string _mythologyDir; // ~/.mythology/

    // GetCharacter → 读 characters/{id}.yaml
    // SaveCharacter → 写 characters/{id}.yaml（AtomicFile 原子写入）
    // GetChapter → 读 chapters/ch{N}.md（解析 front matter + body）
    // SaveChapter → 写 chapters/ch{N}.md（序列化 front matter + body）
}
```

**关键特性**：
- **原子写入**：`AtomicFile` 工具类，先写 `.tmp`，再 `rename`
- **冲突检测**：`FileConflictException`，TUI 人工编辑时通过 `SkipConflictCheck` 跳过
- **ID → 文件名映射**：文件名 = `{id}.yaml`，YAML 内保留 `id` 字段
- **Markdown front matter**：章节元数据在 `---` 边界内，正文在边界外
- **EntityChangeEvent**：文件写入后触发，UI 层即时刷新 + Agent Steering

### 4.2 FileSessionStore — JSONL 会话管理

`FileSessionStore` 管理所有会话相关的读写操作，**从 `INovelStore` 中分离**，职责单一：

```csharp
public sealed class FileSessionStore
{
    private readonly string _novelsRoot;
    private readonly string _sessionsDir;  // ~/.mythology/sessions/
    private readonly string _inputHistoryDir; // ~/.mythology/input-history/

    // 解析缓存：Key = JSONL 文件路径，mtime 未变时复用
    // 容量限制：最多 128 个文件（分片格式下缓存项更多）
}
```

**关键特性**：
- **分片存储（v2）**：每个会话从单 JSONL 文件改为目录，内含 `MANIFEST.json` 元数据和多个 `NNNNNN.jsonl` 分片文件
- **`SessionSegmentManager`**：内部类，封装单个会话的分片生命周期、MANIFEST 读写和并发控制（`SemaphoreSlim` 互斥锁）
- **JSONL 条目类型**：`message` / `compact` / `event` / `leaf` / `label` / `branch_summary`
- **解析缓存**：JSONL 文件 mtime 未变时复用解析结果，避免重复全量读取；分片格式下每个文件独立缓存
- **分支树**：支持 `leaf`（活跃分支指针）、`branch_summary`（被放弃分支摘要）、`label`（用户书签）
- **上下文压缩**：`compact` 条目生成新分片，旧分片冻结为只读
- **自动迁移**：旧格式 `{sessionId}.jsonl` 文件在首次写入时自动迁移为分片目录格式
- **并发模型**：`SaveSessionMessages` 等写入方法在 `SessionSegmentManager` 内部锁 + `AtomicFile` 文件级锁双重保护下执行，`CompactSession` 额外获取 `compact.lock` 文件锁防止重入

### 4.3 信任与数据访问 — 三级 Trust + 权限驱动 DI

Core 在 `Core.Plugins` 命名空间定义「三级信任模型 + 能力映射权限」机制。权限数据源是 DLL 上的 `[assembly: PluginPermission(...)]`（受强名称签名保护），`plugin.json` 只作为构建产物，运行时不读取。

**`PluginTrustLevel` 三级**：

| 级别 | 判定方式 | ServiceProvider | 可访问服务 |
|:---:|---|---|---|
| `Builtin` | 位于 `AppContext.BaseDirectory/plugins/` | 原始 `IServiceProvider` | 全部（`INovelStore`/`IPluginStore`/`ConfigStore`/...） |
| `Verified` | DLL 公钥 token 在官方白名单 | `RestrictedPluginServiceProvider` | 按声明的 `[PluginPermission]` 授权 |
| `External` | 无签名或签名不在白名单 | `RestrictedPluginServiceProvider` | 声明权限与 `ExternalAllowed` 的交集 |

**`PluginPermissions` 权限常量**（唯一真实来源）→ 通过 `PluginCapabilities` 映射到服务类型：

| 能力键 | 允许的服务类型 |
|---|---|
| `novel:read` | `IPluginStore` |
| `novel:write` | `INovelStore`, `IPluginStore` |
| `vector:read` | `IVectorStore`, `IEmbeddingService`, `RetrievalKernel` |
| `vector:write` | `IVectorStore`, `IEmbeddingService`, `RetrievalKernel` |
| `config:read` / `config:write` | `IConfiguration` / `ConfigStore` + `IConfiguration` |
| `fs:access` / `net:access` / `process:execute` / `ui:interact` | 对应能力高亮标记（由具体服务提供） |

`External` 级别的声明权限会被自动裁剪到 `ExternalAllowed`（`novel:read` / `config:read` / `net:access` / `ui:interact`）——即使 DLL 声明了 `novel:write`，`External` 插件也拿不到 `INovelStore`。

**RestrictedPluginServiceProvider**（实际的权限拦截器）：

```csharp
sealed class RestrictedPluginServiceProvider(IServiceProvider inner, HashSet<Type> allowedServices)
    : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        allowedServices.Contains(serviceType) ? inner.GetService(serviceType) : null;
}
```

`PluginLoader` 加载每个插件时：
1. 反射读取 DLL 的 `[assembly: PluginPermission(...)]`，得到声明权限集合
2. 按信任级别决定最终生效权限（`Builtin`/`Verified` 全保留，`External` 取交集）
3. 以 `PluginCapabilities` 映射「权限 → 服务类型」集合，构造 `RestrictedPluginServiceProvider`
4. 把它传给 `IToolPlugin.Initialize(sp)`

**双数据接口**（仍是代码契约）：

| 接口 | 实现 | 访问级别 | 适用对象 |
|------|---|---------|---|
| `INovelStore` | `FileNovelStore`（唯一实现） | 完全 CRUD | 声明了 `novel:write` 的 `Builtin`/`Verified` 插件 |
| `IPluginStore` | `PluginStore`（sealed，委托 `INovelStore`） | 只读 + `NovelSummary` DTO | 声明了 `novel:read` 的任意插件 |

`IPluginStore` 是独立接口（非 `INovelStore` 子集），只有读方法，返回精简的 `NovelSummary`（仅 Id/Title/Genre）。`External` 插件编译时就不可能调用写入方法；即便声明了 `novel:write`，运行时也会被 `ExternalAllowed` 裁剪。

**DI 注册链**：

```
HostBootstrapper:
  1. services.AddSingleton<FileNovelStore>()
  2. services.AddSingleton<INovelStore>(sp => sp.GetRequiredService<FileNovelStore>())
  3. services.AddSingleton<PluginStore>(sp => new PluginStore(
       sp.GetRequiredService<INovelStore>(),
       sp.GetService<ILogger<PluginStore>>()))
  4. services.AddSingleton<IPluginStore>(sp => sp.GetRequiredService<PluginStore>())
```

**`IPluginStore` 只读接口**（实际方法签名，不带 `novelId` 参数的方法仅 `GetNovelSummary`/`GetAllNovelSummaries`）：

```csharp
public interface IPluginStore
{
    Task<NovelSummary?> GetNovelSummary(string id);
    Task<List<NovelSummary>> GetAllNovelSummaries();
    Task<Chapter?> GetChapter(string novelId, string id);
    Task<List<Chapter>> GetChapters(string novelId);
    Task<Character?> GetCharacter(string novelId, string id);
    Task<List<Character>> GetCharacters(string novelId);
    Task<List<Plotline>> GetPlotlines(string novelId);
    Task<List<Faction>> GetFactions(string novelId);
    Task<List<FactionRelationship>> GetFactionRelationships(string novelId);
    Task<Location?> GetLocation(string novelId, string id);
    Task<List<Location>> GetLocations(string novelId);
    Task<Item?> GetItem(string novelId, string id);
    Task<List<Item>> GetItems(string novelId);
    Task<List<TimelineEvent>> GetTimelineEvents(string novelId);
    Task<List<Relationship>> GetRelationshipsByNovel(string novelId);
    Task<List<SceneDefinition>> GetScenesByNovel(string novelId);
}
```

> 注：`IPluginStore` 没有 `GetChapterCount`/`GetCharacterCount`/`GetSceneCount`/`GetTotalWordCount` 等计数方法（计数逻辑在 `INovelStore` 侧）。

### 4.4 ConfigStore

```csharp
public sealed class ConfigStore
{
    private readonly string _configPath; // ~/.mythology/config.yaml

    public ConfigData Load();
    public Task SaveAsync(ConfigData config);
    // ConfigData 包含: Providers, Mcp, Tools 配置
}
```

### 4.5 VectorSyncService + RetrievalKernel

**VectorSyncService** — 启动时与 `rebuild` 时执行补偿对账，对比文件的 `updated-at`/内容哈希与检索内核中的索引状态，补齐缺失索引。

**RetrievalKernel** — 共享检索内核入口（§2.4），顶层工具调用经由它，底层 dense 引擎 (`IVectorStore`/`VectorIndexer`/`VectorSearchEngine`) 仍可用，但不再是 RAG 工具的直接接口。

### 4.6 EntityChangeEvent

`FileNovelStore` 写操作触发 `EntityChangeEvent`，事件携带：
- `EntityType` — 实体类型（Chapter/Character/Plotline/...）
- `EntityId` — 实体 ID
- `EntityName` — 实体名称
- `Action` — AuditAction 枚举（Create/Update/Delete）
- `FieldChanges` — 变更字段字典
- `AuditSource` — 来源枚举（Ai/User/System）
- `ToolName` — 触发工具名
- `SessionId` — 会话 ID

响应链：
- UI 层监听 → Debounce 200ms 刷新设定面板 + 系统气泡 + 高亮闪烁
- Agent 层监听 → 人工编辑时通过 `SteeringQueue` 注入上下文通知
- SessionWriter 监听 → 写入 JSONL `event` 条目（entity_change 子类型）
- 审计日志写入 → `WriteAuditLogs` 方法记录

### 4.7 TodoStore + UserQuestionStore

**TodoStore** — 会话级 Todo 存储
- 工具：todo_create / todo_update / todo_delete / todo_list / todo_clear
- 支持 BlockedBy 依赖关系

**UserQuestionStore** — 用户问题存储
- 支持工具等待用户回复
- ask_user 创建问题 → TUI 渲染 `AskUserDialog` → 用户回复 → 工具继续

---

## 5. 插件系统

### 5.1 加载机制

- `PluginLoader` 扫描多个目录层级，按优先级从高到低加载
- 同名插件（按 `IToolPlugin.Name` 去重）高优先级覆盖低优先级
- 每个插件使用独立的 `AssemblyLoadContext`（隔离加载）
- `AssemblyDependencyResolver` 解析依赖
- 可选强名称签名验证

**插件目录优先级**：

| 优先级 | 目录 | 默认信任级别 | 说明 |
|:---:|------|:---:|------|
| 1 | `NOVEL_PLUGINS_DIR` 环境变量 | External | 测试/调试 |
| 2 | `.mythology/plugins`（当前目录） | External | 项目级 |
| 3 | `~/.mythology/plugins`（全局） | External | 用户安装 |
| 4 | `AppContext.BaseDirectory/plugins` | Builtin | 随 Host 发布 |

> 同一目录下的 DLL 若公钥 token 在官方白名单中，可升级为 `Verified`（详见 §4.3）。

**内置插件构建**：Host.csproj 通过 MSBuild `BuildPlugins` Target（`BeforeTargets=BeforeBuild`）在编译 Host 前先构建 5 个插件项目；插件 csproj 的 `OutputPath` 指向 Host 的 `bin/plugins/`，DLL 自动落到正确位置。另有一个 `CopyJiebaResources` Target（AfterBuild）将 jieba.NET 词典复制到 Host 输出目录（ToolSearchEngine 运行需要）。

### 5.2 信任级别与数据隔离

| 信任级别 | ServiceProvider | 可用服务 | 说明 |
|:---:|---|---|---|
| `Builtin` | 原始 `IServiceProvider` | 全部（`INovelStore`/`IPluginStore`/`ConfigStore`/...） | 内置插件 |
| `Verified` | `RestrictedPluginServiceProvider`（按声明权限） | 声明的 `[PluginPermission]` 授权的服务 | DLL 签名在白名单 |
| `External` | `RestrictedPluginServiceProvider`（权限裁剪） | 声明权限 ∩ `ExternalAllowed` | 第三方插件 |

### 5.3 已有插件

| 插件 | 工具数 | 默认信任 | 存储接口 | 说明 |
|------|--------|:---:|---|---|
| NovelTools | 67 | Builtin | `INovelStore` + `IFictionHarnessClient` + `RetrievalRestClient` | 小说创作核心工具：项目管理、章节/大纲、实体 CRUD + 批量、**23 个云端分析器**（`CloudAnalysisTool` 派生）、Explore（需 Agent 非空）、导出、统计、`web_scrape`/`web_search`（走云端检索 REST） |
| WritingTools | 10 | Builtin | `INovelStore` + `IFictionHarnessClient` | `diagnose_writing` + 9 个 `enhance_*`（immersion/satisfaction/pacing/description/dialogue/foreshadowing/humanizer/prose_quality/chapter_hook）。CHANGE_07 后诊断走云端 Harness，增强的定位/生成/写回走本地 |
| SemanticRetrievalTool | 8 | Builtin | `PluginStore` + `RetrievalKernel` | 语义检索：search_memory / batch_search_memory / recall_character / recall_plot / find_consistency / recall_scene / recall_timeline / recall_related，走 `RetrievalKernel` |
| WebSearchTools | 6 | Builtin | `INovelStore` | 网络搜索（含 batch_web_search） |
| FileSystem | 6 | Builtin | `TodoStore` + `UserQuestionStore` | 文件操作 + Todo 管理 + 用户交互（`read`/`write`/`edit`/`todo_write`/`ask_user`/`get_pending_questions`）。`BashTool` 类已实现但 `GetTools()` 未注册 |
| Core（Host 注册） | 20 | — | `ToolRegistry` | `tool_search`（按需检索）+ `decompress`（恢复压缩原文）+ 13 个 AgentMemory 工具 + 4 个偏好工具 + `tool_analyze_style`（后 18 个全部 DeferLoading）。注：NovelTools 与 WebSearchTools 的 `web_search` 同名，注册表按名覆盖去重后总注册 116 |

> **云端分析 Harness 接线（CHANGE_07 已接通）**：`IFictionHarnessClient` 接口位于 `Core.Cloud`（与 `PluginCapabilities` 同程序集，可被权限系统映射），`HttpFictionHarnessClient` 实现位于 ClientPlatform。Host DI 已注册（`MYTHOLOGY_CLOUD_URL` + 动态 API Key），插件声明 `cloud:harness` 权限（`PluginCapabilities` 映射放行 `IFictionHarnessClient`）。23 个 NovelTools 分析器走云端，WritingTools 10 工具（1 诊断 + 9 增强，增强工具需 Agent + harness 双非空）注册。⚠️ 遗留：`diagnose_writing` 深度模式发送 `"deep"`，但 backend `AgentModule.ValidateInvocation` 仅允许 `quick|standard` → 深度诊断会被 400 拒绝（backend 引擎层已支持 deep/ultra，需 API 层放行）。

### 5.4 工具分类与元数据

`AgentTool` 携带分类和实体类型元数据，驱动 TUI 渲染逻辑：

```csharp
public enum ToolCategory
{
    Generic,        // 通用工具
    FileEdit,       // 文件编辑（→ Diff 渲染）
    EntityCrud,     // 实体增删改查（→ 侧面板高亮）
    ChapterManagement, // 章节管理
    EntityRead,     // 实体读取
    SearchTool,     // 搜索工具
    BashTool,       // Bash 命令
}
```

### 5.5 工具结果详情接口

| 接口 | 说明 | 实现者 |
|------|------|--------|
| `IDiffDetails` | 包含 `EditHunk[]` + `Path`，TUI 渲染 Diff | WriteFileDetails / EditFileDetails / ChapterDetails / EditTextDetails |
| `INovelDetails` | 包含 `Id`，Host `LoadNovelHook` 自动切换当前小说 | ManageNovelDetails |
| `ExploreDetails` | 包含 `VariantResult[]`，TUI 弹出变体选择对话框 | ExploreTool |

---

## 6. LLM 集成

### 6.1 ConversationLoop 集成

基于 LlmTornado 3.8.67 Conversation API，支持 **10 家预设 Provider + 自定义**的自动路由：
- OpenAI / Claude(Anthropic) / Google / Groq / xAI / DeepSeek / Mistral / OpenRouter / Kimi(Moonshot) / 阿里百炼(Alibaba)
- 支持自定义 Provider（`LlmProvider = null` + 填写 BaseUrl + `LLmProviders.Custom`）
- 使用 `new TornadoApi([new ProviderAuthentication(...)])` 多 provider 构造函数
- 模型名支持字符串（如 `"gpt-4o-mini"`），LlmTornado 自动解析 provider

**Provider 定义**：`ProviderDefinition` 类统一管理所有提供商的配置键、DisplayName 与 LlmTornado `LLmProviders` 枚举映射（自定义 Provider `LlmProvider = null`）。

**StreamResponseRich 回调**：
- `ReasoningTokenHandler` — 推理/思考内容增量
- `MessageTokenHandler` — 文本增量
- `FunctionCallHandler` — 工具调用（支持并行执行）
- `OnUsageReceived` — Token 用量统计（InputTokens + OutputTokens），非空时上报 StabilityMonitor

### 6.2 Function Calling

`ConversationLoop.CreateConversation` 收集 `ToolInjector.BuildRequestTools()` 返回的工具集（按需注入后的子集），将其 JSON Schema 传给 LlmTornado 的 `ChatRequest.Tools`。`AgentTool.Aliases`/`ExampleQueries`/`DeferLoading` 虚属性参与 ToolSearch 索引构建与延迟注入。

**JSON 修复**：`JsonRepairUtil` 处理 LLM 返回的畸形 JSON（单引号、缺少逗号、注释等），修复后反序列化。

### 6.3 ThinkingLevel / VendorExtensions

ThinkingLevel 通过双轨映射实现跨 Provider 的推理控制：
- **Anthropic**: `ChatRequestVendorAnthropicExtensions.Thinking` (BudgetTokens + Enabled)
- **OpenAI**: `request.ReasoningEffort = ChatReasoningEfforts.{Low|Medium|High}`
- 两条路径同时设置，LlmTornado 按模型自动选择生效的扩展
- 预算映射：minimal=128, low=512, medium=1024, high=4096, xhigh=8192

### 6.4 多 Provider 密钥配置

`~/.mythology/config.yaml` 为主配置源，`appsettings.json` 兼容。所有密钥支持 `${ENV_VAR}` 语法，由 `ResolveKey` 辅助方法解析。

**运行时切换 Provider**：TUI 通过 `SettingsDialog` 连接器标签页修改配置，调用 `HostBootstrapper.CreateProviderApi` 重建 `TornadoApi` 和 `CancellationHttpHandler`。

### 6.5 错误处理

`LlmErrorHelper` 提取 LlmTornado 异常中的可读错误消息，用于 TUI Toast 提示。

---

## 7. 云端协作

> 云端能力位于独立私有仓库（不在本仓库开源范围）；本地客户端通过 `ClientPlatform` 的 HTTP 客户端访问，
> 云端不可达时自动降级，本地创作不中断。云端计费、额度与私有参数不在本仓库维护。

### 7.1 云端端点与客户端

| 能力 | 端点 | 客户端（ClientPlatform） | 用途 |
|------|------|--------------------------|------|
| 统一传输层 | — | `CloudRestTransport`（internal，数据面客户端共用） | baseUrl 归一 · `X-API-Key` / `Idempotency-Key` · 默认 Resilience 客户端 · 按请求 60s 安全网 · 401/403 吊销总线 · 失败日志（原始响应体进日志）· `CloudErrorMapper` 异常映射（服务端 `code`/`detail` 为权威）。端点路径与 wire 形状留在各客户端，可被 StubHandler 直测 |
| Embedding | `MYTHOLOGY_CLOUD_URL/v1/embedding` | `CloudEmbeddingClient`（显式 opt-in：`config.yaml` `Embedding.Provider: cloud`） | 默认仍是**本地** `Core.Vector.EmbeddingService`（LlmTornado，`config.yaml` `Embedding` 段：bge-m3 / qwen-embedding 等；未配置时降级纯词法）；`provider: cloud` 时 dense 向量走云端（维度须与 `VectorDefaults.Dimension`=1024 一致，`available=false` 显式失败而非静默零向量） |
| 检索 REST | `MYTHOLOGY_CLOUD_URL/v1/retrieval` | `RetrievalRestClient` | REST（`scrape`/`search`/`map`/`discover`），调用按云端额度结算，额度不足抛 `UsageLimitExceeded`。`web_scrape`/`web_search`/`web_map`/`web_discover` 工具使用 |
| 爬取任务 | `MYTHOLOGY_CLOUD_URL/v1/retrieval/crawl` | `CrawlRestClient` | 异步任务三段式：提交 202（`Idempotency-Key` 必填）→ 快照轮询 GET → 取消 DELETE，按成功页结算。`web_crawl`/`web_crawl_status`/`web_crawl_cancel` 工具使用 |
| 素材知识库 | `MYTHOLOGY_CLOUD_URL/v1/knowledge` | `KnowledgeRestClient` | `categories`/`documents`/`documents/{id}`/`search`（混合检索 BM25 jieba + 向量余弦 RRF；`search` 单独计费，其余免费；库未配置回 503 `knowledge_unavailable`）。`knowledge_*` 4 工具使用 |
| 云端用量 | `MYTHOLOGY_CLOUD_URL/v1/agent/usage` | `UsageRestClient` | 月度配额快照 + 逐笔账本（`/v1/agent/usage/ledger`，游标翻页、按能力过滤）；零价只读，`period` 缺省当月、非 `yyyy-MM` 回 400。TUI `:cloud status` 与 Desktop 状态栏使用 |
| 小说同步 | `MYTHOLOGY_CLOUD_URL/v1/sync` | `CloudNovelSyncClient`（+ `CloudNovelSyncService` 编排） | cloud-novel-sync v3：列目录 / `documents:fetch` / `:push` / `:delete`，500 条或 6MB 分批，冲突以 200 + 结构化 `Current` 返回（非异常路径）；`ICloudNovelSyncClient` 接口供服务层测试替身 |
| 分析 Harness | `MYTHOLOGY_CLOUD_URL/v1/agent/runs` | `HttpFictionHarnessClient`（SSE 优先 + 轮询回退） | 23 个 NovelTools 云端分析器 + WritingTools 诊断/增强的诊断阶段；`CloudDiagnosis` 调度链路（DiagnosisScheduler → SignalBuilder → InjectQueue）。`harness-invocations` 协议 |
| 本地回调 OAuth | `GET/POST /v1/oauth/authorize` + `POST /v1/oauth/code` + `POST /v1/oauth/token` | `OAuthLoopbackClient` | Authorization Code + PKCE + loopback 回跳（CHANGE_09 形态 B，默认登录方式） |
| 设备授权 | `POST /v1/device/authorize` + `POST /v1/device/token/stream` | `DeviceAuthorizationClient` | RFC 8628 设备授权登录（QR 码 + 用户码 + SSE 轮询，回退 / SSH 环境） |

**端点配置**：`MYTHOLOGY_CLOUD_URL` 环境变量（默认生产地址 `https://api.mythology.work`，收口于 `ClientPlatform.CloudEndpoints`——全仓唯一默认值来源）。API Key 每次调用从 `~/.mythology/cloud-session.json`（Windows DPAPI 加密）动态读取，登录/登出后自动刷新；真实调用 401/403 经 `CloudAuthInvalidationBus` 事件驱动立即吊销。

**时序收口**：全部云端时序为共享常量 `ClientPlatform.CloudTimings`（9 项，Desktop / TUI / 共享客户端单点共用，与地址收口同模式）——调优任一时序只需改该文件一处，两端自动生效。

| 常量 | 值 | 语义 | 引用点 |
|------|-----|------|--------|
| `ProbeTimeout` | 10s | 探活/校验 HTTP 超时（§13.7 登录后探活、心跳 Probe、手动 Key 远程校验） | `CloudAuthService` 探活 HttpClient · `CloudConnectionService.ProbeCoreAsync` |
| `HeartbeatInterval` | 5min | 心跳间隔（事件驱动吊销之外的状态刷新兜底） | `CloudAuthService.HeartbeatInterval` · `TuiApp.HeartbeatLoopAsync` |
| `SessionLifetime` | 30d | 滑动续期默认有效期（服务端 expiresAt 缺失时；仅 UX 提示，Probe 裁决有效性） | Desktop/TUI 续期字段 · `CloudLoginDialog.CreateServerSession` |
| `RenewalSlack` | 1d | 续期触发 slack（过期或临近才续期落盘，避免心跳写盘） | Desktop/TUI 续期字段 |
| `ToastCooldown` | 60s | Toast 节流冷却（同一降级原因冷却窗口内只提示一次） | `CloudToastThrottle` |
| `LoginHttpTimeout` | 无限 | 登录协议 HTTP 超时（OAuth/设备流 token 交换；用户取消控制生命周期） | `CloudAuthService.CreateLoginHttpClient` · `CloudLoginDialog` 登录流客户端 ×2 |
| `OAuthCallbackTimeout` | 120s | OAuth 本地回调等待窗口（超时回退设备流） | `OAuthLoopbackClient` |
| `CloudApiTimeout` | 60s | 非流式云端 API 单请求超时（按请求链接 CTS，覆盖到响应体读完） | `CloudRestTransport` 对非流式数据面请求统一施加；流式 SSE 不走它（传 `requestTimeout: false`） |
| `StreamingClientTimeout` | 无限 | 流式 SSE 客户端超时（SSE 长连接不能被 HttpClient.Timeout 掐断，也不施加 `CloudApiTimeout` 安全网；由调用方截止 CTS 兜底：Harness `WaitForResultAsync` 2 分钟等待截止 · 设备流服务端 `ExpiresInSeconds` 窗口） | `CloudRestTransport(streamingClient: true)` · `DeviceAuthorizationClient` |

> 与 `CloudTimings.cs` 类级 XML 文档总表互为镜像（编排层/协议层两段式说明见该文件注释）。

### 7.2 凭据与登录

- `CloudSession`：ApiKey / PublicId / WorkspaceId / Plan / SavedAt / ExpiresAt，存于 `~/.mythology/cloud-session.json`（权限 600）。D5 统一后唯一存储实现为 `ClientPlatform.CloudSessionStore`，TUI 经 `CloudConnectionService`（连接状态门面）访问，设备流/OAuth 协议在 ClientPlatform
- TUI 启动时 `InitializeCloudAsync`：加载凭据 → 更新状态栏 → `ProbeCloudAsync` + `StartCloudHeartbeat`（每 5 分钟 `GET /v1/capabilities`）
- 无凭据但配置了 `MYTHOLOGY_CLOUD_URL` → Toast 提示 `:cloud login`
- 登录流程三态：本地回调 OAuth（默认）→ 设备流（回退 / SSH / `--device`）→ 手动粘贴 `mk_live_` Key 兜底
- 命令：`:cloud login` / `:cloud status` / `:cloud logout`（`/cloud` 为 login 快捷方式；`/login [--device] [<url>]`）
- 连接状态（`CloudConnectionState`）：Connected（`☁️`）/ Authenticating（`🔒`）/ RateLimited（`⚠️ RL`）/ QuotaExceeded（`⚠️ Quota`）/ Unavailable（`❌`）/ Disabled（`☁️ off`）

### 7.3 降级链

| 云端能力不可达 | 降级行为 |
|---------------|---------|
| embedding 未配置（本地） | `DummyEmbeddingService` → `RetrievalKernel` 纯词法（DenseTopK=0）、`ToolSearchEngine` 纯 BM25 |
| `/v1/retrieval` | `web_scrape`/`web_search` 返回不可用提示 |
| `/v1/agent/runs` | 云端分析器返回"云端不可用"（不伪造分数）；WritingTools 9 个 enhance_* 因 harness 为 null 不注册 |
| 整体云端 | 本地创作、编辑、检索（词法）、续写、写回、快照全部可用 |

### 7.4 Host 集成现状

- ✅ embedding 已接线（`CoreServiceExtensions.RegisterRetrievalAsync`）：本地 LlmTornado `EmbeddingService`（config.yaml `Embedding` 段）或 `DummyEmbeddingService` 降级；维度固定 1024 不探测。云端 `CloudEmbeddingClient` 未实现
- ✅ `RetrievalRestClient` 已注入（`RegisterStores`）
- ✅ `IFictionHarnessClient` 已注入（`RegisterStores`，`HttpFictionHarnessClient` + 动态 API Key provider），插件经 `cloud:harness` 权限获取
- ✅ 权限链：`PluginPermissions.CloudHarness`（`cloud:harness`）→ `PluginCapabilities` 映射 `IFictionHarnessClient` → NovelTools/WritingTools 声明该权限（csproj `PluginPermissions` 元数据同步）

---

## 8. TUI 布局概览

> 详细的 TUI 布局规范、状态原型、控件映射和交互行为见 [UI.md](./UI.md)。

```
┌─────────────────────────────────────────────────────────────────────┐
│  Header: 📖 Mythology │ ✨ AI 小说写作助手                            │
├──────────────────────────────────────────┬──────────────────────────┤
│                                          │ ┌──┬──┬──┬──┬──┬──┬──┬──┐│
│                                          │ │角│组│地│情│场│道│关│大││
│  DocumentFlow (无边框, 7/10 宽)          │ ├──┴──┴──┴──┴──┴──┴──┴──┤│
│  - 用户/助手/工具/思考/中断/错误气泡       │ │  设定面板 (3/10 宽)   │ │
│  - 系统气泡（实体变更通知）              │ └────────────────────┘ │
│  - compact 进度（🔄 → ✅）               │ Todo 面板               │
├──────────────────────────────────────────┴──────────────────────────┤
│  ❯ 聊天 | /命令 | :cloud | #角色 | @文件【ESC/Ctrl+C 中断回复】      │
├─────────────────────────────────────────────────────────────────────┤
│  🔄 诗词 │ 📖 小说名 │ ☁️Cloud │ ⚡增强模式 │ 📊 tok │ model │ Ctrl+Q│
└─────────────────────────────────────────────────────────────────────┘
```

右侧 **8 个 Tab**：📋 大纲 / 🎭 角色 / 🏰 组织 / 📍 地点 / 📜 情节 / 🎬 场景 / 🎒 道具 / 🔗 关系；下方为 Todo 面板。

状态栏（从左到右）：spinner + 状态文本（💤 idle / 诗词 / 🔄 正在压缩上下文）｜ 📖 小说名 ｜ Cloud 状态（`☁️` Connected / `🔒` Authenticating / `⚠️ RL` RateLimited / `⚠️ Quota` QuotaExceeded / `❌` Unavailable / `☁️ off` Disabled）｜ ⚡全自动/⚡半自动/✋手动 增强模式 ｜ 📊 Token 统计（>90% 黄、>100% 红）｜ 模型名 + 工具数 + Ctrl+Q

上下文压缩反馈：`OnCompactStart` 在对话流插入 Dim 色 `🔄 正在压缩上下文...`，`OnCompactEnd` 原位替换为 `✅ 上下文压缩完成：N → M 条`；Token 口径经 `ContextTokenCalibrator` 校准（最小二乘拟合估算→API 实测）。

### 8.1 Desert 主题

| 色阶 | Hex | 用途 |
|------|-----|------|
| desert0 | `#1C1917` | 深暖黑（主背景） |
| desert1 | `#2C2520` | 深土色（侧面板/输入栏/Tab条背景） |
| desert2 | `#3C3836` | 暖炭色（弹出/对话框背景） |
| desert3 | `#504945` | 暗石色（dim 文本、边框） |
| desert4 | `#EBDBB2` | 暖沙色（主文本） |
| desert5 | `#FBF1C7` | 浅沙色（高亮文本） |
| copper | `#D68A48` | 暖铜色（主强调色） |
| amber | `#FABD2F` | 金琥珀（标题/警告） |
| terracotta | `#FB4934` | 赤陶色（错误/删除） |
| sage | `#8EC07C` | 鼠尾草绿（成功/插入） |
| rose | `#D3869B` | 沙漠玫瑰（Important alert） |

---

## 9. 稳定性监控系统 (StabilityHarness)

### 9.1 核心接口

```csharp
public enum StabilityStatus { Stable, Warning, Unstable, Tripped, Frozen }

public interface IStabilityMonitor : IDisposable
{
    StabilityStatus RecordStep(int tokensUsed, int errors = 0);
    StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors = 0);
    StabilityStatus CheckStability();
    void Reset();
    void Unfreeze(long currentTokens = -1);
    void UpdateTokenBudget(long budgetCeiling);
    void ApplyProfile(ModelContextProfile profile);
    void SeedTokenCount(long tokens);
    long GetTokenBudget();
    long TotalTokens { get; }
    long TotalErrors { get; }
    int TotalSteps { get; }
    bool IsTripped { get; }
    bool IsFrozen { get; }
    bool IsStable { get; }
    IReadOnlyList<double> EnergyHistory { get; }
}

public interface IGrowthRatioGuard : IStabilityMonitor
{
    double? Baseline { get; }
    double? CurrentRatio { get; }
    int ConsecutiveEscalating { get; }
    void ResetEscalation();
    void ResetBaseline();
}
```

### 9.2 ModelContextProfile — 动态阈值的第一公民

所有稳定器阈值通过 `ModelContextProfile` 比例化，不再硬编码绝对值。上下文窗口从 8K 到 1M 的 128× 差异通过比例值自然消化。在模型切换时构建并注入所有稳定器组件。

| 阈值 | 8K | 128K | 1M | 计算依据 |
|------|-----|------|-----|----------|
| `TypicalTurnTokens` | 800 | 3,000 | 5,000 | 锚点间线性插值 × adaptive系数 |
| `ScaledBudgetGate` | 2.4K | 15K | 50K | max(5% 窗口, TypicalTurn×Warmup) |
| `ScaledRatioThreshold` | 2.0 | 2.0~5.0 | 2.0~5.0 | thinking-aware |
| `ScaledWindow` | 3 | 4 | 5 | 大模型允许更多正常递增轮 |
| `ScaledWarmupTurns` | 3 | 3~5 | 3~5 | 有 thinking 时需更多 warmup |
| `ScaledLambda` | 8 | 30 | 50 | TypicalTurnTokens / 100 |
| `ScaledDeltaVTolerance` | 0.10 | 0.15 | 0.20 | 大模型单轮绝对递增量大 |
| `PreflightThresholdPercent` | 85% | 96.5% | 99.25% | 1 - 1.5×TypicalTurn / 窗口 |
| `NudgeInfo/Warn/CriticalPercent` | 70/77/82% | 81/88/93% | 84/91/96% | preflight − 15%/8%/3%（P0，保证先于 pre-flight） |
| `ScaledToolResultMaxChars` | 8K | 20K | 60K | 工具结果截断上限（P1，随窗口缩放） |
| `ScaledCircuitBreakerThreshold` | 3 | 4 | 5 | 大模型批量操作更多 |
| `ScaledAdaptiveK` | 2.5 | 2.0 | 1.5 | 大模型 IQR 小，K 应更小 |

### 9.3 Agent 集成（默认启用）

`Agent.StabilityMonitor` 在 `HostBootstrapper` 中赋值为 `StabilityHarness.CreateGuard()`（`CompositeStabilityMonitor`，聚合 GrowthRatioGuard + LyapunovMonitor，阈值从 `ModelContextProfile` 动态计算）：
- **Warmup**：前 N 轮记录每次 turn 的 non-thinking token 消耗，取中位数作为基线（N = `ScaledWarmupTurns`）
- **Gate**：累计消耗 ≥ `ScaledBudgetGate` 后开始检测
- **检测**：单 turn non-thinking 消耗 > `EffectiveThreshold`× 基线 → 计数 +1；连续 `ScaledWindow` 轮超阈值 → 触发断路（`Tripped`）
- **Adaptive 阈值**：warmup 后基于 IQR 自动调整 `RatioThreshold`，缩放系数 K = `EffectiveAdaptiveK`（大模型→1.5, 小模型→2.5）
- **断路**：`ConversationLoop` 优雅终止，返回最后一条消息，日志记录 `StabilityMonitorTriggered`
- **预算上限**：累计 > `ContextWindowTokens` → 冻结（`Frozen`）
- **Pre-flight 检查**：token 使用达到预算的 `PreflightThresholdPercent` 时提前触发 compact
- **Compact 后恢复**：`Unfreeze` 自动调用 `ResetBaseline`，使监控器重新 warmup 建立新基线

每个 turn 后 `ConversationLoop` 自动调用 `stabilityMonitor.RecordStep(incrementalTokens, thinkingTokens)` 上报 token 用量（thinking-aware）。`incrementalTokens` 由 `ComputeTurnIncrement` 计算（增量 = prompt 差 + completion），避免全量 prompt 每轮重复累计撞破预算；`Tripped`/`Frozen`/`Unstable`/`Warning` 状态均记录到日志。compact 后 `ResetPromptBaseline` 将 `_lastPromptTokens` 置 null，使下一轮从新基线起步（避免旧基线造成大幅负增量压低监控器）。

**模型切换同步**：`Agent.ApplyModelProfile(profile)` 将 profile 传播到 `ConversationLoop` + `StabilityMonitor` + `HolographicEngine` + `MessageScorer`，确保所有阈值随上下文窗口和 thinking 级别自适应。`ConversationLoop.UpdateModelProfile` 内部也同步所有注入的稳定器组件，确保直接调用时 budget 一致。

### 9.4 StabilityHarness 组件（独立 NuGet 库，`PackageId=StabilityHarness`）

| 组件 | 说明 |
|------|------|
| **CompositeStabilityMonitor** | 聚合 GRG + Lyapunov：任一触发即断路，两者互补 |
| **LyapunovMonitor** | Lyapunov 能量监控：V = sK + λ·θK，ΔV 相对增长容忍度，thinking-aware |
| **GrowthRatioGuard** | 增长比率守卫：warmup 建立基线，ratio 超阈值触发，adaptive 阈值，compact 后 baseline 重置 |
| **RGDecimator** | 冗余消息裁剪：关键词密度 + 信息密度 + 相似度惩罚，lookback 动态化 |
| **HolographicEngine (VSA)** | 向量符号架构：注册不变量 → 检测上下文漂移，严重漂移时注入 system-reminder |
| **Diagnostics** | FailureReport + PatternClassifier + CostEstimator |
| **StabilityHarness (工厂)** | `CreateGuard()` (CompositeMonitor) / `CreateGuardOnly()` (GRG only) / `CreateDecimator()` / `CreateHolographicEngine()` / `Analyze()` |

### 9.5 StabilityConfig 覆盖语义

所有 nullable 属性 + `ApplyProfile` 使用 `??=` 语义：手动设置优先于 `ApplyProfile` 计算值。如果用户手动设置了某个属性，模型切换时不会覆盖它。未手动设置的属性在 `ApplyProfile` 时从 `ModelContextProfile` 计算。

### 9.6 CircuitBreaker 动态化

CircuitBreaker 精确重复调用阈值通过 `ModelContextProfile.ScaledCircuitBreakerThreshold` 动态化：
- `Check(history)` — 使用默认阈值 3
- `Check(history, threshold)` — 使用指定阈值
- `ConversationLoop` 调用时传入 `_modelProfile.ScaledCircuitBreakerThreshold`

### 9.7 VSA 漂移检测恢复动作

漂移检测发现 cosine similarity 低于阈值时：
1. 记录日志（所有漂移）
2. 严重漂移（|similarity| < threshold/3，≈1σ 以内）时注入 system-reminder 消息到 Conversation

注入的提醒利用 LLM 的 recency bias 将对话拉回系统提示词方向。

### 9.8 GuardRail 语义检查

`IGuardRail` 接口在 LLM 调用前后检查输入/输出是否满足约束：

- `CheckInputAsync(input, ct)` — 用户消息加入 Conversation 之前
- `CheckOutputAsync(output, ct)` — LLM 响应完成后、MessageEnd 之前
- `GuardRailResult` 支持 Pass/Warn/Block 三级：Pass=通过，Warn=记录但不中断，Block=tripwire 中断循环
- `Agent.GuardRail` 属性注入，`ConversationLoop` 构造函数新增 `IGuardRail?` 参数
- `GuardRailTriggeredException` 在 Block 时抛出，Agent catch 后同步消息并向上传递

与 LlmTornado 的 `GuardRailFunction` 对比：接口化（非 delegate）、支持输入+输出、异步、warn/block 两级。

> 详细的 StabilityHarness 算法规范、性能指标和项目结构见 [StabilityHarness.md](./StabilityHarness.md)。

---

## 10. Session 架构（分片 JSONL + 分支树）

### 10.1 文件布局

```
~/.mythology/sessions/
├── --novels-我的小说--/
│   ├── 2026-07-01T08-30-00-123Z_abc123def456/    ← 会话目录（带时间戳前缀）
│   │   ├── MANIFEST.json       ← 元数据 + 分片索引
│   │   ├── 000000.jsonl        ← 第 0 代（只读）
│   │   └── 000001.jsonl        ← 第 1 代（当前活跃）
│   └── 2026-06-28T14-15-30-456Z_fff9872aaa12/
│       └── MANIFEST.json
│       └── 000000.jsonl        ← 从未 compact
└── input-history/              ← 不变
```

**目录命名格式**：`{yyyy-MM-ddTHH-mm-ss-fffZ}_{sessionId}/`。索引键从 MANIFEST.json 的 `sessionId` 字段读取，与目录名无关。

**MANIFEST 驱动**：会话元数据（标题、时间、分片索引）存储在 `MANIFEST.json` 中，`GetSession`/`GetSessions` 从 MANIFEST 快速读取，无需全量解析 JSONL。`SessionManifest` DTO 和 `ManifestSegmentRecord` 定义在 `JsonlDto.cs`。

**读取策略（B5 跨分片合并）**：`GetMessagesInternal` / `GetActiveMessages` / `GetContextMessages` 通过 `MergeEntriesAcrossSegments` **跨全部分片合并读取**——从新到旧遍历分片，按 EntryId 去重（新分片保留行副本是权威）、leaf 取最新分片的，返回按 Order 升序。保证 compact 滚动分片或分支切换后仍能取到完整历史消息（被压缩消息原文仍在旧分片）。compact 条目充当桥接，其 `parentId` 可指向旧分片条目；`ResolveActivePath` 回溯到旧分片条目时自然终止（idMap 中不存在该 id）。

**树读取**：`GetTreeStructure` 合并所有分片的条目（跳过 compact 条目自身），构建完整的对话分支树。合并后的条目数与单文件方案一致（数据总量不变，分片只是物理切分）。

**compact 语义**：每次 compact 创建新分片文件（`{newSeq:D6}.jsonl`），包含一条 `type=compact` 条目和从 `firstKeptEntryId` 开始的保留消息行。旧分片冻结为只读。活跃路径的 `leafTargetId` 隐式由最新分片的最后一条条目决定（无需独立 leaf 条目）。

### 10.2 核心决策矩阵

| # | 决策点 | 结论 | 理由 |
|---|--------|------|------|
| D1 | compact 是 event 还是独立 type？ | **独立 type = `compact`** | compact 是持久化上下文替换指令，语义与瞬时 event 截然不同 |
| D2 | compact 是否传入 LLM tornado？ | **是，以 UserMessage 角色注入摘要** | compact 摘要替代了被压缩的历史消息 |
| D3 | event 是否传入 LLM tornado？ | **否，event 是瞬时状态** | error/interrupt/model_change 不影响 LLM 对话上下文 |
| D4 | branch_summary 是否传入 LLM tornado？ | **是，以 UserMessage 角色注入** | 分支摘要保留被放弃路径的关键上下文 |
| D5 | soft_delete 是否保留？ | **否，移除** | 已有 compact 机制压缩历史 |
| D6 | 文件写入原子性策略 | **SemaphoreSlim + MD5+mtime 乐观校验 + File.Replace** | 追加操作无法用 File.Replace，需进程级锁防并发交叉 |
| D7 | `firstKeptEntryId` 代替 `keepRecent` | **是，参照 Pi** | 精确标识 compact 保留的起点 |
| D8 | Reasoning 持久化策略？ | **只持久化 `reasoningContent` 文本字段** | `reasoningParts` 是 LlmTornado 内部结构，不需要持久化 |
| D9 | 中断消息如何处理？ | **改为 Event 条目，不进 Conversation** | 节省 LLM token，语义清晰 |
| D10 | 分支实现方式？ | **同文件父指针树（Pi/Tau 验证）** | append-only 兼容，无需跨文件引用 |

### 10.3 JSONL 条目类型体系

```
JSONL 行类型           | 传入 LLM? | 说明
──────────────────────┼───────────┼──────────────────────
session               | 否        | 文件首行元数据，只出现一次
message               | 是        | 按 role 细分
  ├─ role=user        | 是        |
  ├─ role=assistant   | 是        | 含 reasoningContent + toolCalls
  ├─ role=tool        | 是        | 含 toolCallId + toolName
  └─ role=system      | 是        |
compact               | 是(摘要)  | 含 summary + firstKeptEntryId
branch_summary        | 是(摘要)  | 含 summary + fromId
event                 | 否        | 瞬时事件
  ├─ eventType=error  | 否        |
  ├─ eventType=interrupt | 否     |
  ├─ eventType=model_change | 否  |
  └─ eventType=variant_selected | 否 |
leaf                  | 否        | 活跃分支指针
label                 | 否        | 用户书签
custom                | 否        | 扩展状态（预留）
```

**条目公共字段**：

```jsonc
{
  "type": "<entry_type>",
  "id": "<8-char-hex>",           // 条目唯一标识，uuidv7 尾部 8 位
  "parentId": "<id-or-null>",     // 父条目 id，形成树结构；根条目为 null
  "timestamp": "<ISO-8601>"
}
```

### 10.4 Tree 分支机制（对齐 Pi）

```
                    ┌─ [msg_A user] ─── [msg_B assistant] ─── [msg_C user] ─── [msg_D assistant] ← active leaf
                    │
[session header] ───┤
                    │
                    └─ [branch_summary] ─── [msg_E user] ─── [msg_F assistant]
```

**规则**：
1. 每个 JSONL 条目有 `id`（8 位 hex）和 `parentId`（前一条的 id）
2. `leaf` 条目记录当前活跃分支末端（`targetId` 指向末端 entryId）
3. 分支 = 在某个已有 entry 下追加新的子节点
4. 活跃路径 = 从 leaf targetId 沿 parentId 回溯到根
5. 不在活跃路径上的条目仍然存在于 JSONL 中，但不参与 LLM 上下文构建

### 10.5 路径回溯算法

```
ResolveActivePath(entries, leafTargetId):
  if leafTargetId is null:
    return null  // 无 leaf → 所有条目都在活跃路径上

  idMap = { entry.EntryId → entry for entry in entries }
  activeIds = Set()
  currentId = leafTargetId

  while currentId is not null:
    if currentId in activeIds: break  // 循环检测
    activeIds.add(currentId)
    if currentId not in idMap: break
    currentId = idMap[currentId].ParentEntryId

  return activeIds
```

### 10.6 Compact 语义

compact 是独立的 type，不是 event 的子类型。它的角色是**上下文替换指令**：告诉读取器"从 firstKeptEntryId 往前的所有消息，用 summary 替代"。

compact 条目与 leaf 条目在**同一锁内**追加，保证原子性。compact 通过 `compact.lock` 文件锁 + 内存 `_isCompacting` 标志防重入（B4）。

**可逆恢复（B2）**：compact 条目携带 `BlockId`（`b{分片 seq}`，全局递增天然唯一）与 `CompressedMessageIds`（被压缩消息的 entryId 列表）。分片 append-only、旧分片不删除，被压缩消息原文永远在旧分片中；`ReadCompactedMessagesAsync` 按 blockId 跨分片收集原文，`DecompressTool` / `/decompress` 命令恢复（默认写文件 + 预览，`inline:true` 直接返回，不改变上下文折叠状态）。迁移前旧数据由 `002_CompactBlocks` 迁移补全 blockId 映射。

**摘要继承治理**：每次 compact 继承 `previousSummary` 并只保留「目标」+「下一步」两段（`CompactPreviousSummary`，截断 800 字符），防止摘要链式膨胀与「摘要的摘要」信息衰减；摘要末尾内嵌被压缩区尾部最近一轮原文（`BuildRecentExecutionBlock`）+ 作品状态快照（`BuildNovelSnapshot`），保证模型压缩后立即知道上一步执行状态与压缩块位置。

### 10.7 上下文构建

`buildContextEntries()` 处理流程：
1. 从 leaf 回溯活跃路径
2. 遇到 compact → 用 summary 替代 firstKeptEntryId 之前的所有消息
3. 遇到 branch_summary → 以 UserMessage 角色注入摘要
4. event 条目跳过（不传入 LLM）

---

## 11. Provider 配置体系

### 11.1 ProviderDefinition 静态类

位置：`src/Core/Data/ProviderDefinition.cs`

```csharp
public sealed class ProviderDefinition
{
    public string       DisplayName      { get; init; }  // UI 显示名
    public string       ConfigKey        { get; init; }  // 持久化键名
    public LLmProviders? LlmProvider     { get; init; }  // 对应的 LlmTornado 枚举值 (null = 自定义)
    public bool         HasBuiltInUrl    { get; init; }  // LlmTornado 是否已内置 BaseUrl
    public string?      DefaultModelHint { get; init; }  // 默认模型名提示
}
```

预设 provider 定义表：

| DisplayName | ConfigKey | LlmProvider | HasBuiltInUrl | DefaultModelHint |
|---|---|---|---|---|
| OpenAI | openai | OpenAi | true | gpt-4o-mini |
| Claude | anthropic | Anthropic | true | claude-sonnet-4-20250514 |
| DeepSeek | deepseek | DeepSeek | true | deepseek-chat |
| xAI | xai | XAi | true | grok-3-mini |
| Google | google | Google | true | gemini-2.5-flash |
| Groq | groq | Groq | true | llama-3.3-70b-versatile |
| Mistral | mistral | Mistral | true | mistral-large-latest |
| OpenRouter | openrouter | OpenRouter | true | openai/gpt-4o-mini |
| Kimi | moonshot | MoonshotAi | true | moonshot-v1-auto |
| 阿里百炼 | alibaba | Alibaba | true | qwen-turbo |
| 自定义 | custom | null | false | null |

**关于"智谱"**：LlmTornado 当前 `LLmProviders` 枚举中**没有**智谱对应的值。智谱的 API 兼容 OpenAI 格式，应归类到"自定义"——用户选"自定义"，填写 BaseUrl，用 `LLmProviders.Custom` 即可。

### 11.2 ApiKeyEntry 数据模型

```csharp
public sealed class ApiKeyEntry
{
    public string  Provider { get; set; } = "";   // 存储 ConfigKey: "openai", "custom"
    public string? Name     { get; set; }         // 自定义连接器名称
    public string? ApiKey   { get; set; }
    public string? BaseUrl  { get; set; }         // 仅自定义时使用
}
```

- 预设 provider：`Name` 为 null，`BaseUrl` 忽略
- 自定义 provider：`Name` 必填（UI 校验），`BaseUrl` 必填

### 11.3 ProviderConfig 数据模型

```csharp
public sealed class ProviderConfig
{
    public string  Default       { get; set; } = "自定义";  // 当前选中 provider 的 ConfigKey
    public string? DefaultName   { get; set; }              // 如果 Default == "custom"，存储自定义名称
    public string  DefaultModel  { get; set; } = "step-3.7-flash";
    public string  ThinkingLevel { get; set; } = "medium";
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}
```

### 11.4 SettingsDialog UI 重构要点

设置对话框（`:settings` / `Ctrl+P`）四个标签页（源码：`src/Tui/Controls/SettingsDialog.cs`）：

1. **连接器（ProviderTab）**：动态生成选项列表，自定义连接器带上名称（如"自定义: 我的智谱"）；模型选择 `Select<string>` 下拉 + 底部"手动输入"选项，选中"手动输入"时切换为 TextBox
2. **嵌入（EmbeddingTab）**：文本向量化模型配置（模型名输入 + OpenAI 预设 `text-embedding-3-small/large/ada-002`）；Provider/API Key 与连接器共享，支持 Ollama/vLLM/云端 OpenAI 兼容端点，留空降级纯 BM25 检索
3. **MCP（McpTab）**：MCP 服务器列表管理（`config.yaml` 的 `mcp.servers`）
4. **工具（PluginTab）**：插件/工具启停配置（`config.yaml` 的 `tools` 段）

**ProviderTab 模型列表加载**：切换 provider 时异步调用 `tornadoApi.Models.GetModels(provider)` 获取模型列表；API 调用失败时显示 `[加载失败，请手动输入]` 并回退到 TextBox。

---

## 12. 快捷键与命令参考

### 12.1 TUI 键盘快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+P` | 打开命令面板（设置） |
| `Ctrl+H` | 打开帮助对话框 |
| `Ctrl+O` | 打开小说项目对话框 |
| `Ctrl+E` | 打开会话切换对话框 |
| `Ctrl+L` | 清除对话和工具日志 |
| `Ctrl+S` | 中断 Agent |
| `Ctrl+T` | 切换主题 (Dark/Light) |
| `Ctrl+Q` | 退出程序 |
| `Tab` | 输入补全（`/` 命令 / `#` 实体引用 / `@` 文件路径） |
| `Enter` | 发送消息 |
| `Ctrl+J` | 输入框换行 |
| `Esc` | Agent 运行时中断 / 空闲时清空输入 |
| `Ctrl+C` | 同 Esc（通过 SIGINT 路由） |

### 12.2 Slash 命令

| 命令 | 说明 |
|------|------|
| `/tools list` | 列出所有可用工具（按插件分组） |
| `/tools <插件ID>` | 列出指定插件的工具 |
| `/tools <插件ID>:<工具名>` | 列出指定工具详情 |
| `/state` | 查看 Agent 状态 + 工具统计 + 写作统计（弹窗） |
| `/history` | 查看对话历史（弹窗） |
| `/clear` | 清除对话历史 |
| `/resume <novel-id>` | 恢复会话 |
| `/theme <dark\|light\|toggle\|id>` | 切换主题 |
| `/compact [N]` | 压缩上下文（保留最近 N 条，默认 20） |
| `/compact smart [N]` | 智能压缩（RGDecimator 评分，保留信息密度最高的轮次） |
| `/tree` | 对话分支树 |
| `/session` | 切换会话 |
| `/mode` | 循环增强模式（none → semi → full） |
| `/login [--device] [<url>]` | 云端登录（主命令；默认本地回调 OAuth，`--device` 强制设备流，`<url>` 覆盖云端地址） |
| `/logout` | 云端登出（清除凭据 + 停止心跳） |
| `/cloud` | 云端登录（`:cloud login` 快捷方式） |
| `/commands` | 显示命令列表 |
| `/help` | 显示帮助（弹窗） |
| `/stats` | （隐藏别名）合并到 `/state` |
| `/variants` `/var` | （隐藏别名）重新打开变体集 |

### 12.3 冒号命令（云端，CHANGE_07）

| 命令 | 说明 |
|------|------|
| `:cloud login` | 云端登录（默认本地回调 OAuth，失败回退设备流：QR 码 + 用户码 + SSE 轮询，手动 API Key 兜底） |
| `:cloud status` | 显示云端连接状态 + PublicId + 过期时间 |
| `:cloud logout` | 清除本地凭据 + 停止心跳 |

### 12.4 输入补全

| 前缀 | 补全内容 | 说明 |
|------|----------|------|
| `/` | Slash 命令 | 命令补全，含 `/tools <插件ID>` 二级补全 |
| `#` | 实体名引用 | 两阶段：`#类型:` → `#类型:名称`，提交时替换为 `类型[ID:xxx]:名称` |
| `@` | 文件路径 / 实体提及 | 含 `/`、`./`、`~` 时目录导航补全；否则实体提及过滤 |

### 12.5 启动选项

| 选项 | 说明 |
|------|------|
| `--debug` | 输出详细调试日志 |
| `--mock` | Mock 模式（无 LLM） |
| `--novel <id>` | 启动时加载指定小说项目 |
| `--session <id>` | 恢复指定会话 |

### 12.6 环境变量

| 变量 | 说明 |
|------|------|
| `OPENAI_API_KEY` | OpenAI API Key |
| `ANTHROPIC_API_KEY` | Anthropic API Key |
| `GOOGLE_API_KEY` | Google API Key |
| `GROQ_API_KEY` | Groq API Key |
| `XAI_API_KEY` | xAI API Key |
| `DEEPSEEK_API_KEY` | DeepSeek API Key |
| `MISTRAL_API_KEY` | Mistral API Key |
| `OPENROUTER_API_KEY` | OpenRouter API Key |
| `BING_API_KEY` | Bing Search API Key |
| `NOVEL_PLUGINS_DIR` | 自定义插件目录（最高优先级） |
| `MYTHOLOGY_CLOUD_URL` | 云端地址（默认生产地址 `https://api.mythology.work`），`/login` / `:cloud login` 与检索 REST / Harness 端点共用（收口 `CloudEndpoints.ResolveCloudBaseUrl`） |

---

## 13. 实体引用系统（# 补全）

### 13.1 概述

新增 `#` 符号触发实体引用补全，沿用现有 `@` / `/` 的 popup 模式，分为两级输入：

1. **Stage 1**: 输入 `#` → popup 显示实体类型列表（角色/组织/地点/情节/道具/关系/大纲，共 7 种，见 `EntityDisplayHelper.HashDisplayTypes`）
2. **Stage 2**: 选中类型后（`#角色:`）→ popup 显示该类型下所有实体名，支持 prefix 过滤
3. **空格确认**: 选中后末尾带空格，popup 消失
4. **提交预处理**: `#角色:张三` → `角色[ID:xxx]:张三` 发送给 LLM，输入框保持干净

### 13.2 核心设计原则

- **输入框**: 只显示中文名，保持可读性（`#角色:张三`）
- **LLM 侧**: 提交时自动解析名称→ID，LLM 收到结构化引用（`角色[ID:ab12cd34ef01]:张三`）
- **插件依赖**: 所有实体操作工具均要求 ID 参数，预处理为其直接提供 ID

### 13.3 改动清单

#### SetUpPanel.cs — 场景 Tab + 名称字典

- 新增场景 Tab（`_sceneAccordion` / `_sceneTab`）
- 新增全类型名称字典（`_factionNames` / `_locationNames` / `_plotlineNames` / `_itemNames` / `_relationshipNames` / `_chapterNames` / `_sceneNames`）
- 新增公开方法 `GetEntityNamesByType(entityType)` 和 `TryResolveEntityId(entityType, entityName)`

#### MythPromptEditor.cs — 可配置结尾字符

- 新增 `_trailingOnApply` 字段
- Stage 1 选中类型后末尾追加 `":"`，Stage 2 保持默认空格

#### TuiApp.cs — `#` 触发 & 补全 & 预处理

- Update loop 新增 `isHashRef` 检测
- `TriggerCompletion` 新增 `#` 分支（两阶段补全）
- 静态映射数据：`HashDisplayTypes` / `HashDisplayToKeyMap`
- 提交预处理：`InlineEntityReferences` 正则替换

### 13.4 转换示例

| 输入框 | 发送给 LLM |
|---|---|
| `#角色:张三` | `角色[ID:ab12cd34ef01]:张三` |
| `#地点:凌霄峰` | `地点[ID:bc23de45ef02]:凌霄峰` |
| `#大纲:决战之夜` | `大纲[ID:cd34ef56ab03]:决战之夜` |

### 13.5 边界情况

| 场景 | 行为 |
|---|---|
| 同名实体（两个"张三"） | `TryResolveEntityId` 返回第一个匹配 |
| 非法类型 `#xyz:xxx` | 无 mapping → 原样保留 |
| `#:` 空类型 | 无 mapping → 不触发 popup |
| 多个 `#`（`#角色:张三 #地点:`） | `lastIndexOf('#')` 只补全最后一个 |
| 提交时名称不存在 | 原样保留，LLM 通过上下文处理 |

---

## 14. 数据迁移

`MigrationRunner` 在启动时执行数据迁移（优先于所有服务构建），位于 `Core.Data.Migrations` 命名空间。确保从旧版 DB 存储到文件存储的平滑过渡。

### 14.1 从 DB 到文件的映射

| 旧 DB 表 | 新存储位置 | 格式 |
|----------|-----------|------|
| Novels | `novels/{name}/novel.yaml` | YAML |
| Characters | `novels/{name}/characters/{id}.yaml` | YAML |
| Chapters | `novels/{name}/chapters/ch{N}.md` | MD + front matter |
| Plotlines | `novels/{name}/plotlines/{id}.yaml` | YAML |
| Factions | `novels/{name}/factions/{id}.yaml` | YAML |
| Locations | `novels/{name}/locations/{id}.yaml` | YAML |
| Items | `novels/{name}/items/{id}.yaml` | YAML |
| Relationships | `novels/{name}/relationships.yaml` | YAML |
| SceneDefinitions | `novels/{name}/scenes/ch{N}.yaml` | YAML |
| AgentSessions | `~/.mythology/sessions/` | JSONL |
| ChatMessages | `~/.mythology/sessions/` | JSONL |
| AppSettings | `~/.mythology/config.yaml` | YAML |

### 14.2 不再需要的 DB 组件

| 组件 | 原因 |
|------|------|
| `NovelDbContext` | 创作资产迁移到文件，会话迁移到 JSONL |
| `NovelStore`（EF Core 版） | 被 `FileNovelStore` 替代 |
| `noveldata.db` | 完全废弃 |
| 所有 EF Core Migrations | 不再需要 |

### 14.3 数据迁移（`MigrationRunner`）

`MigrationRunner` 在启动时按 `Version` 升序执行所有 `IDataMigration` 实现，并写入 `~/.mythology/version.json`。当前有两个迁移：

| 迁移 | 版本 | 说明 |
|------|------|------|
| `001_SessionSegments` | v1 | 将会话 JSONL 从单文件迁移为分片目录格式（`MANIFEST.json` + `NNNNNN.jsonl`） |
| `002_CompactBlocks` | v2 | 给旧 compact 条目补充 `BlockId` + `CompressedMessageIds`，实现历史数据可恢复 |

**002_CompactBlocks** 推导原理：compact 滚动分片是 append-only，每个 compact 的覆盖区间 = (上一个 compact 保留起点, 本 compact 保留起点) 之间的消息；据此推导 `CompressedMessageIds`，`BlockId = b{分片 seq}`。幂等（已有 blockId 跳过），迁移后二次扫描验证，有残留则抛异常阻止 version.json 写入（防「假装成功」）。

**001_SessionSegments** 扫描 `~/.mythology/sessions/` 下所有 `{sessionId}.jsonl` 文件，将其转换为 `{sessionId}/000000.jsonl + MANIFEST.json` 格式。幂等运行：已使用新分片格式的会话目录跳过。迁移后所有读写操作由 `FileSessionStore` 的 `SessionSegmentManager` 管理。

### 14.4 自动迁移（首次访问时触发）

除了显式的 MigrationRunner 迁移外，`FileSessionStore.GetOrCreateSegmentManager` 在检测到旧格式 `{sessionId}.jsonl` 文件时会**同步自动迁移**：
1. 创建 `{sessionId}/` 目录
2. 重命名 `{sessionId}.jsonl` → `{sessionId}/000000.jsonl`
3. 扫描消息行提取元数据（title、时间范围、消息数）
4. 写入 `MANIFEST.json`
5. 更新 `_sessionIndex` 指向新目录

这种方法确保旧数据在任何路径下都能无缝过渡到新格式，无需用户事先运行迁移命令。

---

## 15. 审计与完成度检查

### 15.1 审计日志

每次实体变更都记录审计日志（`WriteAuditLogs`），包含：
- 实体类型 + ID + 操作（Create/Update/Delete）
- 变更字段（`FieldChanges` 字典）
- 来源（`AuditSource`: Ai/User/System）
- 触发工具名 + 会话 ID

### 15.2 AgentAuditHook

`ShouldStopAfterTurn` 回调在每个 turn 结束后运行完成度审计：
- 草稿章节数量
- 孤立角色（无任何关系）
- 未回收伏笔数量

审计未通过时，通过 `SteeringQueue` 注入消息，引导 LLM 继续完善。

### 15.3 LoadNovelHook

`AfterToolCall` 钩子，在工具返回 `INovelDetails` 时自动设置 `Agent.CurrentNovelId`，切换当前小说上下文。

### 15.4 Explore Tool 与变体选择

`ExploreTool` 允许 LLM 生成多个创作方向变体，用户在 TUI 中选择：

1. LLM 调用 `explore` 工具，返回 `ExploreDetails`（含 `VariantResult[]`）
2. TUI 拦截 `OnExploreVariant` 事件，弹出变体选择对话框
3. 用户选择后：
   - 持久化 `variant_selected` 事件到 JSONL
   - 将选中变体提升为正式 assistant 消息
   - LLM 根据选中方向继续创作
