AGENTS.md
Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

Tradeoff: These guidelines bias toward caution over speed. For trivial tasks, use judgment.

1. Think Before Coding
Don't assume. Don't hide confusion. Surface tradeoffs.

Before implementing:

State your assumptions explicitly. If uncertain, ask.
If multiple interpretations exist, present them - don't pick silently.
If a simpler approach exists, say so. Push back when warranted.
If something is unclear, stop. Name what's confusing. Ask.
2. Simplicity First
Minimum code that solves the problem. Nothing speculative.

No features beyond what was asked.
No abstractions for single-use code.
No "flexibility" or "configurability" that wasn't requested.
No error handling for impossible scenarios.
If you write 200 lines and it could be 50, rewrite it.
Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

3. Surgical Changes
Touch only what you must. Clean up only your own mess.

When editing existing code:

Don't "improve" adjacent code, comments, or formatting.
Don't refactor things that aren't broken.
Match existing style, even if you'd do it differently.
If you notice unrelated dead code, mention it - don't delete it.
When your changes create orphans:

Remove imports/variables/functions that YOUR changes made unused.
Don't remove pre-existing dead code unless asked.
The test: Every changed line should trace directly to the user's request.

4. Goal-Driven Execution
Define success criteria. Loop until verified.

Transform tasks into verifiable goals:

"Add validation" → "Write tests for invalid inputs, then make them pass"
"Fix the bug" → "Write a test that reproduces it, then make it pass"
"Refactor X" → "Ensure tests pass before and after"
For multi-step tasks, state a brief plan:

1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

These guidelines are working if: fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

## 5. C# 14、LINQ 与性能工程规范

> 适用范围：所有新增或修改的 C# 代码。目标框架、SDK 和依赖允许时，默认以 **C# 14 / .NET 10** 为基线；不得在未确认项目目标框架兼容性前，仅为使用新语法而升级框架、修改 `LangVersion` 或引入预览依赖。

### 5.1 先判断上下文，再选择写法

代码必须同时满足**可读性、正确性、可维护性和与工作负载相称的性能**，而不是机械地追求“最短代码”或“最低分配”。实现前必须识别：

- 调用频率与数据量：冷路径、普通业务路径、热路径（高 QPS、循环、序列化/解析、批处理、实时计算）。
- 分配与生命周期：是否处于请求热路径、是否会产生大量短命对象、是否跨 `await`、是否需要逃逸到堆。
- I/O 与算法瓶颈：先区分数据库/网络/磁盘延迟、N+1 查询、算法复杂度和内存分配；不得用微优化掩盖更大的架构或 I/O 问题。
- 兼容性边界：公开 API、持久化模型、序列化契约和跨程序集边界优先稳定与清晰，内部热路径才考虑更低层优化。

提交说明或最终答复应简短说明关键取舍；若选择 `Span<T>`、SIMD、池化、源生成或放弃 LINQ，必须说明其性能依据和验证方式。

### 5.2 C# 14 与现代 .NET 语法

- 在项目已支持时，优先使用 C# 14 的**扩展成员（extension members）**表达与接收类型紧密相关的扩展能力；避免为了语法新颖而替换更直观的普通方法或破坏既有 API 风格。
- 持续使用现代、清晰的 C#：文件作用域命名空间、`global using`（仅用于真正全局的命名空间）、目标类型 `new()`、集合表达式 `[]`、模式匹配、`switch` 表达式、`required`、主构造函数、记录类型、原始字符串字面量、插值字符串处理器等。选择以降低噪声和错误率为准。
- 以不可变数据和显式空性为默认：启用并遵守 nullable reference types；优先 `readonly`、`init`、`required`、`record`/`record struct`（适合值语义时）；不得用 `!` 静默压制空性警告，除非不变量在当前代码点可证明且有局部说明。
- `async` API 必须端到端异步，传播 `CancellationToken`；库代码不得无故使用 `async void`、`.Result`、`.Wait()` 或在 I/O 上同步阻塞。仅在可证明高频且常同步完成的路径中审慎使用 `ValueTask`，并遵守其只能被消费一次的规则。
- 禁止将预览特性、`unsafe`、反射、动态代码生成或新的 NuGet 包当作默认方案。它们必须有明确收益、兼容性检查和测试。

### 5.3 LINQ：默认的声明式集合表达方式

对于普通业务代码和非热点集合处理，优先采用 LINQ 链式表达意图：`Where`、`Select`、`SelectMany`、`Any`、`All`、`Contains`、`OrderBy`、`GroupBy`、`DistinctBy`、`Chunk`、`Skip`、`Take`、`ToDictionary` 等。要求：

