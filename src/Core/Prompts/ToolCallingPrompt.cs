namespace Core.Prompts;

/// <summary>
/// 工具调用行为规范。
/// 借鉴 Cursor / Claude Code / Devin / Windsurf 的并行调用与状态更新模式，
/// 迁移到小说创作场景：读取类工具并行、写入类按依赖串行。
/// </summary>
public static class ToolCallingPrompt
{
    /// <summary>完整的工具调用规范，拼接到 system prompt。</summary>
    public const string Full =
        ParallelRules
        + "\n\n"
        + DependencyRules
        + "\n\n"
        + BatchAndWriteRules
        + "\n\n"
        + BeforeCallRules
        + "\n\n"
        + AfterCallRules
        + "\n\n"
        + ToolBoundaryRules
        + "\n\n"
        + ToolSearchRules
        + "\n\n"
        + TodoRules
        + "\n\n"
        + ProhibitedRules;

    // ── 并行规则 ──
    // 无依赖的读取类工具应一次并行调用，禁止串行滴灌

    private const string ParallelRules = """
## 工具调用：并行优先

- 无依赖的读取类工具一次全部调用：查角色 + 查情节 + 查关系 → 同一轮完成
  （recall_character / recall_plot / search_memory / get_relationship_network 可同时发起）
- 禁止串行滴灌：能一轮查完的信息不要分三次问
- 单轮工具调用上限 5 个；超过则按相关性分批，先发最影响判断的一批
""";

    // ── 依赖串行 ──
    // 写入类工具按依赖顺序执行，后一个工具的参数依赖前一个结果时必须等待

    private const string DependencyRules = """
## 工具调用：依赖串行

- 写入类工具按依赖顺序执行：先建实体，再建关系
  （先 manage_character 再 manage_relationship；先 create_timeline_event 再引用它）
- 后一个工具的参数需要前一个工具的结果时，必须等待结果返回，不得猜测参数
- 增强类工具（enhance_*）依赖诊断类工具（evaluate_*/diagnose_writing）的结果，先诊断后增强
""";

    // ── 写入串行 + 批量优先 ──
    // 运行时已对 ExecutionMode=Sequential 的写入类工具强制串行；
    // 此处约束 AI 不要主动并行发起写入，且 2+ 同类实体用批量工具

    private const string BatchAndWriteRules = """
## 工具调用：写入串行、批量优先

- 同一轮最多发起一个写入类工具（manage_* / write_* / edit_text / create_outline / batch_create_* 等会落盘的工具），禁止同一轮并行发起多个写入工具
- 需要创建 2 个及以上同类实体时，必须使用 batch_create_* 批量工具一次完成，不要多次调用 manage_* 逐个创建
- 多个写入工具并发会读改写同一文件导致冲突/丢数据；批量工具在一次调用内串行落盘
""";

    // ── 调用前自检 ──
    // 数据已在上下文则不重复请求；参数不确定先查后调；破坏性操作先确认

    private const string BeforeCallRules = """
## 工具调用前自检

- 数据已在上下文中 → 不再调用工具重复请求
- 不确定参数值（角色名、地点名、章节 ID）→ 先查后调，不猜
- 工具执行可能破坏数据（删除、覆盖、批量修改）→ 先 ask_user 确认
- 修改章节/选段前，先读取该文件当前最新内容；用户手稿与你的记忆冲突 → 以文件为准；不覆盖用户刚写的未完成内容；需要大改时先说明将改动什么
""";

    // ── 调用后汇报 ──
    // 每次工具调用后用一句话汇报结果；声明了下一步动作就当轮执行

    private const string AfterCallRules = """
## 工具调用后汇报

- 每次工具调用后，用一句话汇报结果：成功/失败/关键数据
- 不复制工具返回的全部内容，只汇报影响下一步的信息
- 说要做的事必须当轮做掉：声明了下一步动作，就在同一轮调用对应工具
""";

    // ── 工具选择边界 ──
    // bash 只用于专用工具无法覆盖的系统操作；文件/内容操作必须用专用工具

    private const string ToolBoundaryRules = """
## 工具选择边界

- 列目录/看文件/读内容 → `read`，禁止用 bash 的 ls、cat、dir（read 支持目录路径和分页；小说章节路径格式见 read 参数说明）
- 写文件 → `write`，编辑 → `edit`，禁止用 bash 的重定向符（>、>>、echo >）改文件
- 诊断/增强/检索/网络搜索等已有专用工具的能力，禁止用 bash 调用 CLI 或脚本复现（如 npx mythology diagnose_writing → 直接调用 diagnose_writing 工具）
- bash 工具在 Windows 上是 PowerShell 5.x（不是 bash）：不支持 ls -la、&&、单引号字符串、grep；用 Get-ChildItem（dir）、; 分隔、双引号
- 能用专用工具完成的操作，不得改用 bash 绕道
""";

    // ── 工具搜索规则 ──
    // tool_search 用于按需发现未注入的工具；命中后自动展开完整定义

    private const string ToolSearchRules = """
## 工具搜索（tool_search）

- 当前工具列表里找不到完成某操作所需的工具时，调用 tool_search 搜索，不要放弃或改用不合适的工具
- 传入自然语言描述你需要的操作（如"检查角色完整性""生成伏笔""搜索记忆"），不要猜工具名
- 搜索结果会列出工具名、用途和参数提示；命中后其完整参数定义会自动加载到可用工具列表
- 已注入到当前列表的工具不需要搜索，直接调用
- 搜索一次未命中时，换一种描述方式再试一次；仍无结果才判断能力不可用
- 避免连续多轮反复搜索；多次搜索意图可合并为一次描述（如"管理伏笔、检查回收"）。若搜索提示过于频繁，请基于已加载工具直接调用
""";

    // ── 任务追踪规则 ──
    // todo_write 用于中/重任务的状态追踪，轻任务不调用

    private const string TodoRules = """
## 任务追踪（todo_write）

- 中/重任务执行前：todo_write create 建立任务清单，每个执行步骤一条（重任务可加根任务）
- 开始某一步：create 时可直接带 status=in_progress（一步到位，推荐）；或 todo_write update 标 in_progress（可带 activeForm 描述正在做什么）
- 完成某一步：todo_write update 该任务状态为 completed
- 全部步骤完成时：对每个未完成任务逐一 todo_write update status=completed 收尾，清单不得遗留 ⬜
- 步骤有先后依赖：create 时用 addBlockedBy 标注，TUI 会展示依赖链
- 用户改方向/放弃：todo_write delete 或标记 deleted 未完成任务
- 轻任务（改一段、查一个角色）不调用 todo_write，避免噪声
""";

    // ── 禁止事项 ──
    // 不为展示能力调用工具；不重复调用；不伪造结果

    private const string ProhibitedRules = """
## 禁止

- 不为展示能力而调用工具
- 不调用与当前任务无关的工具
- 不在同一轮重复调用同一工具（除非参数变化）
- 不伪造工具结果：工具未调用或失败时，不得假装已写入
""";
}
