# COMPACT — 模型驱动块式压缩引擎

> 将 billion-context-pi（ACP，Active Context Pruning）的核心思想集成进 Mythology，
> 替换当前"一刀切、不可逆"的 compact 体系为**块式、可逆、模型驱动**的压缩引擎。
>
> **2026-08-30 补充：模型驱动压缩（决策 #1）经评审判定为不落地**，理由见正文 §1.4「评审结论：模型驱动压缩不落地」。原设计 §3–§9 保留供未来参考，落地需先推翻 §1.4 的价值论证。

## 落地状态（2026-08-30 对照源码）

| 设计项 | 状态 | 实际实现 |
|---|---|---|
| 可逆恢复（B2）：`BlockId` + `CompressedMessageIds` | ✅ 已实现 | `JsonlCompactEntry.BlockId`/`CompressedMessageIds`；`FileSessionStore.ReadCompactedMessagesAsync` / `ListCompactBlocksAsync`；`DecompressTool`（`decompress`）+ `/decompress` 命令；`002_CompactBlocks` 迁移补全历史映射 |
| `[mN]` ref 标签（B3） | ✅ 已实现 | `ConversationLoop.InjectRefTags`（运行时注入、发送后还原、不污染 JSONL）；`Agent.EntryIdProvider` 委托提供稳定身份（`LlmChatMessage.NativeObject` 存 EntryId），ref 跨 compact/恢复稳定 |
| nudge 分级提醒 | ✅ 已实现（简化版） | 内联在 `ConversationLoop`（无独立类，符合决策 #4）；阈值从 `ModelContextProfile.NudgeInfo/Warn/CriticalPercent` 动态推导（先于 pre-flight 触发），非设计稿的固定 50/70/85% |
| `CompressionBlock` record + `CompressionState` | 🟡 最小版 | `Agent.CompressionBlocks` 为 `List<(BlockId, MessageIds, Summary)>` 元组列表（线性扫描），非设计稿的 `CompressionBlock`/`CompressionState` 类型；无 `Tier`/`Topic`/`DirectBlockIds` 等字段 |
| `CompressionEngine`（Compress/Decompress 纯操作 + CJK token 估算） | ❌ 未实现 | 压缩逻辑仍在 `FileSessionStore.CompactSession` + `TuiApp.Compact.cs`；CJK token 估算未独立成 `CompressionEngine.EstimateMessageTokens` |
| `CompressTool`（模型驱动 `compress` 工具） | ❌ 未实现 | 注册表无 `compress` 工具；压缩触发仍为阈值/手动/溢出兜底，模型不能自主压缩 |
| `CompressionPrompt` + `("compression", ...)` 骨架段 | ❌ 未实现 | `PromptAssembly` 仍为 9 段，无 compression 段 |
| 迁移 `002_CompactBlocks` | ✅ 已实现 | `Core/Data/Migrations/002_CompactBlocks.cs`（推导覆盖区间 + 迁移后二次扫描验证） |

**结论**：设计稿的「可逆 + ref + nudge」三件套已落地并稳定运行；「模型驱动压缩」（决策 #1 主通道）尚未实现——当前压缩仍是阈值/规则驱动，`/compact` 与 `ContextOverflowException` 是唯一入口。下文 §3–§9 保留为未落地部分的原始设计，落地时需按本表校准。

---

## 目录