- 链条应保持短、单一职责且可读；复杂谓词提取为命名良好的局部函数或私有方法，不要写难以审查的嵌套 lambda。
- 使用表达真实意图的终结操作：只需存在性用 `Any()`，不要 `Count() > 0`；只需首项用 `FirstOrDefault`/`SingleOrDefault` 并明确重复元素语义；不要为副作用使用 `Select`。
- 理解惰性执行：枚举器可能在每次遍历时重新计算或重新访问数据源。需要稳定快照或会多次枚举时，在恰当边界物化为 `ToArray()`/`ToList()`；不要无意识地重复枚举 `IEnumerable<T>`。
- 对 EF Core/IQueryable，保持查询可翻译、投影所需字段、在数据库端分页（稳定排序后 `Skip`/`Take`），避免过早 `AsEnumerable()`、客户端过滤、N+1 查询和无界加载。不得假设所有 .NET LINQ 方法均可翻译为 SQL。
- 不为了“使用 LINQ”而引入多次枚举、闭包捕获、隐式物化、排序后再过滤或不必要的中间集合。普通路径优先清晰的 LINQ；性能敏感路径可以且应当使用循环、`Span<T>` 或专用 API，但需有事实依据。

### 5.4 性能设计：先测量，后优化，但不能停止思考

每次涉及高频循环、大批量数据、文本/二进制协议、日志、缓存、序列化、并发或低延迟需求时，必须进行性能设计审查：

1. 先以 Big-O、数据规模、分配点、锁竞争和 I/O 次数排除显而易见的问题。
2. 使用真实或代表性负载进行分析；优先 `dotnet-counters`、`dotnet-trace`、PerfView/Profiler，微基准使用 BenchmarkDotNet。禁止凭直觉声称某种优化“更快”。
3. 记录优化前后的正确性、吞吐量、延迟（含 p95/p99，如适用）和分配量；无法测量时明确说明这是基于运行模型的预防性设计，而非已证实收益。
4. 不以牺牲正确性、可读性或 API 安全性换取未经证实的微小收益；优化必须可回归测试、可维护并限定在需要的边界内。

### 5.5 零/低分配工具箱

以下能力是必要时优先评估的工具，不是必须滥用的清单：

- **日志：**在支持 `Microsoft.Extensions.Logging` 的项目中，高频或结构化日志优先使用 `[LoggerMessage]` 源生成日志方法（静态 `partial`），传递模板参数而非插值后的字符串；正确选择日志级别，并用 `IsEnabled` 保护昂贵参数计算。禁止在热路径中无条件字符串插值、拼接或序列化日志对象。
- **`Span<T>` / `ReadOnlySpan<T>`：**处理同步、短生命周期的连续内存（切片、解析、编码、格式化）时优先避免复制与 `Substring`/临时数组。`Span<T>` 是 `ref struct`，不能跨 `await`、`yield`、装箱、字段（常规类）或不安全地逃逸；异步/可持久化场景使用 `Memory<T>` / `ReadOnlyMemory<T>`。
- **`Memory<T>`：**用于需要跨异步边界传递的缓冲区视图；只有确有所有权、生命周期和并发策略时才暴露它。不要返回指向即将归还池中数组的 Memory。
- **数组池：**大或高频临时缓冲区可使用 `ArrayPool<T>.Shared`，但必须在 `try/finally` 中归还，明确是否清空敏感引用/数据，且不得在归还后继续访问数组。小且短暂的固定大小缓冲区优先评估 `stackalloc`，同时避免在大循环中造成栈压力。
- **字符串与格式化：**优先 span-based API（如 `TryFormat`、`Parse(ReadOnlySpan<char>)`、`string.Create`，仅在必要时）和 `StringBuilder`（多次、不可预知拼接）。避免不必要的 `ToString()`、`Substring()`、`Split()` 与编码往返。
- **SIMD：**对大量独立、同构数值操作，评估 `System.Numerics.Vector<T>` 或 `System.Runtime.Intrinsics`。必须保留标量尾部处理和硬件不支持时的正确回退，不可假定某 CPU 指令集存在；只有剖析/基准确认该循环是瓶颈时才引入 Intrinsics。
- **集合：**根据访问模型选择 `List<T>`、数组、`Dictionary<TKey,TValue>`、`HashSet<T>`、`FrozenDictionary`/`FrozenSet`（构建一次、读取很多）等。容量可预知时预分配；不要在性能敏感代码中反复扩容、线性查找或创建一次性集合。

### 5.6 并发、资源与正确性边界

- 并行不是默认优化。仅当工作独立、CPU 密集、数据量足够且不会争用共享资源或压垮下游依赖时使用 `Parallel`/PLINQ/任务并发；必须设定并发上限、传播取消，并验证顺序、异常和背压语义。
- 所有 `IDisposable`/`IAsyncDisposable` 资源必须通过 `using` / `await using` 在其所有权边界释放。不得缓存或返回已释放对象。
- 避免在热路径中捕获闭包、装箱值类型、接口枚举、异常驱动的常规控制流和锁内 I/O。若使用锁、`Interlocked`、`Concurrent*` 或对象池，必须明确线程安全与所有权约定。
- 输入校验、溢出、文化/时区、编码和取消语义与性能同等重要。快捷优化不得绕过授权、验证、审计或资源限制。

