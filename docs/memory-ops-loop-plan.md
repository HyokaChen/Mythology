# 记忆运维闭环：被动注入 + 启动包 + 会话晋升（借鉴 engrim）

## Context

VEC.md §3.1 承诺显式规则"是否注入 prompt：是，最高"，但代码中 `SearchUserMemoryAsync`（src/Core/Vector/RetrievalKernel.cs:131）的唯一调用方是 `RecallPreferenceTool`——记忆只有模型主动调工具才能进上下文。参考实现 engrim（Python）用三层运维闭环补齐同类缺口：minder 每轮被动注入、boot pack 启动包、会话→记忆晋升管线。本计划移植这三层，全部复用现有组件（AgentMemoryStore / AgentMemoryBm25 / AgentMemoryPacking），不引入新存储、不改数据库 schema、不加配置项。

红线保持不变：候选推断（kind=CandidateInference）不作为偏好注入——现有过滤路径不动；确认必须来自用户明示动作。

## Approach

按序执行；每步完成后现有测试应保持通过。新文件均为 file-scoped `namespace Core.Vector;`，风格对齐 AgentMemoryPacking.cs。

### A. 共享组件 MemoryMinder（新文件 src/Core/Vector/MemoryMinder.cs）

```csharp
public sealed class MemoryMinder(RetrievalKernel kernel)
{
    /// 每轮被动注入块；无命中/不可用返回 null。
    public async Task<string?> BuildInjectionAsync(string? novelId, string userText, CancellationToken ct = default)
    /// 启动包：显式规则 + resume 指针；无内容返回 null。
    public async Task<string?> BuildBootPackAsync(string? novelId, CancellationToken ct = default)
    /// 待确认候选审阅块；无候选返回 null。
    public async Task<string?> BuildCandidateReviewAsync(string? novelId, CancellationToken ct = default)
}
```

- `BuildInjectionAsync`：
  - 门控：`kernel.AgentMemory is null` → null。实质词提取：`AgentMemoryBm25.Tokenize(userText)`，剔除 `AgentMemoryConflict.Stopwords`（internal，同程序集可用）与长度 <2 的 token；实质词 <2 → null（琐碎消息不注入，engrim `_content_terms` 同款）。
  - `kernel.SearchUserMemoryAsync("local", novelId, userText, topK: 5, ct)` → 按结果顺序拼行，字符预算 1200，逐行累计超出即停。
  - 行格式：`- [{标签}] {内容截 80}`；标签判定：`DocumentKind == DocumentKind.UserRule` → `规则`，否则 `偏好`（复用 ConvertToRetrievalResult 的映射，RetrievalKernel.cs:241-245）。
  - 块格式（中文，项目文案规范）：
    ```
    [相关记忆 · 系统自动召回，供参考]
    - [规则] 不要用文言文
    ```
- `BuildBootPackAsync`：
  - `AgentMemory.GetAllAsync("local", ns: "rule", novelId: novelId, ct)` 全量显式规则 + `GetAllAsync("local", ns: "resume", novelId: novelId, ct)` 取 `CreatedAt` 最新一条作 resume 指针。两者皆空 → null。
  - 字符预算 2000；规则优先，超出从尾部截断。格式：
    ```
    [用户写作规则 — 必须遵守]
    - 不要用文言文
    [继续点]
    - 上次进行到：xxx
    ```
- `BuildCandidateReviewAsync`：
  - `GetAllAsync("local", ns: "default", novelId: novelId, ct)` 过滤 `MemoryKind == AgentMemoryKind.CandidateInference` 且 `!IsExpired(now)`，取最多 5 条；空 → null。
  - 格式：
    ```
    [候选偏好 — 需用户确认后处理]
    - 偏好破折号（id: abc12345）
    若用户认可某条：用 save_preference 保存，并用 tool_forget 删除候选 id；不认可直接 tool_forget 删除。
    ```
  - 说明：该块是把候选文本交给 agent 走确认流（VEC.md §4.4 "Agent 逐条展示给用户"的既有模式，analyze_style 工具结果同样进 prompt），不是把候选当偏好注入；候选仍不可能经 `SearchUserMemoryAsync` 泄漏为偏好（hybrid 分支已过滤，RetrievalKernel.cs:211）。

### B. 启动包接线（借鉴点 #3）