1. [背景与目标](#1-背景与目标)
2. [核心思想提炼（借鉴什么 / 不借鉴什么）](#2-核心思想提炼)
3. [架构定位与关键设计决策](#3-架构定位与关键设计决策)
4. [文件级变更清单](#4-文件级变更清单)
5. [详细设计](#5-详细设计)
6. [数据流](#6-数据流)
7. [迁移设计](#7-迁移设计)
8. [实现顺序与验证标准](#8-实现顺序与验证标准)
9. [Phase 1 边界（刻意不做）](#9-phase-1-边界刻意不做)
10. [交付前检查清单](#10-交付前检查清单)

---

## 1. 背景与目标

### 1.1 现状问题

当前 Mythology 的 compact 体系（`TuiApp.Compact.cs` + `FileSessionStore.CompactSession`）是**一次性切割**：

- 由 LyapunovMonitor / GrowthRatioGuard 等阈值触发，或用户 `/compact` 手动触发
- 保留最近 N 条消息，其余全部压缩为**单条摘要**
- 摘要不可分解、不可搜索、不可恢复——被压缩的消息永久丢失在 JSONL 深处
- 摘要内容由一次性规则生成，质量依赖触发时机

### 1.2 目标

参照 billion-context-pi（ACP）的成熟方案，建立：

| 维度 | 现状 | 目标 |
|------|------|------|
| 触发方式 | 阈值/规则驱动 | **模型驱动**（模型自主调用 `compress` 工具）+ 阈值兜底 |
| 压缩单位 | 整段切割为单摘要 | **块（Block）**：一段消息 + 模型写的摘要 |
| 可逆性 | 不可逆 | **可逆**：`decompress` 从 JSONL 恢复原始内容 |
| 可见性 | 摘要不可定位 | **ref 标签**：每条消息带 `[mNNNNN]` 引用，模型可精确定位 |
| 持久化 | 单 summary 字段 | **blocks 数组**（多块，含消息 ID 映射） |
| 数据格式 | v1 旧格式 | 通过迁移脚本升级（不做向后兼容） |

### 1.3 成功标准

- 模型能通过 `compress` / `decompress` 工具自主管理上下文（主通道）
- LyapunovMonitor / GrowthRatioGuard / ContextOverflowException 仍作为安全网（兜底通道）
- 压缩后可通过 `decompress` 恢复被压缩消息的完整原文
- 旧会话数据经迁移脚本无损升级（旧单摘要 → 单 block）

### 1.4 评审结论：模型驱动压缩不落地（2026-08-30）

经对照现网压缩链路评审，**不实现 `CompressTool` / `CompressionPrompt` / `CompressionEngine` 的模型驱动部分**。此前对 `docs/compact.md` 的模型驱动压缩尝试已全部撤销（git 还原，工作区无残留）。理由：

| 维度 | 论证 |
|---|---|
| 价值稀缺 | 现网已有完整触发链：pre-flight 动态阈值（85–99%）+ nudge 分级提醒 + 用户 `/compact` + 溢出三重兜底。模型自主压缩的唯一增量场景是"阈值前主动清理"，但该场景下模型无法判断"用户是否还会引用这段上下文"（小说创作中用户随时可能回头改设定），切割点不可控 |
| 摘要质量 | 安全网路径的摘要由 `TryGenerateAiSummaryAsync`（专门摘要模型调用 + 规则 fallback）生成；模型自写摘要质量不稳定，且压缩不可逆（decompress 不回到上下文），错误成本高 |
| 持续成本 | `CompressionPrompt` 段是每轮 LLM 调用都进上下文的固定开销（约 +1150 字符），收益只在不可控的模型主动压缩时兑现；另需新增工具注册/委托装配/ref 解析 API/`PersistCompactCoreAsync` 间接层与测试维护（AGENTS.md §2：不为低价值场景加复杂） |
| 替代已存在 | nudge 提醒用户 `/compact`（用户决策，最安全）+ 自动安全网。nudge 文案"建议用户执行 /compact"是正确行为，不改 |

**维持现状**：`/compact` 与 `ContextOverflowException` 是唯二压缩入口；`decompress` 可逆恢复已覆盖"压缩后找回原文"场景。

---

## 2. 核心思想提炼

### 2.1 从 acp-kernel 借鉴什么

acp-kernel 是 billion-context-pi 的底层引擎（~4500 行 TS）。经逐模块阅读，**只取以下核心思想**，不做代码移植：

| # | 思想 | acp-kernel 出处 | 本方案落地 |
|---|------|----------------|-----------|
| 1 | **Block 是压缩的原子单位** | `types.ts` CompressionBlock | `CompressionBlock` record（字段简化，见 5.1） |
| 2 | **压缩是模型驱动的** | `compress.ts` applyCompression | `CompressTool`（AgentTool） |
| 3 | **压缩可逆** | `decompress.ts` collectBlockContent | `DecompressTool` + JSONL 原文读取 |
| 4 | **ref 标签让模型精确定位消息** | `render-refs.ts` / `refs.ts` | `[mNNNNN X.XK 类型]` 方括号格式 |
| 5 | **被覆盖消息从可见列表移除，摘要插入锚点位置** | `prune.ts` | `BuildMessagesFromEntries` 按 block 渲染 |
| 6 | **nudge 是渐进式效率提醒** | `compress.ts` decideNudge | 简化为阈值 nudge（见 5.5） |
| 7 | **摘要提示词规则（KEEP VERBATIM / DROP / PRIORITY）** | `compression-rules.ts` | 中文版 `CompressionPrompt` |

### 2.2 不借鉴什么（及原因）

以下 acp-kernel 能力**刻意不移植**——它们是 Pi 宿主适配，与 Mythology 架构冲突或已有等价物：

| acp-kernel 能力 | 原因 |
|-----------------|------|
| Pipeline 8 节点链 | Pi 的 `context` 事件模型 ≠ Mythology 的 `ConversationLoop` |
| messageRefs 双向映射（byRaw/byRef） | `ChatMessage.EntryId` 唯一，无需间接层 |
| protected 工具过滤 + hide-consumed-compress-calls | compress 调用写入 JSONL compact entry，不在可见消息里 |
| emergency-truncate（工具输出截断） | LyapunovMonitor + GrowthRatioGuard + ContextOverflowException 三重安全网已覆盖 |
| search / BM25 / fuzzy / semantic | Phase 1 不做；后续可桥接既有 RetrievalKernel |
| rebuild / fork-recovery | JSONL 是持久化 source of truth，无需从历史 compress 调用重建 |
| block 的 survivedCount / generation / promotion | 多级蒸馏的辅助机制，Phase 1 无 T2/T3 自动蒸馏 |
| `<acp>` XML 标签 | 改用 `[mNNNNN]` 方括号格式（TUI MarkdownConverter 可过滤） |

---

## 3. 架构定位与关键设计决策

### 3.1 架构定位

```
Mythology.Core
├── Agent/       ConversationLoop（ref 注入 + nudge 内联）
├── Stability/   CompressionBlock.cs + CompressionEngine.cs   ← 新增
├── Tools/       CompressionTools.cs（compress/decompress）  ← 新增
├── Prompts/     CompressionPrompt.cs                        ← 新增
├── Data/        JsonlDto.cs / FileSessionStore.cs（blocks 持久化）
│                Migrations/002_CompactBlocks.cs             ← 新增
└── Agent/       Agent（持有 CompressionState，ReplaceMessages 是替换入口）

Tui             TuiApp.Compact.cs 走 CompressionEngine（/compact 兜底入口）
```

约束：压缩引擎活在 `Core.Stability`（ConversationLoop 在 Core 内直接用）；不依赖 StabilityHarness 的 RGDecimator/VSA（Core 只定义接口）。

### 3.2 关键设计决策

| # | 决策 | 权衡说明 |
|---|------|---------|
| 1 | **模型驱动为主、阈值兜底** | 模型主动压缩是主通道；LyapunovMonitor 等仍作为安全网自动 compact。两者都走 `CompressionEngine`，只有触发源不同 |
| 2 | **RGDecimator 从"决策者"降级为"顾问"** | 其评分仅用于 `/compact` 命令的切割点辅助（`CompactSessionSmartAsync` 保留），不再驱动自动切割 |
| 3 | **不做 `ICompressionEngine` 接口** | 单一实现，无需抽象层（AGENTS.md §3） |
| 4 | **不做独立 nudge 类 / ref 注入类** | 20 行内联在 ConversationLoop，不建类 |
| 5 | **CompressTool 通过构造注入访问会话** | 注入 `Agent` 引用 + 持久化委托，避免工具直接依赖 FileSessionStore，也不建接口 |
| 6 | **decompress 默认写文件** | `~/.cache/mythology/decompress/`，避免恢复大内容撑爆上下文；`inline:true` 才返回工具结果 |
| 7 | **ref 标签运行时注入、不持久化** | 发送给 LLM 的消息副本带标签；JSONL 持久化保持原始内容，避免标签污染历史 |
| 8 | **不做向后兼容，用迁移脚本升级** | 旧数据（单 summary）通过 `002_CompactBlocks` 迁移为单 block |

---

## 4. 文件级变更清单

### 4.1 新增 5 个文件

| # | 文件 | 行数 | 职责 |
|---|------|------|------|
| 1 | `src/Core/Stability/CompressionBlock.cs` | ~55 | `CompressionBlock` record + `CompressionState` |
| 2 | `src/Core/Stability/CompressionEngine.cs` | ~240 | `Compress` / `Decompress` 两个操作 + CJK token 估算 |
| 3 | `src/Core/Tools/CompressionTools.cs` | ~170 | `CompressTool` + `DecompressTool`（AgentTool） |
| 4 | `src/Core/Prompts/CompressionPrompt.cs` | ~120 | 中文压缩提示词（对标 compression-rules.ts） |
| 5 | `src/Core/Data/Migrations/002_CompactBlocks.cs` | ~120 | 旧 compact entry → blocks 格式迁移 |

### 4.2 修改 7 个文件

| # | 文件 | 改动量 | 改什么 |
|---|------|--------|--------|
| 1 | `src/Core/Data/JsonlDto.cs` | ~20 行 | `JsonlCompactEntry` 新增 `Blocks`；删 `Summary`/`Reason`/`WillRetry`/`EstimatedTokensAfter`；新增 `CompactBlockDto` |
| 2 | `src/Core/Data/FileSessionStore.cs` | ~40 行 | `CompactSession` 签名改 blocks；`BuildMessagesFromEntries` 按 block 渲染；`FromCompactDto` 解析 blocks |
| 3 | `src/Core/Data/Models.cs` | ~5 行 | `ChatMessage` 新增 `CompressionBlockId` |
| 4 | `src/Core/Agent/ConversationLoop.cs` | ~50 行 | 每轮：ref 标签注入 + nudge 检查 |
| 5 | `src/Core/Agent/Agent.cs` | ~10 行 | 持有 `CompressionState` |
| 6 | `src/Core/Prompts/PromptAssembly.cs` | ~3 行 | 新增 `("compression", ...)` 段 |
| 7 | `src/Tui/TuiApp.Compact.cs` | 替换 ~160 行 | 旧逻辑全部走 CompressionEngine |
| 8 | `src/Tui/TuiApp.Session.cs` | ~10 行 | 会话恢复时重建 CompressionState |

---

## 5. 详细设计

### 5.1 `CompressionBlock.cs` — 数据模型

对标 acp-kernel `CompressionBlock`，**砍掉 Pi 宿主字段**（runId / survivedCount / generation / durationMs / compressCallId）。

```csharp
namespace Core.Stability;

/// <summary>压缩 block：一段被压缩消息的摘要替代，可逆可定位。</summary>
public sealed record CompressionBlock
{
    public string BlockId { get; init; } = "";              // "b1", "b2"
    public int Tier { get; init; } = 1;                     // 1=原始→摘要（Phase 1 只用 1）
    public bool Active { get; init; } = true;
    public string Summary { get; init; } = "";
    public string? Topic { get; init; }                     // 3-5 词主题
    public List<string> DirectMessageIds { get; init; } = [];     // 直接覆盖的消息 entryId
    public List<string> EffectiveMessageIds { get; init; } = [];  // 有效覆盖（含嵌套 block）
    public List<string> DirectBlockIds { get; init; } = [];       // 被消费的子 block（T2+ 蒸馏用）
    public long CompressedTokens { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>压缩引擎状态。</summary>
public sealed class CompressionState
{
    public List<CompressionBlock> Blocks { get; init; } = [];
    public int NextBlockId { get; set; } = 1;

    /// <summary>entryId → blockId 索引（压缩时 O(1) 查重）。</summary>
    public Dictionary<string, string> MessageToBlock { get; init; } = [];

    public List<CompressionBlock> ActiveBlocks => Blocks.Where(static b => b.Active).ToList();
    public CompressionBlock? BlockById(string blockId) =>
        Blocks.FirstOrDefault(static b => b.BlockId == blockId);

    public string AllocateBlockId() => $"b{NextBlockId++}";

    public void RebuildIndex()
    {
        MessageToBlock.Clear();
        foreach (var block in Blocks)
            if (block.Active)
                foreach (var id in block.EffectiveMessageIds)
                    MessageToBlock[id] = block.BlockId;
    }
}
```

不引入 `CompressionStats`（tokensCompressed 等）——压缩统计已有 `ModelContextProfile`，不重复。

### 5.2 `CompressionEngine.cs` — 两个操作

对标 acp-kernel `applySingleRange` + `collectBlockContent`，去掉 pipeline 包装与 protected 过滤。

```csharp
namespace Core.Stability;

public sealed record DecompressResult(string Text, string? FilePath, int MessageCount, long CharCount);

/// <summary>压缩引擎：Compress / Decompress 两个纯操作 + CJK token 估算。</summary>
public sealed class CompressionEngine
{
    /// <summary>
    /// 压缩 [startIndex..endIndex] 为摘要 block。纯函数：不修改输入，
    /// 返回（新消息列表, 新状态, 新 block）。
    /// 调用方负责：边界对齐（user 消息起点）、持久化 compact entry、稳定性监控器解冻。
    /// </summary>
    public (List<ChatMessage> Messages, CompressionState State, CompressionBlock Block) Compress(
        IReadOnlyList<ChatMessage> messages,
        CompressionState state,
        int startIndex,
        int endIndex,
        string summary,
        string? topic)
    {
        // 1. 参数校验：范围合法、摘要非空
        // 2. 收集范围内消息 entryId（跳过已被其他 block 覆盖的 → 不在可见列表里重复计）
        // 3. 检测范围内嵌套活跃 block（Phase 1 罕见，仅当模型以 bN 为边界调用时）
        //    合并其 EffectiveMessageIds，将其停用
        // 4. 创建 block：blockId / summary / topic / direct+effective ids / CompressedTokens
        // 5. 新状态 = 旧状态 + 新 block（停用被消费子 block）+ RebuildIndex()
        // 6. 新消息列表 = [0..startIndex) + 摘要消息 + (endIndex+1..)
        //    摘要消息：Content = "[上下文摘要 b1] (主题)\n摘要"，CompressionBlockId = blockId
    }

    /// <summary>
    /// 解压 block：恢复原文。block 保持压缩状态（摘要仍在上下文中）。
    /// readOriginalMessages 回调从 JSONL 读取原始消息（Core 不依赖具体存储）。
    /// inline=false 时写 ~/.cache/mythology/decompress/{blockId}-{ts}.txt，返回路径+预览。
    /// </summary>
    public DecompressResult Decompress(
        string blockId,
        CompressionState state,
        Func<List<string>, List<(string Role, string Content)>> readOriginalMessages,
        bool inline = false,
        string? toFile = null)
    {
        // 1. 找 block；不存在或已停用 → 抛明确异常
        // 2. readOriginalMessages(block.EffectiveMessageIds) 读原文
        // 3. inline → 直接返回文本；否则写文件 + 600 字预览
    }

    /// <summary>
    /// CJK 感知的 token 估算：CJK 字符 1:1，其余按 /4。
    /// 对标 acp-kernel defaultCountTokens——chars/4 会严重低估中文。
    /// 热路径每次 LLM 调用跑一次，O(n)，无分配。
    /// </summary>
    public static long EstimateMessageTokens(ChatMessage msg)
    {
        var text = msg.Content;
        var cjk = 0L;
        foreach (var c in text)
            if (c is >= '\u4e00' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af')
                cjk++;
        return cjk + (long)Math.Ceiling((text.Length - cjk) / 4.0);
    }
}
```

要点：
- 单次遍历收集覆盖消息，不重复枚举（AGENTS.md §5.3）
- `FindMessageByEntryId` 用线性扫描即可（消息量小），不建索引
- 摘要消息的 `Order` 取 `messages[startIndex].Order`，保持顺序不变量

### 5.3 `CompressionTools.cs` — 两个 AgentTool

```csharp
namespace Core.Tools;

public sealed class CompressParams
{
    public string? Topic { get; set; }
    public List<CompressRangeSpec> Content { get; set; } = [];
}

public sealed class CompressRangeSpec
{
    public string StartId { get; set; } = "";   // "m00005" 或 "b3"
    public string EndId { get; set; } = "";     // "m00220" 或 "b15"
    public string Summary { get; set; } = "";
    public string? Topic { get; set; }
}

public sealed class DecompressParams
{
    public string BlockId { get; set; } = "";   // "b5"
    public bool Inline { get; set; }
    public string? ToFile { get; set; }
}
```

**集成方式（关键决策 #5）**：工具不直接碰存储，构造时注入两个依赖：

```csharp
public sealed class CompressTool : AgentTool<CompressParams, string>
{
    private readonly Func<CompressionContext> _context; // 由 TuiApp 装配时提供

    public CompressTool(Func<CompressionContext> context) => _context = context;

    public override string Name => "compress";
    public override string Label => "压缩";
    public override string Description =>
        "将已完成的消息范围替换为详细摘要（模型写的 summary 是唯一记录，必须自包含）。" +
        "单范围：compress({ content: [{ startId: \"m00150\", endId: \"m00220\", summary: \"...\" }] })。" +
        "压缩后的内容可用 decompress 恢复。";
    public override ToolCategory Category => ToolCategory.Generic;

    public override async Task<AgentToolResult<string>> ExecuteAsync(...)
    {
        // 1. 解析 startId/endId（mNNNNN → 消息索引；bN → 该 block 锚点索引）
        // 2. 调 CompressionEngine.Compress
        // 3. agent.ReplaceMessages(newMessages) 同步 ConversationLoop
        // 4. 调持久化委托写 compact entry（blocks）
        // 5. 返回 "[上下文摘要 bN] tokens: X→Y"
    }
}

public sealed class DecompressTool : AgentTool<DecompressParams, string>
{
    // 同构：调 CompressionEngine.Decompress，readOriginalMessages 回调由装配者提供
}
```

`CompressionContext` 是**具体装配记录**（不是接口）：

```csharp
/// <summary>压缩工具的会话访问上下文，由 TuiApp 装配时构建。</summary>
public sealed record CompressionContext(
    Func<CompressionState> GetState,                    // 读当前 CompressionState
    Action<CompressionState> SetState,                  // 更新 CompressionState
    Func<List<ChatMessage>> GetMessages,                // 读当前消息列表
    Action<IReadOnlyList<ChatMessage>> ReplaceMessages, // 替换消息（Agent.ReplaceMessages）
    Func<List<CompactBlockDto>, string, int, Task<string?>> PersistCompact // 写 JSONL
);
```

> 说明：`CompressionContext` 是数据传递记录（record），不是行为抽象——它把 Agent + FileSessionStore 的访问折叠成一个注入点，避免工具构造函数 5 个参数。符合 AGENTS.md"不为单实现建接口"。

工具注册：TuiApp 启动时以 `"core"` 插件组注册到 ToolRegistry（现有 `RegisterRange`）。

### 5.4 `CompressionPrompt.cs` — 中文压缩提示词

对标 acp-kernel `compression-rules.ts`（经数月生产调优的规则文本），译为中文并适配小说创作场景。**规则措辞是承重墙，翻译保留全部结构**（KEEP VERBATIM / DROP / PRIORITY 三段式）。

```csharp
namespace Core.Prompts;

public static class CompressionPrompt
{
    public const string Full =
        Tags + "\n\n" + Philosophy + "\n\n" + WhenToCompress + "\n\n"
        + WhenNotToCompress + "\n\n" + HowToCompress + "\n\n" + DecompressSection;

    private const string Tags = """
## 上下文标签

每条用户和工具消息前有 [mNNNNN X.XK 类型] 标签（ref、token 大小、内容类型）。
助手消息无标签——从相邻标签推断 ref。这些是系统元数据，回复中不要重复标签本身；
compress 调用里只写 ref ID（如 m00005），不写方括号。
""";

    private const string Philosophy = """
## 压缩哲学

- 所有压缩为主任务服务，但需节制。
- 上下文容量珍贵。通过压缩已消耗的输出来节省上下文，而不是避免使用工具。
- 按需压缩，不按百分比压缩。
- 从摘要工作，不原始输出工作。唯一例外：当前步骤正在使用的内容、或无法重建的关键内容。
""";

    private const string WhenToCompress = """
## 何时压缩

- 大量工具输出（章节内容、角色列表、设定查询）且已提取关键信息
- 探索性查询未产生有价值的结果
- 同一设定重复读取，决策已记录
- 讨论已达成结论，决策已写入设定文件或代码
- 多步骤任务的中间步骤，最终结果已记录
- 一个创作阶段已结束（角色塑造完成、情节分析完成、设定探索结束）
""";

    private const string WhenNotToCompress = """
## 何时不压缩

- 当前步骤正在阅读或推理的内容
- 重要的用户消息——保留完整意图、约束和验收标准
- 当前创作所需的小说设定上下文
""";

    private const string HowToCompress = """
## 如何压缩

调用 compress 时，你写的 summary 是范围内对话的唯一记录，必须自包含且完整。

### 必须逐字保留

- 完整文件路径与行号（characters/李明.yaml、chapters/ch03.md:45-52）
- 关键签名与承重代码行（编码了逻辑的那一行，不只是函数名）
- 错误消息与堆栈（原文——后续需要精确 grep）
- 决策及其理由（"选择 X 而非 Y，因为 Z"——"因为"是承重墙）
- 发现的约束、精确值（版本、配置键、阈值）
- 用户意图——短消息逐字引用；长消息保留范围/约束/优先级/验收标准
- 用户的总体目标及其演变（含转向）
- 关键锚点消息的 ref（m00420）——让后续 decompress 能跳回原文

### 应当丢弃

- 冗长日志——提取错误行或结果即可
- 重复文件读取、已消耗的搜索结果——提取事实后丢弃
- 死胡同探索——保留教训一行："尝试了 X，失败因为 Y"
- 反复讨论和自我修正——捕获最终立场后丢弃过程
- 大型工具输出——为每个丢弃项加一行内容描述（覆盖什么，不是在哪）

### 优先级（summary 必须紧凑时）

1. 用户总体目标、演变、意图、硬约束（丢失这些等于改变任务）
2. 决策及其理由
3. 精确技术产物：路径、签名、错误、值
4. 结论和关键发现
5. 教训：什么失败以及为什么

写密集、可扫描的要点——不是叙述散文。跨关注点时在短主题标题下分组。
""";

    private const string DecompressSection = """
## 解压

decompress 恢复之前压缩的内容，默认写入文件（inline:true 在工具结果中返回）。
block 保持折叠状态，上下文不受影响。需要压缩中丢失的精确细节时使用 decompress。
""";
}
```

`PromptAssembly.cs` 新增段（插在 `context` 与 `planning` 之间）：

```csharp
("compression", CompressionPrompt.Full),
```

### 5.5 `ConversationLoop.cs` — ref 注入 + nudge

**5.5.1 Ref 标签注入（运行时，不持久化）**

在每次把消息送入 LLM 前，构造副本注入标签。持久化（`SaveSessionMessages`）仍写原始内容。

```csharp
private int _nextMsgRef = 1;

/// <summary>
/// 为发送给 LLM 的消息副本注入 [mNNNNN X.XK 类型] 标签。
/// 助手消息跳过（模型不需要 ref 自己的回复）；压缩摘要消息跳过。
/// </summary>
private void InjectRefTags(List<ChatMessage> messages)
{
    foreach (var msg in messages)
    {
        if (msg.Role == MessageRole.Assistant || msg.CompressionBlockId is not null) continue;
        if (string.IsNullOrEmpty(msg.Content) || msg.Content.StartsWith("[m")) continue;

        var typeLabel = msg.Role switch
        {
            MessageRole.User => "user",
            MessageRole.Tool => msg.ToolName ?? "tool",
            _ => "text"
        };
        var tokens = CompressionEngine.EstimateMessageTokens(msg);
        msg.Content = $"[m{_nextMsgRef++:D5} {FormatK(tokens)} {typeLabel}] {msg.Content}";
    }
}

private static string FormatK(long n) => n >= 1000 ? $"{n / 1000.0:F1}K" : n.ToString();
```

**5.5.2 Nudge 检查（内联，不建类）**

对标 acp-kernel `decideNudge`，但**不做 growth-aware / per-tier cadence**——这些由 LyapunovMonitor 已覆盖的增量追踪承担。nudge 只读 `stabilityMonitor.TotalTokens / GetTokenBudget()`：

```csharp
/// <summary>每轮发送前调用；返回要注入的 nudge 消息（无则 null）。</summary>
private ChatMessage? CheckCompressionNudge()
{
    if (stabilityMonitor is null || stabilityMonitor.TotalTokens <= 0) return null;
    var budget = stabilityMonitor.GetTokenBudget();
    if (budget <= 0) return null;
    var usage = (double)stabilityMonitor.TotalTokens / budget;

    return usage switch
    {
        >= 0.85 => new ChatMessage(MessageRole.User,
            "⚠️ 上下文使用率超过 85%，请立即用 compress 压缩已完成的工作。\n" +
            "示例：compress({ content: [{ startId: \"m00150\", endId: \"m00220\", summary: \"...\" }] })"),
        >= 0.70 => new ChatMessage(MessageRole.User,
            "📋 上下文使用率超过 70%，建议用 compress 压缩已消耗的对话范围。"),
        >= 0.50 => new ChatMessage(MessageRole.User,
            "💡 上下文使用率超过 50%，可考虑压缩已完成的探索。"),
        _ => null
    };
}
```

nudge 消息在发送前追加到消息副本尾部，**不持久化**（与 ref 标签同策略）。

### 5.6 `JsonlDto.cs` — 持久化格式

```csharp
public sealed class JsonlCompactEntry
{
    public JsonlEntryType Type { get; init; } = JsonlEntryType.Compact;
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Timestamp { get; init; } = "";
    public string? FirstKeptEntryId { get; init; }
    public int? MessagesBefore { get; init; }
    public int Order { get; init; }

    /// <summary>压缩 block 列表（替代旧单 Summary 字段）。</summary>
    public List<CompactBlockDto> Blocks { get; init; } = [];
}

/// <summary>compact entry 中的单个压缩 block。</summary>
public sealed class CompactBlockDto
{
    public string BlockId { get; init; } = "";
    public int Tier { get; init; } = 1;
    public string Summary { get; init; } = "";
    public string? Topic { get; init; }
    public List<string> DirectMessageIds { get; init; } = [];
    public List<string> EffectiveMessageIds { get; init; } = [];
    public List<string> DirectBlockIds { get; init; } = [];
    public long CompressedTokens { get; init; }
}
```

**删除的字段**：`Summary` / `Reason` / `WillRetry` / `EstimatedTokensAfter`（迁移脚本处理旧数据，新代码不再读写）。

新 JSONL compact 行示例：

```json
{"type":"compact","id":"9f3a...","timestamp":"2026-08-07T...","firstKeptEntryId":"7c21...","messagesBefore":87,"order":120,
 "blocks":[{"blockId":"b1","tier":1,"summary":"探索了角色李明的背景设定…","topic":"角色塑造",
   "directMessageIds":["e1a2...","f5d6..."],"effectiveMessageIds":["e1a2...","f5d6..."],
   "directBlockIds":[],"compressedTokens":5200}]}
```

### 5.7 `FileSessionStore.cs` — 读写

**5.7.1 `CompactSession` 签名变更**

```csharp
// 旧：CompactSession(sessionId, novelId, string summary, firstKeptEntryId, messagesBefore, parentEntryId, order, reason, willRetry, estimatedTokensAfter)
// 新：
public async Task<string?> CompactSession(
    string sessionId,
    string novelId,
    List<CompactBlockDto> blocks,
    string firstKeptEntryId,
    int messagesBefore,
    string? parentEntryId = null,
    int order = 0)
```

内部：`compactEntry = new JsonlCompactEntry { ..., Blocks = blocks }`，其余分片写入逻辑不变。

**5.7.2 `ParsedEntry` 新增字段**

```csharp
/// <summary>compact 条目：压缩 block 列表（新格式）。</summary>
public List<CompactBlockDto>? CompactBlocks { get; init; }
```

**5.7.3 `FromCompactDto` 变更**

```csharp
CompactBlocks = dto.Blocks.Count > 0 ? dto.Blocks : null,
CompactSummary = dto.Blocks.Count > 0
    ? string.Join("\n\n", dto.Blocks.Select(static b => b.Summary))
    : dto.Summary  // 迁移前旧数据兜底：单 summary 直接透传
```

**5.7.4 `BuildMessagesFromEntries` 变更（Context 模式）**

```csharp
if (entry.Type == JsonlEntryType.Compact)
{
    // ... 现有活跃路径过滤逻辑保留 ...

    // blocks 格式：每个 block 渲染为一条摘要消息（多块 → 多条，顺序即时间顺序）
    if (entry.CompactBlocks is { Count: > 0 })
    {
        foreach (var block in entry.CompactBlocks)
        {
            var label = block.Topic is not null
                ? $"[上下文摘要 {block.BlockId}] ({block.Topic})"
                : $"[上下文摘要 {block.BlockId}]";
            messages.Add(new ChatMessage
            {
                Id = entry.Order,
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"{label}\n{block.Summary}",
                Order = entry.Order,
                EntryId = $"acp_summary_{block.BlockId}",
                CompressionBlockId = block.BlockId,
                CreatedAt = entry.Timestamp,
            });
        }
        return; // 本 compact 条目处理完
    }

    // 旧格式兜底：单摘要渲染为一条（迁移后正常不会走到）
    if (string.IsNullOrEmpty(entry.CompactSummary)) return;
    messages.Add(new ChatMessage { /* 旧逻辑：一条 [上下文摘要] */ });
}
```

**5.7.5 新增轻量查询（会话恢复用）**

```csharp
/// <summary>读取会话所有 compact 条目的 blocks，用于重建 CompressionState。</summary>
public async Task<List<(string BlockId, int Tier, string Summary, string? Topic, List<string> EffectiveMessageIds)>> GetCompactBlocksAsync(string sessionId)
```

### 5.8 `Models.cs` — ChatMessage 扩展

```csharp
/// <summary>
/// 压缩 block ID（仅摘要消息有值）。标识该消息是某 CompressionBlock 的摘要渲染。
/// 用于：会话恢复时重建 CompressionState；ref 注入时跳过摘要消息。
/// </summary>
public string? CompressionBlockId { get; set; }
```

### 5.9 `Agent.cs` — 持有 CompressionState

```csharp
/// <summary>压缩引擎状态（模型驱动压缩的运行时状态）。</summary>
public CompressionState CompressionState { get; set; } = new();
```

### 5.10 `TuiApp.Compact.cs` — 替换为 CompressionEngine

**保留**：`FindCutPoint`（对齐 user 边界）、`GenerateCompactSummaryAsync`（AI 摘要 + 规则 fallback）、`EstimateContextTokens`、`ScoreTurnsWithDecimator` / `FindSmartCutPoint`（RGDecimator 顾问模式）。

**替换**：`PrepareAndCompactAsync` → 走 `CompressionEngine.Compress` + `PersistCompactAsync`：

```csharp
private async Task PrepareAndCompactAsync(
    IReadOnlyList<ChatMessage> messages,
    int keepRecent,
    string reason,          // 保留：写入日志/审计
    bool willRetry)         // 保留：溢出重试语义
{
    var cutIndex = FindCutPoint(messages, keepRecent);
    // ... cutIndex 过小防御保留 ...

    var previousSummary = await _sessionStore.GetLatestCompactSummaryAsync(_sessionId);
    var summary = await GenerateCompactSummaryAsync(messages, cutIndex, previousSummary);

    var (newMessages, newState, block) = _compressionEngine.Compress(
        messages, _agent.CompressionState, 0, cutIndex - 1, summary, null);
    _agent.CompressionState = newState;
    _agent.ReplaceMessages(newMessages);

    await PersistCompactAsync(block, cutIndex, messages.Count);

    _agent.StabilityMonitor?.Unfreeze(_tokenCalibrator.Project(EstimateContextTokens(newMessages)));
    InvalidateContextMeasure();
    UpdateTokenDisplay();
    _nextOrder++;
    _persistedMessageCount = _agent.State.Messages.Count;
    AssertOrderInvariant();
    _lastParentEntryId = compactEntryId;
}

private async Task PersistCompactAsync(CompressionBlock block, int cutIndex, int messagesBefore)
{
    var dbMsgs = await _sessionStore.GetActiveMessages(_sessionId);
    var keptEntry = cutIndex < dbMsgs.Count ? dbMsgs[cutIndex] : null;
    var dto = new CompactBlockDto
    {
        BlockId = block.BlockId, Tier = block.Tier, Summary = block.Summary,
        Topic = block.Topic,
        DirectMessageIds = block.DirectMessageIds,
        EffectiveMessageIds = block.EffectiveMessageIds,
        DirectBlockIds = block.DirectBlockIds,
        CompressedTokens = block.CompressedTokens,
    };
    var compactEntryId = await _sessionStore.CompactSession(
        _sessionId, _agent.CurrentNovelId ?? "", [dto],
        keptEntry?.EntryId ?? "", messagesBefore,
        parentEntryId: _lastParentEntryId, order: _nextOrder);
    // 调用方负责 _lastParentEntryId = compactEntryId
}
```

注意：手动 `/compact` 与模型 `compress` 是**同一引擎的两个触发入口**；`CompressTool` 的持久化委托可复用 `PersistCompactAsync`（TuiApp 装配时绑定）。

### 5.11 `TuiApp.Session.cs` — 会话恢复

`RestoreContextFromDbAsync` 之后重建 `_agent.CompressionState`：

```csharp
/// <summary>从 JSONL compact entry 重建 CompressionState（比从摘要消息推断更精确）。</summary>
private async Task RestoreCompressionStateAsync(string sessionId)
{
    var blocks = await _sessionStore.GetCompactBlocksAsync(sessionId);
    _agent.CompressionState = CompressionState.Rebuild(blocks);
}
```

`CompressionState.Rebuild` 工厂：

```csharp
public static CompressionState Rebuild(
    IEnumerable<(string BlockId, int Tier, string Summary, string? Topic, List<string> EffectiveMessageIds)> blocks)
{
    var state = new CompressionState();
    foreach (var (id, tier, summary, topic, effective) in blocks)
        state.Blocks.Add(new CompressionBlock
        {
            BlockId = id, Tier = tier, Active = true,
            Summary = summary, Topic = topic,
            EffectiveMessageIds = effective, DirectMessageIds = effective,
        });
    if (state.Blocks.Count > 0)
        state.NextBlockId = state.Blocks.Max(static b =>
            int.TryParse(b.BlockId.AsSpan(1), out var n) ? n : 0) + 1;
    state.RebuildIndex();
    return state;
}
```

---

## 6. 数据流

### 6.1 模型主动压缩（主通道）

```
模型调用 compress({ startId: "m00150", endId: "m00220", summary: "..." })
    ↓ CompressTool.ExecuteAsync（解析 ref → 索引）
    ↓ CompressionEngine.Compress → 新消息列表 + 新 state + block
    ↓ agent.ReplaceMessages(newMessages) → ConversationLoop.RestoreFromMessages
    ↓ 持久化委托 → CompactSession(blocks, firstKeptEntryId, messagesBefore)
    ↓ 写 JSONL compact entry（blocks 数组）
    ↓ 返回 "[上下文摘要 b1] tokens: 26.4K → 2.1K"
```

### 6.2 安全网压缩（兜底通道）

```
LyapunovMonitor 冻结 / 用户 /compact / 溢出重试
    ↓ FindCutPoint → GenerateCompactSummaryAsync（AI 摘要）
    ↓ CompressionEngine.Compress（与 6.1 完全相同的引擎）
    ↓ stabilityMonitor.Unfreeze(新估算) 等收尾
```

### 6.3 解压（恢复原文）

```
模型调用 decompress({ blockId: "b5" })
    ↓ DecompressTool.ExecuteAsync
    ↓ CompressionEngine.Decompress(blockId, state, readOriginalMessages, inline: false)
    ↓ readOriginalMessages → FileSessionStore 从 JSONL 按 entryId 读取原文
    ↓ 写 ~/.cache/mythology/decompress/b5-<ts>.txt
    ↓ 返回 "Block b5 (12 条消息, 8.4K 字符) 已写入 …\n预览: …"
```

block 保持折叠：摘要仍在上下文中，缓存前缀不受影响。

### 6.4 会话恢复

```
加载会话 → GetContextMessages → BuildMessagesFromEntries（按 block 渲染摘要消息）
    ↓ GetCompactBlocksAsync → CompressionState.Rebuild（精确重建 block 索引）
    ↓ ref 计数器从下一个 order 继续（标签运行时分配，不持久化）
```

---

## 7. 迁移设计

沿用 `src/Core/Data/Migrations/` 现有机制（MigrationRunner 自动发现、version.json 追踪、幂等）。

### 7.1 迁移 002 — CompactBlocks

| 项 | 内容 |
|----|------|
| Version | 2 |
| Name | `002_CompactBlocks` |
| 描述 | compact entry 从单 summary 字段迁移为 blocks 数组 |
| 方向 | 旧数据 → 新格式（不做向后兼容） |

**转换规则**（幂等）：

- 遍历 `~/.mythology/sessions/*/*/{MANIFEST.json 所在目录}/*.jsonl` 的所有分片
- 逐行探测 `type=compact`
- 已有 `blocks` 字段 → 跳过（幂等）
- 旧格式：`summary` / `reason` / `willRetry` / `estimatedTokensAfter` →
  保留原字段（`id`/`timestamp`/`firstKeptEntryId`/`messagesBefore`/`order`），
  `summary` 包装为单个 block：`{"blockId":"b1","tier":1,"summary":<旧值>,...,"compressedTokens":0}`，
  **删除** `reason`/`willRetry`/`estimatedTokensAfter` 字段
- 原子写回（整文件重写 + 变更行替换）

**验证**：迁移完成后二次扫描确认无旧格式 compact 残留；有残留则抛异常阻止 version.json 写入（与 `001_SessionSegments` 相同的"防假装成功"模式）。

> 注：旧数据 `compressedTokens=0`、无消息 ID 映射——恢复（decompress）对迁移前的 block 返回空原文。这是可接受的降级：迁移数据多为已消费历史，语义是"摘要不可再展开"而非"数据损坏"。

---

## 8. 实现顺序与验证标准

每步验证通过后才进入下一步（AGENTS.md §4 目标驱动执行）。

| # | 步骤 | 验证标准 |
|---|------|---------|
| 1 | 新增 `CompressionBlock.cs` | `dotnet build` 通过 |
| 2 | 新增 `CompressionEngine.cs` | 单元测试：Compress 后消息数减少 + block 创建 + MessageToBlock 索引正确；Decompress 恢复原文；CJK token 估算（中文文本 > chars/4） |
| 3 | 改 `Models.cs`（CompressionBlockId）+ `Agent.cs`（CompressionState） | build 通过 |
| 4 | 改 `JsonlDto.cs`（Blocks + CompactBlockDto，删旧字段） | build 通过；现有 JSONL 写入/读取测试通过（旧字段删除需同步更新测试 fixture） |
| 5 | 改 `FileSessionStore.cs`（CompactSession 签名 / BuildMessagesFromEntries / FromCompactDto / GetCompactBlocksAsync） | 单元测试：写入 blocks → 读取按 block 渲染多条摘要；旧格式单 summary 兜底渲染 |
| 6 | 新增 `CompressionPrompt.cs` + 改 `PromptAssembly.cs` | `PromptAssembly.Build()` 输出含压缩策略段 |
| 7 | 新增 `CompressionTools.cs` | 注册到 ToolRegistry 后 GetAll() 含 compress/decompress |
| 8 | 改 `ConversationLoop.cs`（ref 注入 + nudge） | 发送给 LLM 的消息带 `[mNNNNN]` 标签；>85% 时注入紧急 nudge；持久化内容不含标签 |
| 9 | 改 `TuiApp.Compact.cs` | `/compact`、auto-compact、overflow 全走 CompressionEngine；compact 后 StabilityMonitor 解冻 + token 显示刷新 |
| 10 | 改 `TuiApp.Session.cs`（恢复 CompressionState） | 会话重启后 `_agent.CompressionState.Blocks` 与 JSONL 一致；后续 compress 的 blockId 从 max+1 继续 |
| 11 | 新增迁移 `002_CompactBlocks.cs` | 旧 JSONL → 新格式；重复执行幂等；残留检测生效 |
| 12 | 集成验证 | 端到端：模型 compress → 摘要替换 → decompress 恢复原文 → 会话重启状态一致 |

---

## 9. Phase 1 边界（刻意不做）

| 不做 | 原因 |
|------|------|
| `search_context` 工具 | 依赖检索基础设施；后续可桥接既有 RetrievalKernel（dense + FTS5 + RRF） |
| T2/T3 多级蒸馏 | 需 block 积累 + 蒸馏提示词；先跑 T1 收集数据 |
| compress 批量多范围 | 参数解析复杂度；单范围已覆盖主场景 |
| Pipeline 架构 | ConversationLoop 非 Pi context 事件模型 |
| messageRefs 双向映射 | EntryId 唯一 |
| protected 工具过滤 | compress 调用不在可见消息里 |
| emergency-truncate | 三重安全网已覆盖 |
| rebuild / fork-recovery | JSONL 即 source of truth |
| `<acp>` XML 标签 | 方括号格式已够，TUI 可过滤 |
| `ICompressionEngine` 接口 / 独立 nudge 类 / 独立 ref 注入类 | AGENTS.md §2/§3：无抽象、无单用类 |

---

## 10. 交付前检查清单

- [ ] 修改与 TargetFramework / LangVersion / 现有依赖兼容，未引入预览能力
- [ ] 压缩引擎为单一实现，无接口、无单用抽象类（`CompressionContext` 是装配记录非行为抽象）
- [ ] nudge 与 ref 注入内联在 ConversationLoop，无独立类
- [ ] 普通集合逻辑以清晰 LINQ 表达；Compress 收集覆盖消息单次遍历，无隐藏多次枚举
- [ ] CJK token 估算用于 ref 标签与压缩统计，非 chars/4 一刀切
- [ ] decompress 默认写文件（避免上下文膨胀），inline 为显式选择
- [ ] 迁移脚本幂等、带残留验证（防假装成功），失败阻止 version.json 写入
- [ ] 已运行 build + 相关单元测试 + 集成验证（§8 第 12 步）
- [ ] 无与 StabilityHarness 的耦合（Core 只通过既有 IStabilityMonitor 交互）