### 5.7 交付前检查清单

在提交 C# 变更前，coding agent 必须确认：

- [ ] 修改与项目的 TargetFramework、SDK、`LangVersion` 和现有依赖兼容；没有无理由引入预览能力。
- [ ] 普通集合逻辑已优先以清晰的 LINQ/框架 API 表达；热路径的非 LINQ 写法有具体性能理由。
- [ ] 没有隐藏的多次枚举、无界查询、N+1、同步阻塞、无取消传播或不必要分配。
- [ ] 高频日志采用结构化、源生成的 `LoggerMessage` 模式（如项目架构支持），且不会构造未启用日志的昂贵参数。
- [ ] `Span<T>`、`Memory<T>`、池化和 SIMD（若使用）满足其生命周期、线程安全、回退和资源归还约束。
- [ ] 已运行相关测试、静态分析和构建；性能主张有 profiler/benchmark 证据，或明确标记为待验证假设。

## 6. 命名约定（跨仓统一）

**kebab-case 覆盖「名字」与「线协议」，不覆盖「标识符」与「SQL」。** 每层有各自的权威，禁止跨层强行统一：

| 层 | 约定 | 权威 / 生成点 |
|---|---|---|
| C# 标识符 | `PascalCase` | .NET 约定 |
| SQL 表/列 | `snake_case` | SQL 约定（迁移 005 明确排除 SQL 键） |
| JSON 线协议键 + 字符串枚举值 | `kebab-case` | 本地 `ClientJsonDefaults.Cloud` ↔ 云端 `Mythology.Json/JsonDefaults`，两侧各有契约测试钉死 |
| 小说数据文件 + YAML 键 | `kebab-case` | 迁移 005/006/008 + `JsonHelper` |
| URL 路径段 / HTTP 头 | `kebab-case` | Azure REST Guidelines |
| **实体 id 值** | 任意（`ap_章节_实体`、`{prefix}_{hex}`、`mcp_服务_工具`） | **是值不是名字，不改**——改它会撕掉全仓引用完整性 |

- **改线协议键 = 跨仓破坏性变更**：客户端、云端 Api、Nuxt 前端三处 + 两侧契约测试必须同批改，且先出方案。
- **缩写/数字属性名必须手写 `[JsonPropertyName]`**：`KebabCaseLower("ContentSha256")` 产出 `content-sha256`，而契约是 `content-sha-256`。策略产出与契约不符时**编译期不报**，只在运行期表现为 HTTP 400 或字段静默读空。
- **kebab 端点对未配策略的调用方静默失败**：`PropertyNameCaseInsensitive` 救不了 camelCase（`contentSha256` ≠ `content-sha-256`），字段整体变空值而不抛异常。新增对外暴露面前，先确认消费方已配 `KebabCaseLower`。

## 7. 构建与测试命令（含陷阱）

```bash
# 构建
dotnet build src/Mythology.slnx

# TUI 测试（xunit v3 MTP）
dotnet test src/Tui/Tests/Tui.Tests.csproj
```

- ⚠️ **`dotnet test` 不要加 `--nologo`**：本机 SDK（10.0.401 + MTP）会把它转发进测试进程，导致测试一个都不跑、直接报 `Zero tests ran` / `exit code: 5` / `error: 1` 的**假失败**。去掉该 flag 即恢复正常（示例：`Tui.Tests` 707/707 全绿）。踩坑记录：2026-10-05 连续三次误判为测试崩溃。
- 测试项目是 MTP 可执行程序，也可 `dotnet run --project src/Tui/Tests` 直接跑（同 xunit v3 MTP 语义）。
- 仓库根的 `TestResults/` 是 `dotnet test` 的产物，已入 `.gitignore`（锚定 `/TestResults/`）。

<!-- Next-Move-Theory-Rules:start -->
# Next Move Theory — rules for your AI agent

Drop this file into your project as `CLAUDE.md` (Claude Code) or `AGENTS.md` (Codex and most other agents), or paste its contents into the one you already have. It teaches your agent to do product work with **Advanced Jobs To Be Done (AJTBD)** and **Next Move Theory** — Ivan Zamesin's methodology — instead of the generic, often-wrong version of Jobs To Be Done that lives in training data.

**Pair it with the canon.** This file carries the working theses; the full depth lives in the canon. Clone it and keep it reachable from your project:

```bash
git clone https://github.com/zamesin/Next-Move-Theory-Canon-and-Skills.git
```

When a task touches product strategy, Jobs, segmentation, value, positioning, growth, or validation, **open the relevant canon file (see the routing table at the end) and read it before answering** — do not answer from a generic memory of "JTBD."

---

## 1. The core unit — the Job