- TUI：`src/Tui/TuiApp.Session.cs` `InjectNovelContextAsync`（~line 606）。现有 `contextBlock` 构建完成后、`SetNovelContext(contextBlock)` 前：`_retrievalKernel` 非 null 时构造 `MemoryMinder`，`await BuildBootPackAsync(novelId)` 与 `BuildCandidateReviewAsync(novelId)`，非 null 块以 `"\n\n"` 追加进 `contextBlock`。kernel 为 null 或抛异常 → 静默跳过（对齐该方法现有 catch 风格，不阻断发消息）。规则内容按小说稳定 → 进 system prompt 不逐轮变化，prefix cache 友好。
- Desktop：`src/Desktop/ViewModels/MainWindowViewModel.cs` ~line 475 `SetNovelContext(profile.ToContextString())` 处同样追加 boot pack + 候选块。`MemoryMinder` 在 `src/Desktop/Program.cs`（retrievalKernel 已就绪，~line 225）构造后传入 MainWindowViewModel（实现时读该构造函数签名，加可选参数，默认 null 保持既有测试兼容）。

### C. minder 每轮被动注入（借鉴点 #1）

注入通道 = 追加到用户消息文本（engrim additionalContext 同款）。未选 system-prompt 注入：minder 块逐轮变化会破坏 provider prefix cache；未选 ConversationLoop 注入通道：需改核心循环且两个平台各接一遍。代价：注入块随消息持久化（可审计，恢复会话时用户气泡内可见块文本——接受，符合"记忆可审查"原则）。

- TUI：`TuiApp.Session.cs` `RunPromptAsync`，在 `await InjectNovelContextAsync();`（line 85）之后、`UpdateTokenDisplay`（line 108）之前：
  ```csharp
  var memoryBlock = _retrievalKernel is null ? null
      : await new MemoryMinder(_retrievalKernel).BuildInjectionAsync(_agent.CurrentNovelId, text, ct);
  ```
  `await _agent.Prompt(text, ct)`（line 110）改为 `await _agent.Prompt(memoryBlock is null ? text : text + "\n\n" + memoryBlock, ct);`
- Desktop：`src/Desktop/Services/DesktopAgentBridge.cs` `SendAsync`（line 53-69）。构造函数加可选参数 `MemoryMinder? memoryMinder = null`；`await Agent.Prompt(userMessage, ct)`（line 62）前调用 `BuildInjectionAsync(Agent.CurrentNovelId, userMessage, ct)`，非 null 拼接后发送。装配点在 `src/Desktop/Program.cs`（bridge 构造处，实现时定位 `new DesktopAgentBridge(`）。
- TUI 的 `MemoryMinder` 实例化为局部即可（无状态）；Desktop 存字段复用。

### D. 会话→记忆晋升管线（借鉴点 #2）

新文件 `src/Core/Vector/MemoryPromotionScanner.cs`：

```csharp
public sealed class MemoryPromotionScanner(AgentMemoryStore store)
{
    /// 扫描 messages[startIndex..] 中 user 角色消息的偏好信号句，
    /// 词法去重后存为 CandidateInference 候选；返回新增条数。
    public async Task<int> ScanAsync(
        string? novelId, string sessionId,
        IReadOnlyList<ChatMessage> messages, int startIndex,
        CancellationToken ct = default)
}
```

- 句子提取：仅 `Role == MessageRole.User` 且非空消息；按 `。！？；\n` 切句；句子长度 ≥6 才参与。
- cue 词表（常量数组，中文偏好信号）：`["以后", "记住", "偏好", "我喜欢", "不喜欢", "不要", "别再", "尽量避免", "默认", "一律", "永远", "保持", "请用", "请别"]`——句子包含任一子串即候选。
- captured-check（engrim `_is_captured` 词法层同款）：候选句与存量记忆去重。存量 = `GetAllAsync("local", ns: "rule", novelId)` ∪ `GetAllAsync("local", ns: "default", novelId)` 中 `MemoryKind != CandidateInference` 的记录；比较用 `AgentMemoryBm25.Tokenize` + `AgentMemoryConflict.Stopwords` 后的 token 集 Jaccard ≥ 0.6 → 跳过。
- 入库：`store.StoreAsync(句子截 200 字符, ownerId: "local", novelId, ns: "default", memoryKind: AgentMemoryKind.CandidateInference, confidence: 0.5, importance: 0.3, evidenceRefs: [$"session:{sessionId}"])`——其余可选参数默认；`evidenceRefs` 参数名以 AgentMemoryStore.cs:274 附近 `evidenceRefsJson` 的来源签名为准（实现时读完整签名）。候选落 ns="default" + kind=2：`SearchUserMemoryAsync` hybrid 分支过滤（RetrievalKernel.cs:211）→ 偏好召回不可见；boot 规则分支只读 ns="rule"（RetrievalKernel.cs:149-192）→ 启动包不可见。注入面零改动、红线由构造保证。
- 触发点（TUI `TuiApp.Session.cs`）：
  - `RunPromptAsync` 中 `await PersistNewMessagesAsync();`（line 111）后：try/catch 包裹调用 `ScanAsync(novelId, _sessionId, _agent.State.Messages, _promotionScanIndex)`，成功后 `_promotionScanIndex = _agent.State.Messages.Count`。异常记日志不阻断（对齐 AgentMemoryHook 的容错风格）。同步 await（纯词法 + 少量 sqlite 读，毫秒级）。
  - 新字段 `private int _promotionScanIndex;`。会话恢复路径（OpenNovelAsync line ~498、ResumeSessionAsync line ~586、branch 恢复 line ~781 的 `_persistedMessageCount = ...` 赋值处）同步置为当前消息数——不回扫历史，避免旧候选重复打扰（captured-check 兜底幂等）。
  - Desktop：`DesktopAgentBridge.SendAsync` 完成后同样扫描（`Agent.Messages`），index 存 bridge 字段，会话切换由 `Agent.ReplaceMessages` 后调用方重置——Desktop 侧在新会话开始处（NewConversationAsync / 会话恢复后首次 SendAsync 前）置 0，实现时以 `_agent.State.Messages.Count` 初始化。
