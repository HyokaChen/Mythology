# Mythology

终端下的 AI 小说写作助手，基于 **ReAct Agent Loop** 架构：LLM 自主决策调用工具、推进写作，关键创作判断（重大剧情、主线、角色命运）由作者确认。

本地创作是产品主路径；云端（云端私有仓库）按需提供 embedding、检索与分析 Harness 能力。

---

## 安装

```bash
dotnet tool install --global Mythology
```

安装后直接使用：

```bash
mythology                          # 启动交互式 TUI
mythology --novel abc123           # 打开指定小说
mythology --debug                  # 调试模式
mythology --mock                   # Mock 模式（无 LLM）
```

更新 / 卸载：

```bash
dotnet tool update --global Mythology
dotnet tool uninstall --global Mythology
```

### 桌面客户端（Desktop）

发行页 [Releases](https://github.com/HyokaChen/Mythology/releases) 按版本提供三平台安装包：

| 平台 | 产物 | 安装 |
| --- | --- | --- |
| Windows x64（Win10 1809+） | `Mythology-Desktop-<ver>-win-x64-setup.exe` | 运行安装向导（可选“仅当前用户”，无需管理员） |
| Linux x64 | `Mythology-Desktop-<ver>-linux-x64.deb` | `sudo apt install ./Mythology-Desktop-<ver>-linux-x64.deb`，启动器 `mythology-desktop` |
| macOS Apple Silicon | `Mythology-Desktop-<ver>-osx-arm64.dmg` | 挂载后把 `Mythology.app` 拖进 Applications |

发行包都不带数字签名（未购买证书），首次运行会被系统拦一次，放行方法：

- **Windows**：SmartScreen 提示“未知发布者”→ 点「更多信息」→「仍要运行」。
- **macOS**（仅 ad-hoc 签名、未公证）：可能在启动时提示无法验证开发者。任选其一：
  1. 在「应用程序」里右键 `Mythology.app` →「打开」→「打开」（只需一次）；
  2. 终端去掉下载隔离属性（一劳永逸）：
     ```bash
     xattr -dr com.apple.quarantine /Applications/Mythology.app
     ```
  确认状态：`codesign -dv --verbose=2 /Applications/Mythology.app`（应显示 `Signature=adhoc`）、
  `xattr -l /Applications/Mythology.app`（应为空）。dmg 内附 `安装说明.txt`。
- **Linux**：deb 自带依赖声明，`apt install ./xxx.deb` 会自动补齐运行库。

每个产物都有同名 `.sha256` 校验文件：`sha256sum -c Mythology-Desktop-<ver>-<rid>.deb.sha256`（macOS 用 `shasum -a 256 -c`）。
桌面端与 TUI 共用 `~/.mythology`（会话、配置、小说索引），卸载只删程序目录不动数据。

### 配置 LLM API Key

```bash
# 首次启动自动创建 ~/.mythology/config.yaml
# 编辑填入 API Key：

providers:
  default: openai
  default_model: gpt-4o-mini
  api_keys:
    - provider: openai
      api_key: sk-...
    - provider: custom          # 自定义提供商
      name: my-provider
      api_key: sk-xxx
      base_url: https://api.example.com/v1

# 或沿用环境变量
export OPENAI_API_KEY="sk-..."
```

也支持 `appsettings.json` 兼容配置（见 `src/Host/appsettings.json`）。

### 连接云端（可选）

本地创作与检索**不依赖云端**即可工作。云端用于：检索 REST（`/v1/retrieval`）、写作分析与增强 Harness（`/v1/agent/runs`）。embedding 由本地 `config.yaml` 的 `Embedding` 段配置（经 LlmTornado 接 OpenAI 兼容端点），未配置时降级纯词法。

```bash
# 指定云端地址（默认生产地址 https://api.mythology.work，开发环境用环境变量覆盖）
export MYTHOLOGY_CLOUD_URL="https://cloud.mythology.example"

# TUI 内登录（默认本地回调 OAuth，失败回退设备流 QR 码 / 用户码）
:cloud login
:cloud status        # 查看连接状态
:cloud logout        # 清除凭据
```

未登录 / 云端不可达时自动降级：检索退回纯词法（BM25/FTS5），云端分析工具返回"云端不可用"提示，本地创作不中断。

---

## 架构概览

### 组件结构

```
┌──────────────────────────────────────────────────────────────────────────┐
│                           Mythology (.NET Tool)                          │
│                  Host + TUI/Desktop → dotnet tool install --global        │
└───────┬──────────────┬───────────────┬──────────────┬───────────────────┘
        │              │               │              │
┌───────▼─────┐  ┌─────▼──────┐  ┌─────▼─────────┐  ┌▼───────────────────┐
│ Mythology.Core│  │  Tui/     │  │   Plugins/    │ │ StabilityHarness   │
│ (NuGet 包)   │  │  Desktop  │  │ (运行时 DLL)  │ │ (独立 NuGet 库)     │
│ 契约+数据层   │  │ XenoAtom/ │  │ NovelTools 67│ │ Lyapunov+GRG+VSA    │
│ Agent 引擎   │  │ Avalonia  │  │ WritingTools │ │ Host 自动注入        │
│ ToolInjector │  │ 终端/GUI  │  │ Rag/Web/File │ └────────────────────┘
│ RetrievalKernel│ │ 云端登录  │  │              │
│ AgentMemory   │  │ 补全/分支 │  │              │
└───────┬──────┘  │  compact  │  └──────┬───────┘
        │         └───────────┘         │
┌───────▼───────────────────────────────▼──────────────────────────────────┐
│  ClientPlatform（本地↔云端客户端，纯 HttpClient）                         │
│  RetrievalRestClient · HttpFictionHarnessClient                          │
│  DeviceAuthorizationClient · OAuthLoopbackClient · CloudSessionStore     │
└───────────────────────────────────────────────────────────────────────────┘
```

**组件职责**：

| 组件 | NuGet / 形态 | 说明 |
|------|------|------|
| **Mythology.Core** | NuGet 包 `Mythology.Core` | 插件契约、文件/会话数据层、Agent 引擎（`ConversationLoop` + `ToolInjector`）、**共享检索内核**（`RetrievalKernel`：dense vec0 + FTS5 + metadata 过滤 + RRF 融合）、**Agent 记忆子系统**（`AgentMemoryStore` + 18 个记忆/偏好工具）、**按需工具检索**（`ToolSearchEngine`）、**上下文压缩（可逆恢复）**（`FileSessionStore` 分片 + `DecompressTool` + `[mN]` ref 锚定）。第三方插件只需引用此包 |
| **Host + Tui** | .NET Tool `mythology` | DI 组装、配置、启动入口、XenoAtom 终端 UI |
| **Desktop** | Avalonia GUI（`src/Desktop/`，已入 .slnx） | Avalonia 12 + ReactiveUI + SukiUI 桌面界面，复用 Core 共享注册（`CoreServiceExtensions`）：MainWindow / ConversationView / SetUpPanelView / TodoPanelView / SettingsView，经 `DesktopAgentBridge` + `StreamEventHandler` 桥接 Agent；装配 `CloudDiagnosis` 云端诊断链路（DiagnosisScheduler + HumanDecisionBridge + MonotonyWatcher）。注意：Desktop 当前未接 ToolInjector / tool_search / decompress / AgentMemory 工具注册（仅 Host 装配） |
| **ClientPlatform** | 库 `ClientPlatform` | 云端客户端：检索 REST / 分析 Harness / 设备授权 / 本地回调 OAuth / 凭据存储（DPAPI 加密）。纯 HttpClient，无 LlmTornado 依赖。embedding 客户端（CloudEmbeddingClient）**未实现**——embedding 走本地 LlmTornado `EmbeddingService` |
| **StabilityHarness** | 内置 | Lyapunov + GrowthRatioGuard + RGDecimator + VSA。Host 启动时自动注入 `CompositeStabilityMonitor`，阈值从 `ModelContextProfile` 动态计算 |
| **Plugins/*** | 运行时 DLL | Host 启动时自动发现并加载：NovelTools（67 工具，含 23 个云端分析器）、WritingTools（10）、SemanticRetrievalTool（8）、WebSearchTools（6）、FileSystem（6；`BashTool` 已实现但未注册）。另 Host 注册 Core 组 20 工具（tool_search + decompress + 13 个 AgentMemory + 4 个偏好工具 + tool_analyze_style）；NovelTools 与 WebSearchTools 的 `web_search` 同名，注册表按名覆盖去重，总数 116 |

### 交互逻辑（核心）

```
用户输入 ──► Agent.Prompt ──► ConversationLoop（ReAct 循环）
                 │                    │
                 │              ┌─────▼──────────────┐
                 │              │  BuildChatRequest   │
                 │              │  ToolInjector 按需注入│  ← 常驻 3 + 已展开 LRU 20 + 非延迟
                 │              └─────┬──────────────┘
                 │                    │
                 │        ┌───────────▼───────────┐
                 │        │  LLM（LlmTornado）      │
                 │        │  · 9 段 Prompt 骨架     │
                 │        │  · 工具调用 / 文本流式   │
                 │        └───────────┬───────────┘
                 │                    │ tool_calls
                 │        ┌───────────▼───────────┐
                 │        │  ToolDispatcher        │
                 │        │  · before/after 钩子    │
                 │        │  · 断路器 / 审计        │
                 │        └───────────┬───────────┘
                 │                    │ 执行 AgentTool
                 │        ┌───────────▼───────────┐
                 │        │  工具（插件）           │
                 │        │  本地：文件/会话/检索    │
                 │        │  云端：分析 Harness     │
                 │        └───────────────────────┘
                 ▼
            用户确认（Human-in-the-loop）
            · Explore 变体选择
            · ask_user 决策点
            · Steering 中断引导
            · 写回前 EditHunk Diff 确认
```

**按需工具注入（Tool Search）**：注册表共 116 个工具，但并非全部注入每轮请求。`ToolInjector.BuildRequestTools()` 注入：
- 常驻工具：`tool_search`、`ask_user`、`todo_write`
- 已展开工具：`tool_search` 命中后按需展开（LRU 上限 20）
- 非延迟工具：未命中延迟名单且未标 `DeferLoading` 的工具

延迟插件名单默认为 `Semantic Retrieval Tool`、`Web Search Tools`（+ 所有第三方插件，按 `PluginManifest.Name` 匹配），可用 `Agent:ToolSearchDeferredPlugins` 覆盖；18 个记忆/偏好工具全部 `DeferLoading=true`。默认配置下每轮注入约 84 个工具 schema（核心写作/分析工具常驻——2026-08-23 起延迟名单从 4 插件收窄为 2 插件，以保证 write_chapter/diagnose_writing 等主链路工具直接可用）。可配置 `Agent:ToolSearchMode=all` 关闭按需注入。

**云端协作边界**：

| 能力 | 本地 | 云端（云端私有仓库） |
|------|------|----------------------|
| 创作资产（YAML/MD） | ✅ 唯一事实源 | — |
| 会话（JSONL） | ✅ | — |
| 语义检索 | ✅ 词法降级（本地 embedding 可选） | ✅ 检索 REST（`/v1/retrieval`） |
| 写作分析（23 分析器 + 诊断） | — | ✅ Harness 已接通（`IFictionHarnessClient` DI 注册，见 `CloudAnalysisTool`） |
| 写作增强（9 维度） | ✅ 生成/写回 | ✅ 诊断阶段走云端 Harness |
| 上下文压缩 | ✅ | — |

### 三层存储架构

```
┌─────────────────────────────────────────────────────────┐
│                    存储架构总览                           │
├─────────────┬──────────────────┬────────────────────────┤
│   创作资产   │    运行时会话     │      语义索引          │
│  (项目目录)  │  (~/.mythology)  │    (vectors.db)       │
│             │                  │                        │
│ YAML/MD 文件 │  JSONL 文件      │ SQLite vec0 + FTS5    │
│ 人可读写     │  追加写入         │ 检索派生数据           │
│ Git 版本控制 │  按项目/时间组织   │   可从文件重建         │
└─────────────┴──────────────────┴────────────────────────┘
```

**设计原则**：创作资产是文件（人可编辑、可 Git 管理），运行时数据是 JSONL（追加写入、人类可读）；SQLite 承载检索内核的 dense、FTS5、metadata 与索引状态。小说知识索引均为可重建派生数据；用户长期记忆的原始规则/偏好则必须可审查、可撤销。

### 数据访问分级

插件按 `PluginTrustLevel` 分为三级，权限权威来自 DLL 上的 `[assembly: PluginPermission(...)]`；`plugin.json` 只作为构建产物。`PluginLoader` 用 `PluginCapabilities` 将生效权限映射为可获取的服务，并通过 `RestrictedPluginServiceProvider` 只放行该集合。

| 信任级别 | 判定 | ServiceProvider | 数据访问 |
|---|---|---|---|
| `Builtin` | `AppContext.BaseDirectory/plugins/` | 原始 DI 容器 | 完整 `INovelStore` CRUD |
| `Verified` | DLL 公钥 token 在白名单 | 受限容器，按声明权限放行 | 取决于 `[PluginPermission]`，`novel:write` 可获 CRUD |
| `External` | 无签名或签名未在白名单 | 受限容器，声明权限再裁剪 | 仅 `ExternalAllowed` 内权限；`novel:read` 对应 `IPluginStore` 只读访问 |

`IPluginStore` 是独立于 `INovelStore` 的只读接口，`PluginStore`（sealed）委托 `INovelStore` 实现；小说摘要返回精简 `NovelSummary`（Id/Title/Genre）。External 插件即使声明 `novel:write` 也会被裁剪，不能取得 `INovelStore`。

### 插件目录优先级

同名插件高优先级覆盖低优先级：

```
1. NOVEL_PLUGINS_DIR 环境变量     （最高 — 测试/调试）
2. .mythology/plugins (当前目录)   （项目级）
3. ~/.mythology/plugins (全局)     （用户安装）
4. AppContext.BaseDirectory/plugins （内置 — 最低）
```

内置插件由 Host 构建时通过 `BuildPlugins` MSBuild Target 编译并输出到插件目录。

---

## 存储详解

### 1. 创作资产 — 项目本地文件

一部小说 = 一个文件夹，**可以 Git 管理**：

```
novels/
└── 神话纪元/
    ├── novel.yaml                # 小说元数据
    ├── characters/
    │   ├── 李明.yaml             # 角色设定（YAML，手工可编辑）
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
    ├── relationships.yaml        # 角色关系网（集中管理）
    ├── scenes/
    │   ├── ch01.yaml             # 场景编排
    │   └── ch02.yaml
    └── chapters/
        ├── ch01.md               # Markdown + YAML front matter
        └── ch02.md               # 任何编辑器可直接预览
```

**角色 YAML 示例**：

```yaml
id: a1b2c3d4e5f6
name: 李明
gender: 男
age: 少年
role: protagonist        # protagonist / supporting / minor / antagonist
personality: 内向敏感、外冷内热
appearance: 黑发碧眼、身材高挑
background: 孤儿出身，自幼在天机阁长大
motivation: 寻找身世之谜
tags: [主角, 天机阁]
abilities: 剑术精湛、灵觉敏锐
status: active           # active / deceased / missing / transformed
```

**角色定位枚举 (`CharacterRole`)**：

| 值 | 中文标签 | 说明 |
|---|---|---|
| `protagonist` | 主角 | 核心视角角色 |
| `supporting` | 配角 | 默认值，大多数角色的定位 |
| `minor` | 小角色 | 一次性出场或工具人 |
| `antagonist` | 反派 | 与主角对立的角色 |

**章节 Markdown 示例**：

```markdown
---
id: x1y2z3a4b5c6
order: 1
title: 序幕
status: draft
---

长安城的黄昏，残阳如血...
```

**文件优势**：
- ✅ 文本编辑器直接打开、grep、diff
- ✅ 章节用 Markdown，任何编辑器可预览
- ✅ Git 版本控制，追踪每一次创作变更
- ✅ 跨项目复用角色模板（复制 YAML 文件）
- ✅ 手工修复数据（无需写 SQL）

### 2. 运行时会话 — `~/.mythology/sessions/`

采用 **分片 JSONL 格式**（v2，2026-07-29），由 `FileSessionStore` 管理。每个会话从单 `{sessionId}.jsonl` 文件改造为**目录**，内含 `MANIFEST.json` 元数据、多个世代分片文件和**时间戳前缀目录名**：

```
~/.mythology/
├── sessions/
│   └── --novels-神话纪元--/
│       ├── 2026-07-01T08-30-00-123Z_abc123def456/  ← 带时间戳的会话目录
│       │   ├── MANIFEST.json       ← 元数据 + 分片索引
│       │   ├── 000000.jsonl        ← 第 0 代（原始消息，只读）
│       │   ├── 000001.jsonl        ← 第 1 代（compact 后只读）
│       │   └── 000002.jsonl        ← 第 2 代（当前活跃，append 中）
│       └── 2026-06-28T14-15-30-456Z_fff9872aaa12/  ← 时间戳便于文件系统排序
│           ├── MANIFEST.json
│           └── 000000.jsonl        ← 从未 compact 的唯一活跃分片
├── config.yaml                     # 全局配置
├── cloud-session.json              # 云端凭据（ApiKey/PublicId/WorkspaceId/Plan）
├── logs/                           # Serilog 日志
│   └── mythology-{date}.log
└── input-history/                  # 输入历史
```

**目录命名格式**：`{yyyy-MM-ddTHH-mm-ss-fffZ}_{sessionId}/`。时间戳来自会话创建时的 UTC 时间（自动迁移时来自旧文件的创建时间）。索引键从 MANIFEST.json 的 `sessionId` 字段读取，与目录名无关。

**分片规则**：每次 compact 创建下一个序号的文件。活跃分片 = 最大序号的分片。`GetSession`/`GetSessions` 从 MANIFEST 快速读取元数据，无需全量解析 JSONL。旧格式 `{sessionId}.jsonl` 文件在首次访问时自动迁移。

**JSONL 条目类型**：

| type | 说明 |
|------|------|
| `message` | 对话消息（user/assistant/tool/system） |
| `compact` | 上下文压缩摘要（每个分片的第一行） |
| `event` | 事件条目（interrupt/error/model_change/variant_selected） |
| `leaf` | 活跃分支指针 |
| `label` | 用户书签/标记 |
| `branch_summary` | 被放弃分支的 AI 摘要 |

**JSONL 优势**：
- ✅ 分片存储，单个文件有明确上界
- ✅ 追加写入，无事务，不会损坏
- ✅ `cat` / `grep` / `jq` 直接查看
- ✅ 会话恢复：回放 `type=message` 的行
- ✅ 审计追踪：`event` 条目记录 entity_change
- ✅ 分支支持：`leaf` + `branch_summary` 支持对话分支树
- ✅ 上下文压缩：`compact` 创建新分片，旧分片冻结为只读
- ✅ 自动迁移：旧单文件格式无缝过渡

### 3. 共享检索内核 — `vectors.db`（唯一保留的 SQLite）

`vectors.db` 承载共享检索内核：`RetrievalKernel` 先执行 metadata/权限过滤，再并行召回 dense `vec0` 与 FTS5（中文 `simple` tokenizer）的候选，以 RRF 融合、按 `RankingProfile` 重排。它覆盖三个逻辑域：

- `novel_knowledge`：小说实体摘要、章节约 300 字语义块、场景与时间线；均为可重建派生数据。
- `user_memory`：用户显式规则、已确认偏好与候选推断；候选推断默认不注入 prompt，原始记录必须可审查、可撤销。
- `agent_memories`：Agent 自身工作记忆（`agent_memories` / `agent_journal` / `agent_snapshots` 三表，由 `AgentMemoryStore` 管理）。精确混合评分召回（`w_sim·相似 + w_rec·新鲜 + w_imp·重要 + w_use·有用`），`mark_useful`/`mark_useless` 反馈学习 + spaced repetition；13 个记忆工具 + 4 个偏好工具 + `tool_analyze_style` 全部按需发现（DeferLoading），`AgentMemoryHook` 自动标记使用。

**Embedding 走本地 LlmTornado（显式 opt-in）**：`CoreServiceExtensions` 按优先级选择 embedding 实现——入口注入的 `embeddingFactory`（测试覆盖）＞ `config.yaml` 的 `Embedding` 段独立密钥（`EmbeddingService`，OpenAI 兼容端点如 bge-m3 / qwen-embedding / text-embedding-3-small）＞ 回退复用聊天连接器 ＞ `DummyEmbeddingService`。**未配置 embedding 模型名时不发任何远程 embedding 请求**：`ToolSearchEngine` 检测到 `DummyEmbeddingService` 直接跳过 dense 通道（纯 BM25），`RetrievalKernel` 遇零向量设 `DenseTopK=0` 降级纯词法。向量维度固定 `VectorDefaults.Dimension = 1024`（不做启动探测，避免远程调用拖住启动链路）。云端 `CloudEmbeddingClient` 尚未实现。

文件保存通过 `FileNovelStore.OnEntityChanged` 进入后台索引队列；`VectorSyncService` 在启动或重建时补偿对账。可执行全量重建：

```bash
mythology index --rebuild       # 从小说事实源重建检索索引
```

详见 [vec.md](./docs/vec.md)。

---

## Markdown 渲染管线

`MarkdownConverter.Convert()` 实现完整的预处理管线，将 Markdown 转为 XenoAtom ANSI markup：

```
原始 Markdown
  → PreprocessEmojiKeycaps()     // emoji keycap 序列（1️⃣→1.）
  → PreprocessMarkdownTables()   // 管道表 → Unicode 盒绘表格（含列对齐）
  → PreprocessImages()           // ![alt](url) → 盒绘框卡片展示
  → PreprocessLinks()            // [text](url) → 追加 dim URL 显示
  → PreprocessTaskLists()        // [x]→☑, [ ]→☐
  → InsertZwspAroundEmphasis()   // CJK 与 */_ 之间插入 ZWSP
  → MarkdownMarkupConverter.Convert()  // XenoAtom 库：AST → ANSI markup
  → 移除所有 ZWSP                 // markup.Replace("\u200B", "")
  → StripHeadingHashes()          // 移除 # 前缀（跳过 [code] 块）
  → InsertHeadingDecorations()    // H1 双线 ═══ / H2 点线 ···
  → 输出
```

**已实现的渲染增强**：

| 特性 | 说明 |
|------|------|
| **表格盒绘** | 管道表 → Unicode 盒绘（┌┬┐│├┼┤└┴┘），支持列对齐（左/右/居中） |
| **VisibleLength 增强** | 处理 `**`/`*`/`__`/`_`/`~~`/链接/图片/内联代码/转义字符/surrogate pair Emoji 的宽度计算 |
| **CJK ZWSP** | CJK 文本与 `*`/`_` 强调符号之间插入零宽空格，修复 Markdig 解析 |
| **标题层次** | H1-H6 颜色明度递减（金色→暗灰色），H1 下划线+双线 `═══`，H2 点线 `···`，H6 Dim |
| **引用块** | 前缀 `▎ ` + 暗石色，替代默认 `│ ` |
| **Alert 块** | NOTE/TIP/IMPORTANT/WARNING/CAUTION 五色独立配置 |
| **列表 bullet** | `▸` 替代默认 `•` |
| **零宽字符** | ZWSP/VS/ZWJ/组合用标记宽度为 0，Emoji 宽度为 2 |
| **图片卡片** | `![alt](url)` → Unicode 盒绘框展示文件名 + alt 文本 |
| **链接 URL** | `[text](url)` 后追加 `[dim](url)[/]`，终端用户可看到链接目标 |
| **任务列表** | `[x]` → `☑`，`[ ]` → `☐` |
| **水平线** | 实线 `────`，占满整行 |
| **Diff 展示** | ✅ EditHunk → DiffComputer（DiffPlex Myers diff）→ FoldContext GitHub 式折叠 → Markup + ANSI color 渲染 |

---

## 核心设计

| 概念 | 说明 |
|------|------|
| **ConversationLoop** | 基于 LlmTornado Conversation API 的 ReAct 循环引擎。`BuildChatRequest` 通过 `ToolInjector` 按需注入工具 |
| **Agent** | 状态机：对话历史 + 事件流 + Steering/FollowUp 队列。支持运行时切换 Provider（`TornadoApi` 可设置）、模型 / ThinkingLevel / 上下文窗口 |
| **ToolInjector** | 按需工具注入器：常驻（`tool_search`/`ask_user`/`todo_write`）+ 已展开（LRU 20）+ 非延迟工具。会话切换（`CurrentNovelId` 变化）时清空已展开集合 |
| **ToolSearchEngine** | 工具检索：jieba 分词 + BM25 倒排 + dense embedding + RRF 融合（k=60）。索引一次构建，进程内常驻。云端不可达 → 纯 BM25 |
| **ToolSearchTool** | `tool_search` 工具：搜索频率熔断（8 次/会话），命中后自动展开完整工具定义 |
| **PromptAssembly** | 9 段 Prompt 骨架：role → conversation → tooling → context → planning → errors → safety → writing → humanizer。`ToolCallingPrompt` 含 ToolSearchRules / TodoRules / 并行与依赖规则 |
| **AgentEvent** | 全生命周期：`agent_start → turn_start → message_* → tool_* → turn_end → agent_end` |
| **IToolPlugin** | 插件契约：`Initialize(IServiceProvider) / GetTools()`（元数据由 csproj 驱动，构建时写入 plugin.json） |
| **IPluginStore** | 独立的只读数据接口（非 `INovelStore` 子集）；`PluginStore` sealed class 实现，`novel:read` 权限可获取 |
| **INovelStore** | 完全 CRUD 数据接口；仅 Builtin 或声明 `novel:write` 的 Verified 插件可获取。Session 管理已分离到 `FileSessionStore` |
| **FileNovelStore** | `INovelStore` 唯一实现，YAML/MD 文件读写 + 审计日志，`AtomicFile` 原子写入 |
| **FileSessionStore** | JSONL 会话持久化，分片存储（MANIFEST + 多世代 NNNNNN.jsonl），按项目隔离，支持分支树和上下文压缩，SessionSegmentManager 并发控制 |
| **ConfigStore** | `~/.mythology/config.yaml` 读写，`ConfigData` / `ProviderConfig` / `ApiKeyEntry` 类型安全 |
| **RetrievalKernel** | 共享检索入口：metadata 过滤 + vec0 dense + FTS5 词法 + RRF 融合 + `RankingProfile` 重排；零向量时降级纯词法 |
| **AgentMemoryStore** | Agent 记忆存储（`vectors.db` 三表）：精确混合评分召回（`w_sim·相似 + w_rec·新鲜 + w_imp·重要 + w_use·有用`），`mark_useful`/`mark_useless` 反馈学习 + spaced repetition；与检索域共享同源 embedding（避免向量通道分叉） |
| **AgentMemoryHook** | AfterToolCall 钩子：`tool_recall`/`tool_find_conflicts` 成功后对返回记忆自动 `MarkUsefulAsync`（autoWeight=0.3） |
| **EmbeddingService** | 本地 LlmTornado embedding（`Core.Vector`），实现 `IEmbeddingService`/`IBatchEmbeddingService`；复用聊天 Provider 或 `config.yaml` 独立 embedding 密钥；未配置时 `DummyEmbeddingService` 降级纯词法 |
| **RetrievalRestClient** | 云端 `/v1/retrieval` REST 客户端（`scrape`/`search`/`map`/`discover`，按云端额度结算），`web_scrape`/`web_search` 工具使用 |
| **IFictionHarnessClient** | 云端分析 Harness 契约（`HttpFictionHarnessClient` SSE 实现，`/v1/agent/runs` 协议）。接口位于 `Core.Cloud`（可被权限系统映射），Host DI 已注册（动态 API Key），23 个 NovelTools 云端分析器与 WritingTools 增强工具已接通 |
| **CloudAnalysisTool** | NovelTools 云端分析工具基类（`CloudAnalysisTool<TParams,TDetails>`）：23 个分析器（代入感/爽感/弧光/冲突/节奏/描写/对话/伏笔/结构/完整性等）的统一云端适配；云端不可用返回明确提示，不伪造分数 |
| **VectorSyncService** | 实时索引队列的启动/rebuild 补偿对账，补齐缺失或陈旧的检索索引 |
| **IToolHook** | beforeToolCall（支持阻止）/ afterToolCall 双钩子 |
| **CircuitBreaker** | 检测重复工具调用（阈值动态化：8K→3, 1M→5），TurnId 去重 |
| **ContextTokenCalibrator** | Token 口径校准：滚动最小二乘拟合（字符估算 → API 实测），状态栏 / compact 阈值用校准后口径，模型切换时重置 |
| **EditHunk** | 单次替换的结构化 diff 数据，工具产出 → TUI 按需生成 diff |
| **DiffComputer** | DiffPlex Myers diff + FoldContext GitHub 式上下文折叠 + CountChanges |
| **MarkdownConverter** | Markdown → ANSI markup 管线：表格盒绘 + CJK ZWSP + 标题层次 + 引用/Alert 样式 |
| **StabilityHarness** | 独立库提供 CompositeStabilityMonitor（GRG+Lyapunov 聚合）+ RGDecimator + VSA + GuardRail；`ModelContextProfile` 驱动所有动态阈值，Host 启动时自动注入 |
| **ExploreTool** | 多变体创作方向选择，用户选择后持久化 + 提升为正式消息 |
| **DecompressTool** | 恢复被压缩消息原文（`blockId` 定位，默认写文件 + 预览，`inline:true` 直接返回全文；不改变上下文折叠状态） |
| **CompressionBlocks** | `Agent` 内存压缩状态：`(BlockId, MessageIds, Summary)` 列表，支撑 decompress 与作品快照注入 |
| **IDiffDetails / INovelDetails** | 工具结果结构化详情接口，TUI 通过模式匹配提取 |
| **CancellationHttpHandler** | HTTP 流式请求中断，Abort 时强制关闭活跃连接 |
| **回合结束审计** | `HostBootstrapper` 配置 `Agent.ShouldStopAfterTurn`，检查草稿章节、孤立角色与未回收伏笔，并通过 SteeringQueue 引导后续处理 |
| **LoadNovelHook** | AfterToolCall 钩子，自动切换当前小说上下文 |
| **WritingFlowHook** | AfterToolCall 钩子，write_chapter/rewrite_selection 成功后注入 AI 味密度 / 章末钩子 / 信息差信号 |
| **ToolCategory** | 工具分类枚举：Generic/FileEdit/EntityCrud/ChapterManagement/EntityRead/SearchTool/BashTool |

---

## 插件管理命令

### mythology build — 构建插件

```bash
mythology build                        # 编译当前目录的插件
mythology build /path/to/plugin        # 编译指定路径
mythology build --install              # 编译 + 安装到 ~/.mythology/plugins
mythology build --install-local        # 编译 + 安装到 .mythology/plugins
```

### mythology plugin — 插件管理

```bash
mythology plugin list                  # 列出已安装插件
mythology plugin list --available      # 列出云端可用插件（云端 API 未实现）
mythology plugin install <name>        # 安装插件（云端 API 未实现，需手动复制 DLL + plugin.json）
mythology plugin remove <name>         # 移除插件
mythology plugin update <name>         # 更新插件（云端 API 未实现）
```

---

## 技术栈

| 组件 | 选型 |
|------|------|
| 运行时 | .NET 10 + C# 14 |
| 分发 | .NET Tool (`dotnet tool install`) |
| 插件 SDK | NuGet 包 `Mythology.Core` |
| 终端 UI | XenoAtom.Terminal.UI |
| 桌面 GUI | Avalonia 12 + ReactiveUI + SukiUI（`src/Desktop/`，已入 .slnx） |
| LLM 聚合 | LlmTornado 3.8.67（10 预设 Provider + 自定义） |
| Diff 计算 | DiffPlex（Myers diff 算法） |
| 云端客户端 | `ClientPlatform`（纯 HttpClient：检索 REST / Harness / 设备授权 / 本地回调 OAuth / DPAPI 凭据存储） |
| 创作资产存储 | YAML + Markdown 文件（Git 版本控制） |
| 会话存储 | JSONL 分片文件（`~/.mythology/sessions/`，MANIFEST + NNNNNN.jsonl） |
| 语义索引 | SQLite + `vec0` 扩展 + FTS5 simple（jieba）tokenizer |
| 中文分词 | jieba.NET 0.42.2（Core 检索 + ToolSearch 索引） |
| YAML 解析 | YamlDotNet |
| 日志 | Serilog（RollingFile，脱敏 RedactingTextFormatter） |
| 插件加载 | 运行时 DLL + AssemblyLoadContext 隔离 |
| 稳定性监控 | Lyapunov + VSA + RGDecimator |

---

## 项目结构

```
Mythology/
├── src/
│   ├── Core/                 # NuGet 包 (Mythology.Core)
│   │   ├── Agent/             # Agent 引擎（Agent / ConversationLoop / SteeringQueue / ToolUsageStats）
│   │   ├── Tools/             # 工具系统（AgentTool + ToolSearchEngine + ToolInjector + ToolSearchTool + ToolRegistry + DecompressTool）
│   │   ├── Prompts/           # 9 段 Prompt 骨架（PromptAssembly + RoleDefinition + ToolCalling + ...）
│   │   ├── Plugins/           # 插件系统（IToolPlugin / PluginLoader / PluginCapabilities / 三级信任）
│   │   ├── Stability/         # 稳定性监控接口（IStabilityMonitor / ModelContextProfile）
│   │   ├── Vector/            # 检索内核 + Agent 记忆（RetrievalKernel / IRetrievalStore / SqliteRetrievalStore / AgentMemoryStore / IEmbeddingService）
│   │   ├── Shared/            # 共享类型（AtomicFile / EditHunk / JsonHelper / TodoStore / UserQuestionStore）
│   │   ├── Infrastructure/    # 基础设施（CancellationHttpHandler / CircuitBreaker / IdGenerator）
│   │   └── Data/              # 数据层（INovelStore / FileNovelStore / FileSessionStore / ConfigStore / Migrations[001_SessionSegments + 002_CompactBlocks]）
│   │
│   ├── ClientPlatform/        # 云端客户端库（ClientPlatform）
│   │   ├── RetrievalRestClient.cs      # /v1/retrieval（REST，扣费）
│   │   ├── IFictionHarnessClient.cs     # /v1/agent/runs（HttpFictionHarnessClient，SSE 流式）
│   │   ├── OAuthLoopbackClient.cs       # 本地回调 OAuth（授权码 + PKCE + loopback）
│   │   ├── DeviceAuthorizationClient.cs # RFC 8628 设备授权
│   │   ├── CloudSessionStore.cs         # cloud-session.json 凭据存储
│   │   └── CloudEndpoints.cs / CloudTimings.cs  # 端点与 9 项时序常量单点收口
│   │
│   ├── Host/                 # .NET Tool (mythology)
│   │   ├── HostBootstrapper.cs # DI + 插件加载 + ToolSearch 初始化 + 云端客户端 + AgentMemory 注入 + 稳定性注入
│   │   ├── PluginCommands.cs  # build / plugin 命令实现
│   │   ├── Program.cs        # 命令入口 + 应用保存的 Provider
│   │   ├── LlmProvider.cs    # TornadoApi 工厂（多 provider + 自定义 BaseUrl）
│   │   ├── LoadNovelHook.cs  # AfterToolCall 钩子：切换小说上下文
│   │   └── WritingFlowHook.cs # （已迁至 CloudDiagnosis）写作流程信号钩子
│   │
│   ├── Tui/                  # XenoAtom.Terminal.UI 界面
│   │   ├── TuiApp.cs         # 主应用 + 布局 + 云端初始化
│   │   ├── TuiApp.Commands.cs # / 与 : 命令路由
│   │   ├── TuiApp.Compact.cs # 上下文压缩（手动/智能/自动/溢出）
│   │   ├── TuiApp.Completion.cs # #/@/ 补全
│   │   ├── Controls/         # ConversationPanel / SetUpPanel / CloudLoginDialog / SettingsDialog（连接器/嵌入/MCP/工具四标签页）/ MythPromptEditor / StatusBar / ...
│   │   └── Services/         # AgentEventAdapter / CloudConnectionService / ContextTokenCalibrator / ThemeService
│   │
│   ├── Desktop/              # Avalonia 12 + ReactiveUI + SukiUI GUI（已入 .slnx）
│   │   ├── Views/            # MainWindow / ConversationView / SetUpPanelView / TodoPanelView / SettingsView
│   │   ├── ViewModels/       # MainWindowViewModel / ConversationViewModel / SetUpPanelViewModel / TodoPanelViewModel / SettingsViewModel
│   │   ├── Services/         # DesktopAgentBridge / StreamEventHandler / ChatHistoryService / CloudAuthService / DesktopThemeService / NovelContextHook
│   │   └── Controls/         # WelcomeWindow / ProjectPickerWindow / SessionManagerWindow / CloudLoginWindow / AskUserWindow / EntityEditWindow / ...
│   │
│   ├── Plugins/              # 内置插件 (→ DLL)
│   │   ├── NovelTools/       # 67 工具（23 云端分析器 + 项目/章节/实体 CRUD + 批量 + Explore + 导出）
│   │   │   └── Tools/CloudAnalysisTool.cs  # 云端分析工具基类
│   │   ├── WritingTools/     # 10 工具（diagnose_writing + 9 enhance_*，诊断走云端）
│   │   ├── SemanticRetrievalTool/ # 8 语义检索工具
│   │   ├── WebSearchTools/   # 6 网络搜索工具
│   │   └── FileSystem/       # 6 工具（read/write/edit/todo_write/ask_user/get_pending_questions；BashTool 已实现未注册）
│   │
│   ├── CloudDiagnosis/      # 云端诊断组件库（Host + Desktop 双宿主共用）
│   │   ├── DiagnosisScheduler.cs    # 三层漏斗调度器（L1 本地预筛 → L2 事件触发 → 预算护栏）
│   │   ├── SignalBuilder.cs         # 云端诊断信号 → InjectQueue 翻译层
│   │   ├── HumanDecisionBridge.cs    # 人工裁决回路（repair-intent → UserQuestionStore → 回注）
│   │   ├── ContextSnapshotCache.cs  # 跨章状态缓存
│   │   ├── MonotonyWatcher.cs       # 单调检测与灵感供给
│   │   ├── NovelBudgetStore.cs        # 每小说预算池 + 银行（vectors.db）
│   │   └── WritingFlowHook.cs         # AfterToolCall 钩子：写入事件 → 调度漏斗
│   └── StabilityHarness/     # 独立 NuGet 库（PackageId=StabilityHarness，引用 Core）
│       ├── StabilityHarness.cs  # 工厂：CreateGuard/CreateDecimator/CreateHolographicEngine/Analyze
│       ├── Lyapunov/         # LyapunovMonitor + GrowthRatioGuard + StabilityConfig
│       ├── RGDecimator/      # RGDecimator + ScoredMessage + TextProcessor
│       ├── VSA/              # HolographicEngine + VsaCore + VSAConfig
│       └── Diagnostics/      # FailureReport + PatternClassifier + CostEstimator
│
├── novels/                   # 创作资产目录（Git 管理）
│
├── docs/
│   ├── architecture.md            # 架构设计（Agent/ToolSearch/云端协作/Session/Provider/命令）
│   ├── tool-search.md             # 按需工具检索注入方案（Tool Search）
│   ├── ui.md                      # TUI 布局规范
│   ├── plugin.md                  # 插件开发指南
│   ├── markdown-renderer.md       # Markdown 渲染器技术设计
│   ├── stability-harness.md       # 稳定性监控算法规范
│   ├── vec.md                     # 检索内核设计（dense + FTS5 + RRF + 分块）
│   ├── compact.md                 # 块式压缩引擎设计（可逆恢复）
│   ├── context-management.md      # 上下文管理与压缩策略
│   ├── desktop.md                 # 桌面客户端设计（Avalonia + ReactiveUI）
│   ├── entity-evolution-lifecycle.md # 实体演化 + 状态生命周期 + edit_entities 批量编辑
│   ├── plot-forecast-and-notes.md # 远期推演 + 作者随手记
│   ├── memory-ops-loop-plan.md    # 记忆运维闭环（计划）
│   ├── wechat.md                  # 微信通道
│
├── Directory.Build.props     # 统一版本号 (MythologyVersion)
```

> 云端（Cloud.Api / 私有引擎 / Retrieval / Web 前端）已迁移至独立私有仓库，本仓库仅保留本地客户端。

---

## TUI 布局

```
┌──────────────────────────────────────────────────────────────────┐
│  📖 Mythology │ ✨ AI 小说写作助手                                  │
├──────────────────────────────────────────┬───────────────────────┤
│                                          │ ┌──┬──┬──┬──┬──┬──┬──┬──┐│
│  DocumentFlow (7/10)                     │ │角│组│地│情│场│道│关│纲││
│  - 用户/助手/工具/思考/中断/错误气泡       │ ├──┴──┴──┴──┴──┴──┴──┴──┤│
│  - 系统气泡（实体变更通知，居中 dim）     │ │  设定面板 (3/10)     ││
│  - compact 进度（🔄→✅）                  │ └────────────────────┘│
├──────────────────────────────────────────┴───────────────────────┤
│  ❯ 聊天 | /命令 | :cloud | #角色 | @文件【ESC/Ctrl+C 中断回复】     │
├──────────────────────────────────────────────────────────────────┤
│  🔄 诗词 │ 📖 小说名 │ 🌐Cloud │ ⚡增强模式 │ 📊 tok │ model │ Ctrl+Q│
└──────────────────────────────────────────────────────────────────┘
```

右侧 8 Tab：🎭 角色 / 🏰 组织 / 📍 地点 / 📜 情节 / 🎬 场景 / 🎒 道具 / 🔗 关系 / 📋 大纲。下方是 Todo 面板。

状态栏：左侧 spinner + 状态文本（idle/诗词/compact 中）；右侧依次为小说名、Cloud 连接状态（`🌐`/`🌐❗`/`🌐$`/`🌐✕`；暗淡 = 未登录 / 停用）、增强模式（⚡全自动/⚡半自动/✋手动）、Token 统计（>90% 黄色、>100% 红色）、模型名 + 工具数 + Ctrl+Q。

---

## 运行时命令

### `/` 前缀命令

| 命令 | 说明 |
|------|------|
| `/commands` | 显示命令列表 |
| `/help` | 关于 Mythology |
| `/state` | Agent 状态 + 工具统计 + 写作统计（含 `/stats` 别名） |
| `/history` | 对话历史（弹窗） |
| `/clear` | 清除对话 |
| `/resume <id>` | 恢复上次会话（从 JSONL） |
| `/theme <dark\|light\|toggle\|id>` | 切换主题 |
| `/tools [插件:工具]` | 列出工具（支持二级补全：插件 ID → 工具名） |
| `/compact [keepRecent\|smart [N]]` | 压缩上下文（保留最近 N 条，默认 20；smart = RGDecimator 评分） |
| `/decompress [blockId]` | 恢复被压缩的消息原文（不传 blockId 时列出所有压缩块；写文件到 `~/.cache/mythology/decompress/`） |
| `/tree` | 对话分支树 |
| `/session` | 切换会话 |
| `/mode` | 循环增强模式（none → semi → full） |
| `/login [--device] [<url>]` | 云端登录（主命令；默认本地回调 OAuth，`--device` 强制设备流，`<url>` 覆盖云端地址） |
| `/logout` | 云端登出（清除凭据 + 停止心跳） |
| `/cloud` | 云端登录（`:cloud login` 的快捷方式） |
| `/variants` `/var` | 重新打开当前变体集（隐藏别名） |

### `:` 前缀命令（云端，CHANGE_07）

| 命令 | 说明 |
|------|------|
| `:cloud login` | 云端登录（默认本地回调 OAuth，失败回退设备流：QR 码 + 用户码 + SSE 轮询，手动 API Key 兜底） |
| `:cloud status` | 显示云端连接状态 + PublicId + 过期时间 |
| `:cloud logout` | 清除本地凭据 + 停止心跳 |

---

## 功能概览

### 工具集（116 个注册，默认动态注入约 84 个/轮）

| 插件 | 工具数 | 说明 |
|------|--------|------|
| NovelTools | 67 | 项目管理、章节与大纲、角色/情节线/组织/地点/道具/时间线/关系/场景 CRUD + 批量、23 个云端分析器、Explore、导出、统计、web_scrape/web_search |
| WritingTools | 10 | `diagnose_writing` + 9 个 `enhance_*`（诊断走云端，增强定位/生成/写回走本地） |
| SemanticRetrievalTool | 8 | search_memory、batch_search_memory、recall_character、recall_plot、find_consistency、recall_scene、recall_timeline、recall_related |
| WebSearchTools | 6 | web_search、batch_web_search、web_read、search_literary_criticism、lookup_term、recommend_similar_style |
| FileSystem | 6 | read、write、edit、todo_write、ask_user、get_pending_questions（`BashTool` 已实现但未注册） |
| Core | 20 | tool_search + decompress + 13 个 AgentMemory 工具（store/recall/reinforce/forget/mark_useful/mark_useless/find_conflicts/resolve_conflict/snapshot/as_of/diff/pack/consolidate）+ 4 个偏好工具（recall/save/list/delete_preference）+ tool_analyze_style；后 18 个全部 DeferLoading |

> 注：NovelTools 与 WebSearchTools 都注册 `web_search`，`ToolRegistry` 按名覆盖（后加载者胜出），注册表唯一名 116。

**NovelTools 云端分析器（23 个，CloudAnalysisTool 派生）**：evaluate_immersion_strength、evaluate_satisfaction_intensity、track_character_arc、evaluate_conflict_intensity、evaluate_opening_strength、analyze_pacing_quality、detect_description_quality、analyze_supporting_character、detect_avoidable_mistakes、check_naming_quality、evaluate_power_system、analyze_dialogue_quality、detect_content_padding、suggest_foreshadowing、evaluate_plot_structure、check_character_completeness、evaluate_excitement_curve、evaluate_reader_engagement、analyze_genius_character、detect_self_indulgence、detect_plot_holes、analyze_style、analyze_network_structure

**WritingTools 增强维度（9 个）**：immersion、satisfaction、pacing、description、dialogue、foreshadowing、humanizer、prose_quality、chapter_hook

### 核心能力

- **ReAct Agent Loop** — LlmTornado Conversation API，支持 reasoning/thinking + 运行时切换 Provider
- **按需工具注入（Tool Search）** — 116 个工具不全量注入：常驻 3 + tool_search 命中展开（LRU 20）+ 非延迟工具；默认动态模式每轮约 84 个（核心写作/分析工具常驻，仅检索/网络/第三方插件延迟）
- **上下文压缩（可逆恢复）** — 分片 append-only 存储 + `BlockId`/`CompressedMessageIds` 精确映射；`decompress` 工具/`/decompress` 命令恢复被压缩原文；摘要继承治理防链式膨胀；跨分片消息合并；`[mN]` ref 跨压缩稳定
- **工具结果截断 + 使用率分级提醒** — 超长工具结果头尾截断（阈值随模型窗口动态化，超大结果写内容寻址缓存文件），内存/落库/重建三路径字节一致；上下文使用率分级提醒（阈值从 pre-flight 动态推导，先于拦截触发）
- **Agent 记忆系统** — `AgentMemoryStore`（`vectors.db` 三表：memories/journal/snapshots），精确混合评分召回（相似+新鲜+重要+有用），`mark_useful`/`mark_useless` 反馈学习 + spaced repetition；13 记忆 + 4 偏好 + tool_analyze_style 共 18 个工具按需发现，`AgentMemoryHook` 自动标记使用
- **云端协作** — 检索 REST（`/v1/retrieval`）、分析 Harness（`/v1/agent/runs`）；本地回调 OAuth / 设备流登录、状态栏连接状态、心跳探活、401/403 事件驱动吊销；不可达自动降级
- **文件优先存储** — 创作资产 YAML/MD 文件，Git 版本控制，`AtomicFile` 原子写入
- **JSONL 会话** — 分片存储（MANIFEST + 多世代分片），追加写入，分支树 + 上下文压缩 + 自动迁移
- **断路器** — 高频/重复调用检测，TurnId 去重，batch 工具减少并行
- **混合检索** — `RetrievalKernel`：metadata 过滤 + dense vec0 + FTS5（中文 simple tokenizer）+ RRF 融合 + RankingProfile 重排；embedding 未配置/零向量时纯词法降级
- **10 预设 Provider** — OpenAI / Claude / Google / Groq / xAI / DeepSeek / Mistral / OpenRouter / Kimi / 阿里百炼 + 自定义 Provider
- **9 段 Prompt 骨架** — role / conversation / tooling / context / planning / errors / safety / writing / humanizer，ToolCallingPrompt 含 ToolSearchRules
- **Markdown 渲染** — 预处理管线：表格盒绘+对齐、CJK ZWSP、标题层次递减、引用/Alert 五色、列表 ▸ bullet
- **Diff 展示** ✅ — EditHunk → DiffComputer（DiffPlex Myers diff）→ FoldContext GitHub 式折叠 → Markup + ANSI color
- **Explore 变体选择** ✅ — LLM 生成多变体 → 用户选择 → 持久化 + 提升为正式消息
- **TodoStore + UserQuestionStore** — 会话级 Todo（BlockedBy 依赖）+ 用户交互问答
- **稳定性监控** — `CompositeStabilityMonitor`（GRG+Lyapunov 聚合）自动注入：`ModelContextProfile` 驱动所有阈值（8K~1M 自适应），thinking-aware ratio 检测，compact 后 baseline 重置，VSA 漂移时注入 system-reminder
- **GuardRail 语义检查** — `IGuardRail` 接口支持输入/输出检查，Pass/Warn/Block 三级，tripwire 中断循环
- **实体变更实时同步** — 文件写入触发 Debounce 200ms 刷新 + 高亮闪烁 + Agent Steering + 审计日志
- **Desert 主题** — 暖色调深色主题（沙色/赤陶/琥珀/鼠尾草），支持深色/浅色切换
- **HTTP 请求中断** — CancellationHttpHandler + stty isig SIGINT 路由
- **Token 口径校准** — ContextTokenCalibrator 用 API 实测拟合估算，状态栏与 compact 阈值显示校准后口径

---

## 开发模式

如果不想用 .NET Tool 安装，也可以直接从源码构建：

```bash
dotnet build src/Host -c Release
dotnet run --project src/Host

# 指定插件目录
NOVEL_PLUGINS_DIR=src/Plugins dotnet run --project src/Host

# Mock 模式（无 LLM）
dotnet run --project src/Host -- --mock
```

### 打包 Core NuGet

```bash
dotnet pack src/Core/Core.csproj -c Release
dotnet nuget push Mythology.Core.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
```

### 打包 .NET Tool

```bash
dotnet pack src/Host/Host.csproj -c Release
dotnet nuget push Mythology.1.0.0.nupkg --source https://api.nuget.org/v3/index.json
```

### 发布打包（三平台安装包）

`installer/` 下每个脚本可单独本地跑（产物落 `dist/` 根，带 `.sha256`）：

```bash
installer/build-tui.sh    --version 1.2.3    # Mythology nupkg + dotnet tool 安装冒烟
installer/build-linux.sh  --version 1.2.3    # .deb（需 Linux/WSL：dpkg-deb）
installer/build-macos.sh  --version 1.2.3    # .app + .dmg（需 macOS：sips/iconutil/hdiutil）
```

```powershell
installer\build-windows.ps1 -Version 1.2.3   # Inno Setup 安装器（需 ISCC.exe）
```

版本真源是 `vX.Y.Z` tag；推 tag 即触发 `.github/workflows/release.yml`（prepare → tui/linux/windows/macos → release）。三平台各自原生 runner 构建，不交叉编译。

---

## 项目状态

| 维度 | 完成度 |
|------|--------|
| 工具集 | 🟢 116 注册工具：NovelTools 67（含 23 云端分析器）+ WritingTools 10 + SemanticRetrievalTool 8 + WebSearchTools 6 + FileSystem 6 + Core 20（13 AgentMemory + 4 偏好 + tool_analyze_style + tool_search + decompress）；`web_search` 同名覆盖去重 |
| 按需工具注入 | 🟢 ToolInjector 常驻 3 + LRU 20 展开 + 延迟插件名单 + `Agent:ToolSearchMode` 灰度开关 |
| Core 引擎 | 🟢 ConversationLoop + 9 段 PromptAssembly + TodoStore + UserQuestionStore + CircuitBreaker + CancellationHttpHandler |
| 存储架构 | 🟢 三层存储（文件+JSONL+向量）+ FileSessionStore 分片存储 + 分支树 + 自动迁移 |
| TUI | 🟢 8 Tab Accordion + 实体编辑 + 主题切换 + #实体引用 + 分支树 + compact 反馈 |
| 云端协作 | 🟢 检索 REST + 分析 Harness + 本地回调 OAuth / 设备流登录（/login）+ 心跳 + 状态栏 + 401/403 事件驱动吊销；🟡 embedding 走本地 LlmTornado（云端 CloudEmbeddingClient 未实现） |
| Diff 展示 | 🟢 IDiffDetails → DiffComputer（DiffPlex Myers）+ FoldContext + ANSI color |
| Explore 变体 | 🟢 ExploreDetails → 变体选择对话框 → 持久化 + 消息提升 |
| NuGet 分发 | 🟢 Core NuGet + Host .NET Tool |
| 插件安全 | 🟢 三级 PluginTrustLevel + PluginPermission + RestrictedPluginServiceProvider（权限驱动） |
| 稳定性监控 | 🟢 Lyapunov + GrowthRatioGuard + RGDecimator + VSA |
| Token 校准 | 🟢 ContextTokenCalibrator（估算→实测最小二乘拟合） |
| 云端插件市场 | 🟡 `mythology plugin install/remove`（云端 API 未实现，命令已就绪） |
| 云端分析 Harness | 🟢 `IFictionHarnessClient`（Core.Cloud 接口）已接入 Host DI + `cloud:harness` 权限映射；23 个 NovelTools 分析器走云端，WritingTools 10 工具（1 诊断 + 9 增强）全部注册；`CloudDiagnosis` 组件库实现 DiagnosisScheduler + SignalBuilder + HumanDecisionBridge 诊断调度链路 |
| 测试 | 🟢 11 个测试项目：Core / Host / ClientPlatform / NovelTools / SemanticRetrievalTool / FileSystem / WebSearchTools / WritingTools / Tui / Desktop / StabilityHarness |

---

## 文档

| 文档 | 说明 |
|------|------|
| **[架构设计](./docs/architecture.md)** | 整体架构、Agent/ToolSearch、云端协作、三层存储、Session JSONL 分支树、Provider 配置、快捷键与命令、实体引用系统 |
| **[按需工具注入](./docs/tool-search.md)** | Tool Search 方案：jieba + BM25 + embedding + RRF、ToolInjector、延迟插件、灰度开关 |
| **[TUI 设计规范](./docs/ui.md)** | TUI 布局、状态原型、Desert 主题、对话流渲染、写作体验增强、实体字段差异分析 |
| **[插件开发指南](./docs/plugin.md)** | 创建插件、信任体系、权限系统、分析器、build/plugin 命令、攻击面推演 |
| **[Markdown 渲染器](./docs/markdown-renderer.md)** | 渲染管线、表格对齐、Diff 展示、CJK 强调修复、字符宽度处理 |
| **[StabilityHarness](./docs/stability-harness.md)** | Lyapunov 监控 + VSA + RGDecimator 算法规范与性能指标 |
| **[检索内核](./docs/vec.md)** | dense + FTS5 + RRF + 分块设计 |
| **[块式压缩引擎](./docs/compact.md)** | 模型驱动块式压缩、可逆恢复、`[mN]` ref 标签 |
| **[桌面客户端](./docs/desktop.md)** | Avalonia + ReactiveUI 双入口架构、ViewModel/View 映射、信号链路 |
| **[实体演化与状态生命周期](./docs/entity-evolution-lifecycle.md)** | 出场记录、状态变化、发布事务、变更集回滚、全实体时间线、设定变更、`edit_entities` 批量编辑 |
| **[远期推演与随手记](./docs/plot-forecast-and-notes.md)** | 大纲/细纲驱动的未来 N 章推演、作者笔记实体与双端 UI |

## 许可

MIT