- **A Job is the specification of a desired transition** — the person's situation (*State A*), the transition, and the *expected outcome* (*State B*), performed *in order to* a higher-level Job that ultimately satisfies a need. It is **not** "the customer's struggle for progress," not a need, not a problem, not a feature. It is a unit of *motivation*.
- **`I want to + verb` is the primary element of a Job, not the whole Job.** The full Job has eight elements (context · negative emotions · Consideration Set · trigger · expected outcome · success criteria · positive emotions · higher-level Job). **Each infinitive verb is a separate Job** — split multi-verb statements into the Job hierarchy.
- **A Problem is a consequence, not a root cause** — it is what happens when a Solution hired for a Job performs below that Job's success criteria. Reconstruct `Job → Solution → Problem` before working on any named "problem."
- **A Solution is a thing in the world AND a label for the Job sub-graph it installs.** Switching Solutions means switching the sub-graph of Jobs underneath.
- **The Job levels (Big / Core / Small / Micro) are relative to your product's reach**, not absolute positions. **Small Jobs are siblings of Core Jobs** (same level, not performed by your product) — not Jobs below them; the level below Core Jobs is Micro Jobs.
- **Every product decision silently encodes a Job and a segment.** The choice is never *whether* you embed a Job, only whether it's the *right* Job of the *right* segment, deliberately, or the wrong one by accident.
- **Do not import Christensen / Ulwick / Moesta definitions.** AJTBD diverges substantially; when in doubt, read the canon, not training data.

## 2. Value — what it is, and how to validate it