- 候选的消费路径即 B 步的 `BuildCandidateReviewAsync`（下次会话启动出现在 system 上下文，agent 主导确认），无新增工具、无新增 UI。

### E. 工具描述补充

`src/Core/Tools/AgentMemoryTools.cs` `tool_store`（line 60-64）Description 末尾追加一句：`ns="resume" 的最新记录将作为会话继续点注入下次启动包。`仅文案，无逻辑。

## Critical files & anchors

- `src/Core/Vector/MemoryMinder.cs`（新）——三个构建器；依赖 RetrievalKernel/AgentMemoryStore/AgentMemoryBm25/AgentMemoryConflict。
- `src/Core/Vector/MemoryPromotionScanner.cs`（新）——扫描+去重+候选入库。
- `src/Tui/TuiApp.Session.cs`——`RunPromptAsync`（75-115）、`InjectNovelContextAsync`（606-658）、三处会话恢复赋值点（~498/~586/~781）。
- `src/Desktop/Services/DesktopAgentBridge.cs`——`SendAsync`（53-69）、构造函数（29）。
- `src/Core/Vector/RetrievalKernel.cs`——`SearchUserMemoryAsync`（131-221）语义参照，只读不改。

## Verification

1. 新单测 `src/Core/Tests/MemoryMinderTests.cs`：
   - 空记忆 → `BuildInjectionAsync` 返回 null；单实质词（如"好的"）→ null。
   - 规则+偏好并存 → 块含规则行且排在偏好前；超预算截断不超 1200 字符。
   - `BuildBootPackAsync`：全部显式规则出现；ns="resume" 最新一条出现在 [继续点]；无 resume 时无该节。
   - `BuildCandidateReviewAsync`：只含 kind=CandidateInference 的行；Explicit 不出现。
2. 新单测 `src/Core/Tests/MemoryPromotionScannerTests.cs`（DummyEmbeddingService 纯词法，参照既有 RagTools/PreferenceTools 测试装配）：
   - 含 cue 的 user 句 → 入库为 CandidateInference，EvidenceRefsJson 含 sessionId。
   - assistant 角色消息即使含 cue 也不入库。
   - 与存量显式记忆 Jaccard ≥0.6 的句子跳过（captured-check）。
   - 无 cue 的长句不入库。
3. 注入安全回归（既有）：Core.Tests 全量 `dotnet run --project src/Core/Tests`（xunit v3 MTP），SearchUserMemoryAsync 不返回 kind=2 的既有断言必须保持绿。
4. 端到端冒烟（throwaway 脚本或手动 TUI）：
   - 装配真实 AgentMemoryStore（临时 sqlite）：`save_preference` 写入规则"不要用文言文" → `BuildInjectionAsync(novelId, "以后都不要用文言文，另外帮我写一段打斗")` → 输出包含该规则行。
   - 手动 TUI 冒烟：启动 Host，保存偏好 → 发含偏好关键词的消息 → 模型响应体现规则已可见（或用 `/state`/日志确认注入块）；重启会话 → 系统上下文含 [用户写作规则] 节；会话中说"以后偏好短句" → 下次启动出现 [候选偏好] 节，确认 save_preference 后候选消失。

## Assumptions

- 注入通道为用户消息追加（engrim 同款）；注入块随会话持久化属接受的成本，换来零 ConversationLoop 改动与 provider prefix cache 稳定。
- 候选确认走 agent 主导（engrim auto-curate 模式：system 上下文给候选清单 + 指令），不做独立确认对话框；后续需要可加 slash 命令，不在本次范围。
- captured-check 用词法 Jaccard 而非语义 cosine（engrim 词法层同为第一道防线）；误报方向是"少存候选"，安全。
- 不新增配置项与设置 UI；minder 门控（<2 实质词）即天然开销上限。
- resume 指针是约定（ns="resume"），由 agent 经 tool_store 写入（E 步描述已提示），晋升管线不自动写 resume。