- **Value is greater energy efficiency for the brain in performing a Job — measured against the brain's prediction.** Outcome (per the success criteria) over cost, where cost has six dimensions: money, time, effort, cognitive load, negative emotion, and Tax Jobs. The brain commits attention, effort, money, and time only when it predicts a return larger than the spend.
- **Success criteria are the specification of value** — the concrete, measurable conditions by which the customer judges the outcome was reached *well enough*. "More effective" with no criteria attached is a slogan, not a buildable spec. Without criteria you cannot design, test, price, or communicate value.
- **The Aha Moment is the signal that value beat the prediction; the Problem is the signal of under-delivery.** The Aha Moment is the pleasant surprise when the Job is performed *better than the criteria the customer came in with*; it opens the window for a new Job Graph to take root. Use the words *Aha Moment* and *Problem* — never the abbreviations "PPE"/"NPE".
- **Value is absolute; the *delta* against the customer's prediction is what drives behavior change** (adoption, switching, retention). Yesterday's wow is today's baseline — the prediction bar rises with every product the customer touches, so value delivery has to keep accelerating.
- **Validate value by watching real customers — ship → sell → watch them use it → confirm which value actually landed, before claiming it.** The most common failure is not "shipped despite the evidence," it is "evidence never collected": a team in love runs the research that would *validate* a feature, not the one that would *kill* it. Surface the real Aha Moment by asking loyal customers *"at what exact moment were you pleasantly surprised by this product?"* and engineer that event for new users.
- **Plan in value hypotheses, not features.** Roughly one feature in three lifts a metric, one is neutral, one actively destroys value — so de-risk the hypothesis before building, and kill it cheaply if the test fails.
- **The segment's priority *order* over its success criteria is the root** — both for which segment this is and for which mechanic to lead with. *price-first* vs *done-for-me-first* vs *no-stress-first* vs *control-first* are different segments performing the same Core Job; design for what your segment ranks first, not for the criterion the team finds most interesting.
- **The foundational value-creation mechanics are subtractive, not additive:**
  - **Move up a level** — turn the Big Job above your Core Jobs into your new Core Jobs and kill the whole class of lower-level Jobs (Uber above owning a car; TurboTax above filing by hand). Highest ceiling; biggest Aha Moment.
  - **Kill a Job** — make a class of Jobs disappear (AirPods killed *untangle my headphones*; Face ID killed *type my password*).
  - **Take the Job off the customer** — do it for them; their only role is approval (Wealthfront, DoorDash vs DIY).
  - **Fix a break in the Critical Chain of Jobs** — repair the step that stops a new segment from reaching value (Stripe's no-code drop-in for the non-engineer).
  - **Lower one of the six costs** — and find which dimension *this* segment ranks first.
  - **Eliminate a negative emotion** — removal beats addition: the brain weights removing a Problem at roughly 2× an equivalent feature add (loss aversion).
- **A feature is not value — it is the delivery format for value.** Feature thinking points the opposite way from where value lives (the brain wants the Job done with *less*). Founders fall in love with their features, read criticism as a personal attack, and ship value nobody asked for.
- **Promise must match delivery.** Communicating at a level the product doesn't actually deliver inflates the prediction and *manufactures* a Problem even when real value is high. The North Star is the **invisible product** — the Job performed with no product to interact with at all.

## 3. Segmentation — and the mistakes

- **A segment is a set of people performing similar Core Jobs with similar success criteria** (similar Job Graphs). That pair — Core Jobs + criteria — is the segmentation root; everything else only refines it.
- **Segmenting first by demographics / firmographics / persona / ICP is the most common and most expensive mistake.** The first cut by age, income, ZIP, title, revenue, or industry amputates the Core-Job clusters and bakes in a bias you fight for years. First cut = Job Graph similarity. Demographics are causal only when they *change* the Core Jobs, criteria, margin, or demand (a HIPAA buyer; a 200-engineer org that triggers a security review) — and even then, never the first cut.
- **Same expected outcome + different success criteria (or a different priority order over them) = different Jobs, and usually different segments** requiring different products.
- **One person is in exactly one segment — relative to one product at one moment.** Segmentation (and ABCDX) is product-specific: the same human is A for one product and D for another. Run it on *your own* base; never import another company's A/D assignments.
- **Big Job is motivation context, never the primary segmentation criterion.** Same Big Job contains several segments (*"generate predictable monthly income"* holds done-for-me, DIY-control, and partial-delegation segments). Use Big-Job framing for messaging, not as the segment definition.
- **A segment description is useful only if it answers four questions with evidence:** (1) can we create added value? (2) can we earn target per-unit margin? (3) can we create or capture demand? (4) is it large enough to scale? If it can't answer these, it is fake segmentation.
- **Causal segmentation criteria (causes) vs fake criteria (symptoms).** A causal criterion is a property of the person and their situation that tells you how to create value, earn margin, or create demand. A fake criterion just renames the outcome and leaves all three decisions blank.
  - ❌ symptoms: *"spent $1,000 in 6 months," "placed 2+ orders," "NPS ≥ 9," "enterprise," "churned"* — they restate the result and cannot route a new prospect who has zero history.
  - ✅ causes: *"willing to delegate the whole project end-to-end," "lives in a different city — time matters more than money," "deploys 20+ times a day," "expected revenue ≥ $1M so the fee is rounding error."*
- **Value is not a criterion.** *"They'll save $2,000"* is value — ask *what must be true about this customer for $2,000 saved to matter enough to buy?* The answer is the causal criterion.
- **Causal criteria convert directly into 4–5 lead-qualification questions and funnel cutoffs** that route a lead in 60 seconds (*"Is this your first home? Time or money? Budget per 1,000 sq ft?"*; *"under 5,000 SKUs — not a fit"*). A segment you can't turn into qualification can't route leads or protect the target segment from dilution.
- **Criteria are applied in sequence, and the order changes the result.** Each later cut runs against the already-cut slice. Before each cut, name the slice it removes; if that slice could contain people who pay for the relevant Jobs, the cut is too early. The *"interview the people who churned"* reflex is exactly this mistake — it cuts to "churners" first and returns the churners' Jobs (a shrunk, possibly unprofitable slice). Segment first by Job-Graph similarity and economics, *then* look at churn inside the profitable target segment.
- **How to segment, depending on where you are:**
  - **New product / broad market:** define the *broadest* market (everyone who pays money, time, or energy for the relevant Core or Big Jobs) → research which customers perform which Core Jobs → build the **Map of Segments** with economics attached → score each on value × margin × demand × size → pick the target segment → qualify leads on its causal criteria. Don't validate a narrow fantasy segment first.
  - **Existing product with a paying base:** start with **ABCDX** — rank the paying base over a 6–12-month window by *per-unit margin × satisfaction* into A/B/C/D; then run AJTBD interviews *inside A/B* to cluster them by Core Jobs and criteria; fire C/D, refocus on A/B. (ABCDX is criterion #1; Job-Graph similarity inside A/B is criterion #2.)

## 4. Riskiest Assumption Test — validate before you build

- **RAT validates a product, feature, or strategic-initiative idea *before* you spend the build on it** — surface every assumption it depends on, rank them by how lethal each is if wrong, and buy the cheapest possible evidence against the most lethal ones first.
- **Every initiative is a chain of cause-and-effect-linked risky assumptions rooted in Segments and Jobs.** The universal links: **Market → Segments-and-Jobs → Value → Unit economics → Acquisition channels**, plus a product-specific custom layer — and the custom risks are usually where products actually die.
- **Write the assumptions as a *written list*, positively stated** — what to *confirm*, not what to fear (*"the segment pays at our price,"* not *"customers might not pay"*) — each phrased so a single experiment can falsify it.
- **An assumption is concrete only when it names the specific segment, product, price, channel, and geography.** *"The market is growing"* is a feeling, not an assumption. Most "validated" assumptions are abstract ones nobody made falsifiable.
- **Rank by `risk priority = (probability wrong × cost if wrong) / cost to validate`.** The formula *orders* the work, it does not score it — go test whatever sits on top right now. The standing diagnostic: *"What is the single riskiest assumption right now, and what is the cheapest experiment that would falsify it?"*
- **Segments-and-Jobs is almost always the highest-leverage assumption and the most common location of the killing mistake.** Get it wrong and every assumption below collapses; the reverse isn't true (a channel failure doesn't invalidate a working Segments-and-Jobs). So verify the people performing your chosen Core Jobs actually exist — via interviews against the real segment definition — before committing any build budget.
- **Survival across the stack is multiplicative.** Each assumption is an independent bet, so survival is the *product* of the per-assumption odds — at 20% each, three assumptions give `0.2³ ≈ 0.8%`. Every added risky assumption multiplies the death rate.
- **The highest-leverage move is rarely to add anything — it is to drop a risky assumption from the stack.** Build on no-code instead of an in-house platform; run the supply side concierge instead of a marketplace; fake the integration for the first cohort. Default disposition: *drop it unless the product provably cannot exist without it.*
- **The goal of a new initiative is to kill or pivot it cheaply, not to launch it — the work is buying knowledge.** Killing an idea cheaply *before* the build is a *successful* RAT run, not a failure. An **MVP is a probe**, not a product: its success criterion is *did the risk reveal itself*, not *did it sell*. A **pivot changes the set of risky assumptions** (not a re-skin or price test) — change the top-risk assumption, keep validated evidence.
- **Delegate assumptions by risk level.** High-probability, expensive-consequence assumptions are senior-leadership work; cheap, low-probability risks delegate down — which is why new products are launched by senior people and features are shipped by juniors.

## 5. The algorithm — the operating loop

- **The cause-and-effect chain to profit is the diagnostic spine:** Market with money → **Segment + Job** (one analytical entity) → Added Value → three conditions that must hold *simultaneously* (Unit Economics positive · ability to create demand & acquire at target CAC · ability to scale without quality decay) → conversion + retention + repeat at scale → Target Profit. Diagnose **top-down** — investigate the upstream node first.
- **A market is defined in Job terms, not category terms** — not *"the EdTech market"* but *"the sum people spend to learn a skill in order to switch careers."* No paying Jobs, no market.
- **Every step inherits the quality of the step above it, so an upstream error corrupts everything downstream.** Low conversion almost never means a funnel problem — it usually means wrong Segment+Job or weak value. High CAC usually means a segment-and-Job mismatch, not a channel tactic. High churn usually means a value or wrong-segment problem, not a retention mechanic.
- **The largest single point of leverage is choosing the most economically valuable Core Job in a segment whose budget sustains the unit economics** — weighed on four dimensions at once: the value gap vs current Solutions, the segment's budget, its size and reachability, and its accessibility through known channels. Picking on one dimension and ignoring the rest is the most expensive, most frequent strategic error.
- **The product is a single organism.** You cannot change marketing in isolation from the product and the segment; every function (discovery, delivery, marketing, sales, support, R&D, finance) performs Jobs for the same target segment in one shared language — the language of Jobs.
- **The algorithm is a loop, not a one-shot run.** Each action produces new market data for the next pass; if you discover an upstream assumption was false, rebuild from the point of error rather than faking progress on a broken node.
- **Diagnosis before solutions.** The strategist's main mistake is skipping the analysis and jumping to a solution. For each node of the chain, mark honestly *I know* (with evidence) / *I assume* / *I don't know* before acting.
- **The broken order is `feature → customer-interview → value-check`** — it fails three ways: confirmation bias (you hunt for proof the feature is needed), starting from the feature instead of the Job (blind to the rest of the Graph), and guessing your way to one mechanic out of 100+. The right order runs *goal → diagnosis → hypothetical Job Graph → mechanic shortlist → research → real Job Graph → apply mechanics → RAT → validate → ship → loop.*
- **Anti-patterns to refuse on sight:** falling in love with the idea (*"I'm the expert, I know everything"*); believing *"the customer will see the product and a need will appear"*; chasing a rare / low-frequency Job for a small audience, or a Fake Job no one ever paid for; forgetting unit economics during segment search and picking a segment you can't be profitable in; drifting into the local optimum with no explicit decision.

## Where to read in the canon

When a task matches one of the situations below, **open that file and read the relevant part before answering.** Don't answer a methodology question from memory — the canon is the source of truth and your training data is not. Paths are inside the cloned **`Next-Move-Theory-Canon/`** (adjust the prefix to wherever you keep it). It is fine — expected — to open several files for one task.

### Start here / the whole model

- **`Advanced-Jobs-To-Be-Done/ajtbd-key-theses.md`** — the entire methodology in numbered theses; the map to every other file. **Read it when:** you're starting any product task and need the model loaded; you need the canonical definition of a Job, value, a segment, the Job Graph, the Aha Moment, a Solution, or a Consideration Activator; you're unsure which deeper file to open; you want to sanity-check that you're not drifting into generic JTBD.
- **`Next-Move-Theory/nmt-key-theses.md`** — the integrative root that joins AJTBD, Unit Economics, RAT, and ABCDX into one system. **Read it when:** the task spans more than one pillar; you need the cause-and-effect chain to profit; you're weighing a local-optimum vs a global-optimum move; you're aligning several functions (product, marketing, sales) on one strategy; someone treats marketing/pricing/product as separable ("the product is a single organism").

### The Job and the Job Graph

- **`Advanced-Jobs-To-Be-Done/job-structure.md`** — the eight elements of a single Job, element by element, with the interview question for each. **Read it when:** you're writing or auditing a Job statement; a Job is vague, abstract, or has multiple verbs; you need to fill in context, trigger, success criteria, or the higher-level Job; you're turning a feature request into a real Job.
- **`Advanced-Jobs-To-Be-Done/job-graph.md`** — the Job Graph and the four product-relative levels (Big / Core / Small / Micro). **Read it when:** you're mapping the Jobs around a product; you're unsure whether a Job is Core, Big, Small, or Micro; you're deciding where to climb a level or where to expand; you're hunting growth via sibling Small Jobs or the Previous/Next Job in a chain; someone asserts a universal "top" of the Graph.
- **`Advanced-Jobs-To-Be-Done/job-types-and-properties.md`** — the taxonomy (Regular, Orientation, Tax, Fake, Emotional, Viral) plus per-Job properties (frequency, conscious/unconscious). **Read it when:** you need to classify a Job; you suspect a **Fake Job** (a future fantasy nobody has paid for); you spot a **Tax Job** (forced rework from a broken chain); you're handling an emotional outcome ("I want to feel safe") or a viral/referral Job; you're deciding whether a Job is worth investing in at all.
- **`Advanced-Jobs-To-Be-Done/scientific-foundations.md`** — the neuroscience the methodology stands on (allostasis, prediction, reward prediction error, needs, emotions, habit, identity, loss aversion). **Read it when:** you need to justify *why* value is energy efficiency or *why* the Job (not the need) is the right unit; you're grounding a claim about emotion, habit, or decision-making; someone challenges the model's scientific basis; you're explaining the mechanism under the Aha Moment.

### Creating value

- **`Advanced-Jobs-To-Be-Done/value-creation.md`** — the deep canon on value. **Read it when:** you're designing or auditing value; you need to define or sharpen success criteria; you're deciding where the Aha Moment should fire; you're validating a value hypothesis; you're choosing which mechanic to lead with for this segment's priority order; you're diagnosing why real value isn't landing or isn't being noticed.
- **`Advanced-Jobs-To-Be-Done/value-creation-mechanics.md`** — the catalog of concrete value-creation mechanics. **Read it when:** you've fixed a segment + Core Job and need *moves* (not just "add a feature"); you want options beyond the obvious; you're looking for the highest-leverage mechanic for a specific bottleneck (conversion, retention, AOV, churn).
- **`Next-Move-Theory/subtraction.md`** — subtraction as the meta-operator across all four pillars, and the mechanism through which focus works. **Read it when:** the default impulse is to *add*; you're deciding what to remove (a Job, a feature, a segment, an assumption, a customer cohort); you need to argue *why* removing beats adding.
- **`Advanced-Jobs-To-Be-Done/critical-chain.md`** — the Job Graph projected onto time; where value is actually delivered. **Read it when:** you're designing the real product flow, onboarding, or activation; you're finding where customers drop off; you're placing the Aha Moment in the journey; you're diagnosing churn as a chain break; you're scaling into a sub-segment where the chain breaks because the new context is invisible from the old one.

### Reaching, converting, and keeping customers

- **`Advanced-Jobs-To-Be-Done/behaviour-change.md`** — switching as swapping one Job Graph for another; Solution-as-label; habit and fears. **Read it when:** a clearly-better product isn't being adopted; you're analyzing why customers won't switch; you're designing a switch or a migration; habit or fear is blocking adoption.
- **`Advanced-Jobs-To-Be-Done/consideration-activators.md`** — the five Consideration Activators and how to load them. **Read it when:** you're writing messaging/positioning that must change a choice; the customer doesn't yet know a better way exists; you're selling an unfamiliar or innovative product; you're deciding exactly what to write into the customer's head, and against which Big Job.
- **`Advanced-Jobs-To-Be-Done/barrier-removal.md`** — the objective barriers that make a better Job Graph non-executable for a segment. **Read it when:** a segment *cannot* adopt (missing prerequisite, unsupported state/ZIP/plan, compliance regime, missing integration, irreversible-loss risk); you're deciding whether to fix reality vs reduce a fear; you're entering a regulated or enterprise segment.
- **`Advanced-Jobs-To-Be-Done/customers-attention-management.md`** — attention as the metabolic resource every mechanism routes through. **Read it when:** value exists but customers don't notice it; you're designing onboarding/activation so attention doesn't drop before value lands; you're managing cognitive cost; you're deciding where to direct (and where to stop spending) the customer's attention.
- **`Advanced-Jobs-To-Be-Done/communication.md`** — communication in the language of Jobs: the value-proposition formula and the 9-block landing-page structure. **Read it when:** you're writing a landing page, ad, email, sales script, or one-line value proposition; you're positioning a product; messaging feels off-strategy; you're generating and testing creative variants.

### Choosing where to compete & validating before you build

- **`Advanced-Jobs-To-Be-Done/segmentation.md`** — segmentation by Job Graph similarity, and the mistakes. **Read it when:** you're defining or auditing a segment; you're tempted to cut by demographics / persona / ICP first; you're building a Map of Segments; you're choosing a target segment; you're turning segment criteria into lead-qualification questions; you catch yourself "interviewing the people who churned."
- **`ABCDX-Segmentation/abcdx-segmentation-key-theses.md`** — ABCDX (per-unit margin × satisfaction) on a paying base. **Read it when:** you already have paying customers and need to focus; loud, unprofitable customers are eating the roadmap; you're deciding whom to keep vs fire; you spot off-label or adjacent demand (the X class); you're choosing between a local-optimum and a global-optimum bet; you're running a marketplace (two ABCDXs).
- **`Riskiest-Assumption-Test/rat-key-theses.md`** — validating the riskiest assumptions before spending the build. **Read it when:** you're deciding whether to start, continue, pivot, or kill an initiative; you need to list an idea's assumptions and rank them; you're choosing the cheapest test for the deadliest risk; you're scoping an MVP/probe; you're deciding what *not* to build.

### Company focus, B2B, interviews, and the operating loop

- **`Next-Move-Theory/focus-as-company-attention-management.md`** — company-side focus as attention management. **Read it when:** the team is spread across too many segments/Jobs; you're deciding what to subtract at the company, portfolio, or team level; you're facing the Innovator's Dilemma; you're trying to sustain focus over time and fund a parallel global-optimum track.
- **`Advanced-Jobs-To-Be-Done/b2b.md`** — the B2B deal as a Job Graph across roles; personal vs business Jobs. **Read it when:** you're working on a B2B product or sale; deals stall with no clear reason; you're mapping the Jobs of the decision-maker, champion, IT, security, legal, and end users; you want to lift close rate, ACV, or renewal by performing the decision-maker's *personal* Jobs.
- **`HowTos/basic-ajtbd-interview-guide-and-principles.md`** — the practical interview guide: principles + question bank. **Read it when:** you're planning or running customer interviews; you're recruiting respondents (study people who *paid* in the past); you're writing the questions; you're reconstructing Jobs, criteria, the Consideration Set, the Critical Chain of Jobs, Aha Moments, and Barriers from what a customer actually did.
- **`Algorithms/the-algorithm.md`** — the integrative operating loop and its anti-patterns. **Read it when:** you need the order of operations; you're unsure what to do next; you're about to jump to a solution before diagnosing; you want the explicit list of anti-patterns to refuse.

## Default behavior

- When a request involves choosing what to build, who for, how to position, how to grow, or whether to build at all — frame it as **which Jobs of which segment**, then check the canon for the relevant mechanic before answering.
- State Job-level claims with the level named (Big / Core / Small / Micro), and keep Jobs as `I want to + verb`.
- Treat new features and new initiatives as **value hypotheses with risky assumptions** — name the riskiest one and the cheapest test before recommending a build.
- If you are unsure of a definition, **read the canon file rather than guessing from training data.**

Learn more at [nextmovetheory.com](http://nextmovetheory.com/?utm_source=canon&utm_medium=github).
<!-- Next-Move-Theory-Rules:end -->

# Consensus review loop (agent-agnostic instructions)

This file is for agents other than Claude Code (Codex, Cursor, Gemini CLI, etc.) that cannot load the plugin natively. Copy it into your project's agent instructions file or point your agent at it.

Rule: whenever you have formed a proposed solution (design, architecture choice, bug diagnosis + fix, refactor plan, non-trivial implementation approach), run it through this loop BEFORE presenting it as final.

1. Write the full problem context and the proposed solution to ONE temp file. Use `templates/context.md` as the structure. Pointers (`path:line`) plus targeted snippets, not whole files.
2. Spawn 1 to 3 independent reviewer agents (strongest model available; in Claude this is Fable). Give each the file and this prompt:

   "Critically review the solution to the problem suggested by our intern. Be critical: review it, verify it against the code, and suggest changes to the solution if it is not good enough. Do not over-engineer."

   Reviewer output format and rules are in `agents/consensus-reviewer.md`. 1 reviewer for small contained fixes, 2 for design decisions / cross-cutting refactors, 3 for architecture / data model / security / irreversible changes.
3. Treat reviewer findings as INTERN findings. Verify each against the code yourself. Reject wrong, out-of-scope, over-engineered, or constraint-violating findings. Accept verified findings that change the outcome. Code decides disagreements, not majority vote.
4. Fold accepted findings into the final solution. Present it, followed by a short "Consensus" receipt: reviewers spawned, accepted findings (one line each), rejected findings (one line each, with why), open questions for the user.

Skip for trivial one-line edits, pure informational answers, or when the user says "no consensus".
